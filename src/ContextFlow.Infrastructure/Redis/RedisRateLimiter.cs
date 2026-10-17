using System.Collections.Concurrent;
using ContextFlow.Core.Interfaces;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ContextFlow.Infrastructure.Redis;

/// <summary>
/// Distributed rate limiter governing tool invocations using a Redis Sorted Set sliding-window algorithm,
/// with automatic failover to an in-memory sliding-window log when Redis is absent, disconnected, or throws.
/// </summary>
public sealed partial class RedisRateLimiter(
    ILogger<RedisRateLimiter> logger,
    IConnectionMultiplexer? connection = null,
    TimeProvider? timeProvider = null) : IRateLimiter
{
    private const long _windowDurationMs = 60_000;
    private const long _pexpireMs = 65_000;
    private const string _keyPrefix = "contextflow:ratelimit:";

    private const string _slidingWindowScript = """
        local key = KEYS[1]
        local now = tonumber(ARGV[1])
        local window = tonumber(ARGV[2])
        local maxRequests = tonumber(ARGV[3])
        local member = ARGV[4]
        local pexpireMs = tonumber(ARGV[5])

        local windowStart = now - window

        -- Remove entries older than or equal to windowStart (now - 60s)
        redis.call('ZREMRANGEBYSCORE', key, '-inf', windowStart)

        local currentCount = redis.call('ZCARD', key)
        if currentCount < maxRequests then
            redis.call('ZADD', key, now, member)
            redis.call('PEXPIRE', key, pexpireMs)
            local remaining = maxRequests - currentCount - 1
            return { 1, remaining, 0 }
        else
            local oldest = redis.call('ZRANGE', key, 0, 0, 'WITHSCORES')
            local retryAfter = math.ceil(window / 1000)
            if #oldest >= 2 then
                local oldestScore = tonumber(oldest[2])
                if oldestScore then
                    local diff = oldestScore + window - now
                    if diff > 0 then
                        retryAfter = math.ceil(diff / 1000)
                    else
                        retryAfter = 1
                    end
                end
            end
            if retryAfter < 1 then
                retryAfter = 1
            end
            return { 0, 0, retryAfter }
        end
        """;

    private readonly IConnectionMultiplexer? _connection = connection;
    private readonly ILogger<RedisRateLimiter> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, SlidingWindowBucket> _inMemoryBuckets = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<RateLimitResult> CheckRateLimitAsync(
        string userId,
        string toolName,
        int maxRequestsPerMinute,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRequestsPerMinute);

        cancellationToken.ThrowIfCancellationRequested();

        var normalizedUserId = userId.Trim();
        var normalizedToolName = toolName.Trim().ToLowerInvariant();
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var key = $"{_keyPrefix}{normalizedUserId}:{normalizedToolName}";

        if (_connection is not null && _connection.IsConnected)
        {
            try
            {
                var db = _connection.GetDatabase();
                var keys = new RedisKey[] { key };
                var member = $"{nowMs}:{Guid.NewGuid():N}";
                var values = new RedisValue[]
                {
                    nowMs,
                    _windowDurationMs,
                    maxRequestsPerMinute,
                    member,
                    _pexpireMs
                };

                var redisResult = await db.ScriptEvaluateAsync(_slidingWindowScript, keys, values)
                    .WaitAsync(cancellationToken);

                if (redisResult is not null && !redisResult.IsNull)
                {
                    var array = (RedisResult[]?)redisResult;
                    if (array is not null && array.Length >= 3)
                    {
                        var isAllowed = ParseRedisInt(array[0]) == 1;
                        var remaining = ParseRedisInt(array[1]);
                        var retryAfter = ParseRedisInt(array[2]);
                        return new RateLimitResult(isAllowed, remaining, retryAfter);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                LogRedisException(_logger, ex, normalizedUserId, normalizedToolName);
            }
        }
        else
        {
            LogRedisUnavailable(_logger, normalizedUserId, normalizedToolName);
        }

        return CheckInMemory(key, nowMs, maxRequestsPerMinute);
    }

    private RateLimitResult CheckInMemory(string key, long nowMs, int maxRequestsPerMinute)
    {
        try
        {
            MaybePruneBuckets(nowMs);
            var bucket = _inMemoryBuckets.GetOrAdd(key, _ => new SlidingWindowBucket());
            return bucket.Check(nowMs, maxRequestsPerMinute, _windowDurationMs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDegradedFallbackFailed(_logger, ex, key);
            // Fail open per context.md §6.10
            return new RateLimitResult(IsAllowed: true, RemainingRequests: maxRequestsPerMinute, RetryAfterSeconds: 0);
        }
    }

    private void MaybePruneBuckets(long nowMs)
    {
        if (_inMemoryBuckets.Count <= 1000)
        {
            return;
        }

        var windowStart = nowMs - _windowDurationMs;
        foreach (var (k, bucket) in _inMemoryBuckets)
        {
            if (bucket.IsEmptyOrExpired(windowStart))
            {
                _inMemoryBuckets.TryRemove(k, out _);
            }
        }
    }

    private static int ParseRedisInt(RedisResult result, int fallback = 0)
    {
        try
        {
            return (int)result;
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            return int.TryParse(result.ToString(), out var parsed) ? parsed : fallback;
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Redis connection is unavailable. Operating rate limiter in degraded in-memory mode for user '{UserId}', tool '{ToolName}'.")]
    private static partial void LogRedisUnavailable(ILogger logger, string userId, string toolName);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Redis rate limit check failed for user '{UserId}', tool '{ToolName}'. Falling back to degraded in-memory mode.")]
    private static partial void LogRedisException(ILogger logger, Exception ex, string userId, string toolName);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Error,
        Message = "In-memory rate limit fallback evaluation failed for key '{Key}'. Failing open per degraded mode policy.")]
    private static partial void LogDegradedFallbackFailed(ILogger logger, Exception ex, string key);

    /// <summary>
    /// Thread-safe in-memory sliding-window log bucket.
    /// </summary>
    private sealed class SlidingWindowBucket
    {
        private readonly Lock _gate = new();
        private readonly List<long> _timestamps = [];

        public RateLimitResult Check(long nowMs, int maxRequestsPerMinute, long windowMs)
        {
            lock (_gate)
            {
                var windowStart = nowMs - windowMs;
                _timestamps.RemoveAll(t => t <= windowStart);

                if (_timestamps.Count < maxRequestsPerMinute)
                {
                    _timestamps.Add(nowMs);
                    var remaining = maxRequestsPerMinute - _timestamps.Count;
                    return new RateLimitResult(IsAllowed: true, RemainingRequests: remaining, RetryAfterSeconds: 0);
                }

                var oldest = _timestamps.Count > 0 ? _timestamps[0] : (nowMs - windowMs);
                var diff = oldest + windowMs - nowMs;
                var retryAfter = diff > 0 ? (int)Math.Ceiling(diff / 1000.0) : 1;
                if (retryAfter < 1)
                {
                    retryAfter = 1;
                }

                return new RateLimitResult(IsAllowed: false, RemainingRequests: 0, RetryAfterSeconds: retryAfter);
            }
        }

        public bool IsEmptyOrExpired(long windowStart)
        {
            lock (_gate)
            {
                return _timestamps.Count == 0 || _timestamps[^1] <= windowStart;
            }
        }
    }
}

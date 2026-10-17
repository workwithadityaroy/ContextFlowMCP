using System.Collections.Concurrent;
using ContextFlow.Core.Interfaces;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ContextFlow.Infrastructure.Redis;

/// <summary>
/// Distributed result cache for tool execution outcomes backed by Redis,
/// with seamless failover to thread-safe in-memory caching during Redis downtime or errors.
/// </summary>
public sealed partial class RedisResultCache(
    ILogger<RedisResultCache> logger,
    IConnectionMultiplexer? connection = null,
    TimeProvider? timeProvider = null) : IResultCache
{
    private const int _maxInMemoryEntriesBeforePrune = 1000;

    private readonly ILogger<RedisResultCache> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IConnectionMultiplexer? _connection = connection;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (string Value, DateTimeOffset ExpiresAt)> _inMemoryCache = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(key))
        {
            LogInvalidKey(_logger);
            return null;
        }

        try
        {
            if (_connection is not null && _connection.IsConnected)
            {
                try
                {
                    var db = _connection.GetDatabase();
                    var redisValue = await db.StringGetAsync(key).WaitAsync(cancellationToken);
                    if (redisValue.HasValue)
                    {
                        return (string?)redisValue;
                    }

                    RemoveFromInMemory(key);
                    return null;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is RedisException or TimeoutException)
                {
                    LogRedisGetFailed(_logger, ex, key);
                }
            }
            else
            {
                LogRedisUnavailable(_logger, key);
            }

            return GetFromInMemory(key);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogDegradedGetFailed(_logger, ex, key);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(key))
        {
            LogInvalidKey(_logger);
            return;
        }

        if (value is null)
        {
            LogInvalidValue(_logger, key);
            return;
        }

        if (ttl <= TimeSpan.Zero)
        {
            await RemoveAsync(key, cancellationToken);
            return;
        }

        try
        {
            SetInMemory(key, value, ttl);

            if (_connection is not null && _connection.IsConnected)
            {
                try
                {
                    var db = _connection.GetDatabase();
                    await db.StringSetAsync(key, value, ttl).WaitAsync(cancellationToken);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is RedisException or TimeoutException)
                {
                    LogRedisSetFailed(_logger, ex, key);
                }
            }
            else
            {
                LogRedisUnavailable(_logger, key);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogDegradedSetFailed(_logger, ex, key);
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(key))
        {
            LogInvalidKey(_logger);
            return;
        }

        try
        {
            RemoveFromInMemory(key);

            if (_connection is not null && _connection.IsConnected)
            {
                try
                {
                    var db = _connection.GetDatabase();
                    await db.KeyDeleteAsync(key).WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is RedisException or TimeoutException)
                {
                    LogRedisRemoveFailed(_logger, ex, key);
                }
            }
            else
            {
                LogRedisUnavailable(_logger, key);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogDegradedRemoveFailed(_logger, ex, key);
        }
    }

    private string? GetFromInMemory(string key)
    {
        MaybePruneExpired();

        if (_inMemoryCache.TryGetValue(key, out var entry))
        {
            if (_timeProvider.GetUtcNow() < entry.ExpiresAt)
            {
                return entry.Value;
            }

            _inMemoryCache.TryRemove(key, out _);
        }

        return null;
    }

    private void SetInMemory(string key, string value, TimeSpan ttl)
    {
        MaybePruneExpired();
        var expiresAt = _timeProvider.GetUtcNow().Add(ttl);
        _inMemoryCache[key] = (value, expiresAt);
    }

    private void RemoveFromInMemory(string key) => _inMemoryCache.TryRemove(key, out _);

    private void MaybePruneExpired()
    {
        if (_inMemoryCache.Count <= _maxInMemoryEntriesBeforePrune)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        foreach (var (k, entry) in _inMemoryCache)
        {
            if (now >= entry.ExpiresAt)
            {
                _inMemoryCache.TryRemove(k, out _);
            }
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Redis connection is unavailable for key '{Key}'. Falling back to degraded in-memory cache.")]
    private static partial void LogRedisUnavailable(ILogger logger, string key);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Redis GetAsync operation failed for key '{Key}'. Falling back to degraded in-memory cache.")]
    private static partial void LogRedisGetFailed(ILogger logger, Exception ex, string key);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Redis SetAsync operation failed for key '{Key}'. Falling back to degraded in-memory cache.")]
    private static partial void LogRedisSetFailed(ILogger logger, Exception ex, string key);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Warning,
        Message = "Redis RemoveAsync operation failed for key '{Key}'.")]
    private static partial void LogRedisRemoveFailed(ILogger logger, Exception ex, string key);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Warning,
        Message = "Degraded in-memory cache Get operation failed for key '{Key}'. Returning null.")]
    private static partial void LogDegradedGetFailed(ILogger logger, Exception ex, string key);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Warning,
        Message = "Degraded in-memory cache Set operation failed for key '{Key}'.")]
    private static partial void LogDegradedSetFailed(ILogger logger, Exception ex, string key);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Warning,
        Message = "Degraded in-memory cache Remove operation failed for key '{Key}'.")]
    private static partial void LogDegradedRemoveFailed(ILogger logger, Exception ex, string key);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Warning,
        Message = "Cache key cannot be null or whitespace.")]
    private static partial void LogInvalidKey(ILogger logger);

    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Warning,
        Message = "Cache value cannot be null for key '{Key}'.")]
    private static partial void LogInvalidValue(ILogger logger, string key);
}

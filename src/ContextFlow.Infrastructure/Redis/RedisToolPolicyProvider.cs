using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContextFlow.Core.Entities;
using ContextFlow.Core.Interfaces;
using ContextFlow.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ContextFlow.Infrastructure.Redis;

/// <summary>
/// Implements 2-tier caching for tool governance policies using Redis as the distributed cache tier,
/// an in-memory ConcurrentDictionary fallback for degraded/offline modes, and PostgreSQL as the authoritative source.
/// </summary>
public sealed partial class RedisToolPolicyProvider(
    ContextFlowDbContext dbContext,
    ILogger<RedisToolPolicyProvider> logger,
    IConnectionMultiplexer? redis = null,
    ConcurrentDictionary<string, ToolPolicy>? inMemoryCache = null) : IToolPolicyProvider
{
    private const string _policyKeyPrefix = "contextflow:policy:";
    private static readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ContextFlowDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly ILogger<RedisToolPolicyProvider> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IConnectionMultiplexer? _redis = redis;
    private readonly ConcurrentDictionary<string, ToolPolicy> _inMemoryCache = inMemoryCache ?? new ConcurrentDictionary<string, ToolPolicy>(StringComparer.OrdinalIgnoreCase);

    public async Task<ToolPolicy?> GetPolicyAsync(string toolName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedToolName = toolName.Trim().ToLowerInvariant();
        var redisKey = _policyKeyPrefix + normalizedToolName;
        var redisFailedOrUnavailable = _redis is null || !_redis.IsConnected;

        // 1. Try Redis cache if available
        if (!redisFailedOrUnavailable)
        {
            try
            {
                var db = _redis!.GetDatabase();
                var value = await db.StringGetAsync(redisKey);
                if (value.HasValue)
                {
                    var cachedPolicy = JsonSerializer.Deserialize<ToolPolicy>((string)value!, _serializerOptions);
                    if (cachedPolicy is not null)
                    {
                        _inMemoryCache[normalizedToolName] = cachedPolicy;
                        return cachedPolicy;
                    }
                }
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                redisFailedOrUnavailable = true;
                LogRedisPolicyGetFailed(_logger, ex, normalizedToolName);
            }
        }

        // 2. Fall back to in-memory cache if Redis is offline/unavailable or threw
        if (redisFailedOrUnavailable && _inMemoryCache.TryGetValue(normalizedToolName, out var inMemoryPolicy))
        {
            return inMemoryPolicy;
        }

        // 3. Query authoritative database with AsNoTracking()
        ToolPolicy? policy;
        try
        {
            policy = await _dbContext.ToolPolicies
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.ToolName == normalizedToolName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && _inMemoryCache.TryGetValue(normalizedToolName, out var staleFallback))
        {
            LogDatabaseQueryFailedUsingFallback(_logger, ex, normalizedToolName);
            return staleFallback;
        }

        if (policy is null)
        {
            return null;
        }

        // Update in-memory fallback
        _inMemoryCache[normalizedToolName] = policy;

        // Update Redis cache with 5-minute TTL if Redis is available
        if (_redis is not null && _redis.IsConnected)
        {
            try
            {
                var db = _redis.GetDatabase();
                var json = JsonSerializer.Serialize(policy, _serializerOptions);
                await db.StringSetAsync(redisKey, json, _cacheTtl);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                LogRedisPolicySetFailed(_logger, ex, normalizedToolName);
            }
        }

        return policy;
    }

    public async Task<IReadOnlyDictionary<string, ToolPolicy>> GetAllPoliciesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var policies = await _dbContext.ToolPolicies
                .AsNoTracking()
                .ToDictionaryAsync(p => p.ToolName, StringComparer.OrdinalIgnoreCase, cancellationToken);

            foreach (var (toolName, policy) in policies)
            {
                _inMemoryCache[toolName.Trim().ToLowerInvariant()] = policy;
            }

            if (_redis is not null && _redis.IsConnected)
            {
                try
                {
                    var db = _redis.GetDatabase();
                    foreach (var (toolName, policy) in policies)
                    {
                        var redisKey = _policyKeyPrefix + toolName.Trim().ToLowerInvariant();
                        var json = JsonSerializer.Serialize(policy, _serializerOptions);
                        await db.StringSetAsync(redisKey, json, _cacheTtl);
                    }
                }
                catch (Exception ex) when (ex is RedisException or TimeoutException)
                {
                    LogRedisPolicySetBatchFailed(_logger, ex);
                }
            }

            return policies;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !_inMemoryCache.IsEmpty)
        {
            LogDatabaseQueryAllFailedUsingFallback(_logger, ex);
            return new Dictionary<string, ToolPolicy>(_inMemoryCache, StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task InvalidatePolicyCacheAsync(string toolName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedToolName = toolName.Trim().ToLowerInvariant();
        _inMemoryCache.TryRemove(normalizedToolName, out _);

        if (_redis is not null && _redis.IsConnected)
        {
            try
            {
                var db = _redis.GetDatabase();
                var redisKey = _policyKeyPrefix + normalizedToolName;
                await db.KeyDeleteAsync(redisKey);
                LogRedisPolicyInvalidated(_logger, normalizedToolName);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                LogRedisPolicyInvalidateFailed(_logger, ex, normalizedToolName);
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to retrieve tool policy for {ToolName} from Redis; falling back to cache/database")]
    private static partial void LogRedisPolicyGetFailed(ILogger logger, Exception ex, string toolName);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Failed to cache tool policy for {ToolName} in Redis")]
    private static partial void LogRedisPolicySetFailed(ILogger logger, Exception ex, string toolName);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Invalidated Redis cache for tool policy {ToolName}")]
    private static partial void LogRedisPolicyInvalidated(ILogger logger, string toolName);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Failed to invalidate Redis cache for tool policy {ToolName}")]
    private static partial void LogRedisPolicyInvalidateFailed(ILogger logger, Exception ex, string toolName);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Database query for tool policy {ToolName} failed; falling back to in-memory cached policy")]
    private static partial void LogDatabaseQueryFailedUsingFallback(ILogger logger, Exception ex, string toolName);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Failed to cache all tool policies in Redis")]
    private static partial void LogRedisPolicySetBatchFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Database query for all tool policies failed; falling back to in-memory cached policies")]
    private static partial void LogDatabaseQueryAllFailedUsingFallback(ILogger logger, Exception ex);
}

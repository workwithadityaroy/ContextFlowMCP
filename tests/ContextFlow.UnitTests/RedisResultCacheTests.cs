using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using ContextFlow.Infrastructure.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace ContextFlow.UnitTests;

public sealed class RedisResultCacheTests
{
    private sealed class TestTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        private DateTimeOffset _currentTime = initialTime;

        public override DateTimeOffset GetUtcNow() => _currentTime;

        public void Advance(TimeSpan delta) => _currentTime = _currentTime.Add(delta);
    }

    [Fact]
    public async Task BasicSetAndGetWithInMemoryFallbackSucceeds()
    {
        // Arrange: connection = null triggers in-memory fallback
        var cache = new RedisResultCache(
            logger: NullLogger<RedisResultCache>.Instance,
            connection: null);

        const string key = "tool:test:user1";
        const string expectedValue = "{\"result\":\"ok\"}";

        // Act
        await cache.SetAsync(key, expectedValue, TimeSpan.FromMinutes(5));
        var actualValue = await cache.GetAsync(key);

        // Assert
        Assert.Equal(expectedValue, actualValue);
    }

    [Fact]
    public async Task GetAsyncReturnsNullForMissingKey()
    {
        // Arrange
        var cache = new RedisResultCache(
            logger: NullLogger<RedisResultCache>.Instance,
            connection: null);

        // Act
        var result = await cache.GetAsync("non-existent-key");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task TtlExpirationWithTestTimeProviderExpiresEntry()
    {
        // Arrange
        var initialTime = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(initialTime);
        var cache = new RedisResultCache(
            logger: NullLogger<RedisResultCache>.Instance,
            connection: null,
            timeProvider: timeProvider);

        const string key = "transient-key";
        const string value = "transient-value";
        var ttl = TimeSpan.FromSeconds(30);

        // Act & Assert 1: Store and verify item is immediately retrievable
        await cache.SetAsync(key, value, ttl);
        var beforeExpiry = await cache.GetAsync(key);
        Assert.Equal(value, beforeExpiry);

        // Act & Assert 2: Advance time just before expiration (29s) -> still valid
        timeProvider.Advance(TimeSpan.FromSeconds(29));
        var justBeforeExpiry = await cache.GetAsync(key);
        Assert.Equal(value, justBeforeExpiry);

        // Act & Assert 3: Advance time past expiration (2s more -> 31s total) -> expired
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        var expiredResult = await cache.GetAsync(key);
        Assert.Null(expiredResult);

        // Act & Assert 4: Subsequent retrieval confirms eviction
        var secondFetch = await cache.GetAsync(key);
        Assert.Null(secondFetch);
    }

    [Fact]
    public async Task RemoveAsyncEvictsCachedEntry()
    {
        // Arrange
        var cache = new RedisResultCache(
            logger: NullLogger<RedisResultCache>.Instance,
            connection: null);

        const string key = "evict-key";
        await cache.SetAsync(key, "evict-value", TimeSpan.FromMinutes(5));

        var beforeRemove = await cache.GetAsync(key);
        Assert.Equal("evict-value", beforeRemove);

        // Act
        await cache.RemoveAsync(key);

        // Assert
        var afterRemove = await cache.GetAsync(key);
        Assert.Null(afterRemove);
    }

    [Fact]
    public async Task FallbackWhenRedisThrowsRedisServerException()
    {
        // Arrange: Multiplexer that throws RedisServerException on all database operations
        var throwingMultiplexer = ThrowingMultiplexerProxy.Create();
        var cache = new RedisResultCache(
            logger: NullLogger<RedisResultCache>.Instance,
            connection: throwingMultiplexer);

        const string key = "throwing-redis-key";
        const string value = "resilient-payload";

        // Act 1: SetAsync should catch RedisServerException and seamlessly store in in-memory fallback
        await cache.SetAsync(key, value, TimeSpan.FromMinutes(5));

        // Act 2: GetAsync should catch RedisServerException and retrieve from in-memory fallback
        var retrieved = await cache.GetAsync(key);

        // Assert
        Assert.Equal(value, retrieved);

        // Act 3: RemoveAsync should catch RedisServerException, remove in-memory entry, and not throw
        await cache.RemoveAsync(key);
        var afterRemove = await cache.GetAsync(key);
        Assert.Null(afterRemove);
    }

    [Fact]
    public async Task FallbackWhenRedisThrowsTimeoutException()
    {
        // Arrange: Multiplexer that throws TimeoutException
        var timeoutMultiplexer = TimeoutMultiplexerProxy.Create();
        var cache = new RedisResultCache(
            logger: NullLogger<RedisResultCache>.Instance,
            connection: timeoutMultiplexer);

        const string key = "timeout-redis-key";
        const string value = "timeout-payload";

        // Act: Set and Get should fall back to in-memory mode without throwing
        await cache.SetAsync(key, value, TimeSpan.FromMinutes(5));
        var retrieved = await cache.GetAsync(key);

        // Assert
        Assert.Equal(value, retrieved);
    }

    [Fact]
    public void HasSinglePublicConstructorForUnambiguousDependencyInjection()
    {
        var constructors = typeof(RedisResultCache).GetConstructors();
        Assert.Single(constructors);
    }

    [Fact]
    public async Task PrunesExpiredEntriesWhenCountExceedsThreshold()
    {
        // Arrange
        var initialTime = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(initialTime);
        var cache = new RedisResultCache(
            logger: NullLogger<RedisResultCache>.Instance,
            connection: null,
            timeProvider: timeProvider);

        // Populate 1005 entries with 1-second TTL
        for (var i = 0; i < 1005; i++)
        {
            await cache.SetAsync($"batch-key-{i}", $"value-{i}", TimeSpan.FromSeconds(1));
        }

        // Advance time so all 1005 entries expire
        timeProvider.Advance(TimeSpan.FromSeconds(5));

        // Adding another entry triggers pruning
        const string activeKey = "active-entry";
        const string activeValue = "active-val";
        await cache.SetAsync(activeKey, activeValue, TimeSpan.FromMinutes(10));

        // Assert: New entry is present, expired entries are purged
        var activeResult = await cache.GetAsync(activeKey);
        Assert.Equal(activeValue, activeResult);

        var expiredCheck = await cache.GetAsync("batch-key-0");
        Assert.Null(expiredCheck);
    }

    [Fact]
    public async Task CancellationTokenHonorsCancellation()
    {
        // Arrange
        var cache = new RedisResultCache(
            logger: NullLogger<RedisResultCache>.Instance,
            connection: null);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cache.GetAsync("key", cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cache.SetAsync("key", "val", TimeSpan.FromMinutes(1), cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cache.RemoveAsync("key", cts.Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FailOpenOnInvalidKeyReturnsNullOrNoOpsWithoutThrowing(string? invalidKey)
    {
        // Arrange
        var cache = new RedisResultCache(
            logger: NullLogger<RedisResultCache>.Instance,
            connection: null);

        // Act & Assert
        var getResult = await cache.GetAsync(invalidKey!);
        Assert.Null(getResult);

        // Should not throw on SetAsync or RemoveAsync
        await cache.SetAsync(invalidKey!, "val", TimeSpan.FromMinutes(1));
        await cache.RemoveAsync(invalidKey!);
    }

    [Fact]
    public async Task FailOpenOnNullValueNoOpsWithoutThrowing()
    {
        // Arrange
        var cache = new RedisResultCache(
            logger: NullLogger<RedisResultCache>.Instance,
            connection: null);

        // Act & Assert: Should not throw
        await cache.SetAsync("valid-key", null!, TimeSpan.FromMinutes(1));
        var result = await cache.GetAsync("valid-key");
        Assert.Null(result);
    }

    [Fact]
    public async Task NonPositiveTtlRemovesKeyImmediately()
    {
        // Arrange
        var cache = new RedisResultCache(
            logger: NullLogger<RedisResultCache>.Instance,
            connection: null);

        await cache.SetAsync("zero-ttl-key", "initial", TimeSpan.FromMinutes(5));
        Assert.Equal("initial", await cache.GetAsync("zero-ttl-key"));

        // Act: Setting with TimeSpan.Zero should remove entry
        await cache.SetAsync("zero-ttl-key", "updated", TimeSpan.Zero);

        // Assert
        Assert.Null(await cache.GetAsync("zero-ttl-key"));
    }

    [Fact]
    public async Task SuccessfulRedisOperationsWhenRedisIsConnectedAndHealthy()
    {
        // Arrange
        var healthyDb = WorkingDatabaseProxy.Create();
        var healthyMultiplexer = WorkingMultiplexerProxy.Create(healthyDb);
        var cache = new RedisResultCache(
            logger: NullLogger<RedisResultCache>.Instance,
            connection: healthyMultiplexer);

        const string key = "redis-live-key";
        const string val = "redis-live-value";

        // Act & Assert: Set, Get, Remove via healthy Redis
        await cache.SetAsync(key, val, TimeSpan.FromMinutes(5));
        var retrieved = await cache.GetAsync(key);
        Assert.Equal(val, retrieved);

        await cache.RemoveAsync(key);
        var afterRemove = await cache.GetAsync(key);
        Assert.Null(afterRemove);
    }

    [SuppressMessage("Performance", "CA1852:Seal internal types", Justification = "DispatchProxy requires non-sealed types.")]
    private class ThrowingMultiplexerProxy : DispatchProxy
    {
        public static IConnectionMultiplexer Create() =>
            DispatchProxy.Create<IConnectionMultiplexer, ThrowingMultiplexerProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_IsConnected")
            {
                return true;
            }

            if (targetMethod?.Name == "GetDatabase")
            {
                return ThrowingDatabaseProxy.Create();
            }

            return null;
        }
    }

    [SuppressMessage("Performance", "CA1852:Seal internal types", Justification = "DispatchProxy requires non-sealed types.")]
    private class ThrowingDatabaseProxy : DispatchProxy
    {
        public static IDatabase Create() =>
            DispatchProxy.Create<IDatabase, ThrowingDatabaseProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name.Contains("StringGet") == true)
            {
                throw new RedisServerException("ERR Redis read failed");
            }

            if (targetMethod?.Name.Contains("StringSet") == true)
            {
                throw new RedisServerException("ERR Redis write failed");
            }

            if (targetMethod?.Name.Contains("KeyDelete") == true)
            {
                throw new RedisServerException("ERR Redis delete failed");
            }

            return null;
        }
    }

    [SuppressMessage("Performance", "CA1852:Seal internal types", Justification = "DispatchProxy requires non-sealed types.")]
    private class TimeoutMultiplexerProxy : DispatchProxy
    {
        public static IConnectionMultiplexer Create() =>
            DispatchProxy.Create<IConnectionMultiplexer, TimeoutMultiplexerProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_IsConnected")
            {
                return true;
            }

            if (targetMethod?.Name == "GetDatabase")
            {
                return TimeoutDatabaseProxy.Create();
            }

            return null;
        }
    }

    [SuppressMessage("Performance", "CA1852:Seal internal types", Justification = "DispatchProxy requires non-sealed types.")]
    private class TimeoutDatabaseProxy : DispatchProxy
    {
        public static IDatabase Create() =>
            DispatchProxy.Create<IDatabase, TimeoutDatabaseProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name.Contains("StringGet") == true)
            {
                throw new TimeoutException("The Redis operation timed out.");
            }

            if (targetMethod?.Name.Contains("StringSet") == true)
            {
                throw new TimeoutException("The Redis operation timed out.");
            }

            if (targetMethod?.Name.Contains("KeyDelete") == true)
            {
                throw new TimeoutException("The Redis operation timed out.");
            }

            return null;
        }
    }

    [SuppressMessage("Performance", "CA1852:Seal internal types", Justification = "DispatchProxy requires non-sealed types.")]
    private class WorkingMultiplexerProxy : DispatchProxy
    {
        private IDatabase? _database;

        public static IConnectionMultiplexer Create(IDatabase database)
        {
            var proxy = DispatchProxy.Create<IConnectionMultiplexer, WorkingMultiplexerProxy>();
            ((WorkingMultiplexerProxy)(object)proxy)._database = database;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_IsConnected")
            {
                return true;
            }

            if (targetMethod?.Name == "GetDatabase")
            {
                return _database;
            }

            return null;
        }
    }

    [SuppressMessage("Performance", "CA1852:Seal internal types", Justification = "DispatchProxy requires non-sealed types.")]
    private class WorkingDatabaseProxy : DispatchProxy
    {
        private readonly Dictionary<string, string> _store = new(StringComparer.Ordinal);

        public static IDatabase Create() =>
            DispatchProxy.Create<IDatabase, WorkingDatabaseProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name.Contains("StringGet") == true && args is not null && args.Length > 0)
            {
                var key = args[0]?.ToString() ?? string.Empty;
                if (_store.TryGetValue(key, out var val))
                {
                    return Task.FromResult((RedisValue)val);
                }

                return Task.FromResult(RedisValue.Null);
            }

            if (targetMethod?.Name.Contains("StringSet") == true && args is not null && args.Length > 1)
            {
                var key = args[0]?.ToString() ?? string.Empty;
                var val = args[1]?.ToString() ?? string.Empty;
                _store[key] = val;
                return Task.FromResult(true);
            }

            if (targetMethod?.Name.Contains("KeyDelete") == true && args is not null && args.Length > 0)
            {
                var key = args[0]?.ToString() ?? string.Empty;
                _store.Remove(key);
                return Task.FromResult(true);
            }

            return null;
        }
    }
}

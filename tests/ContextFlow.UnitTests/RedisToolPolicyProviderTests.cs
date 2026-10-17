using System.Collections.Concurrent;
using ContextFlow.Core.Entities;
using ContextFlow.Core.Enums;
using ContextFlow.Infrastructure.Data;
using ContextFlow.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContextFlow.UnitTests;

public sealed class RedisToolPolicyProviderTests
{
    private static (RedisToolPolicyProvider provider, ContextFlowDbContext db, ConcurrentDictionary<string, ToolPolicy> cache) CreateProvider()
    {
        var options = new DbContextOptionsBuilder<ContextFlowDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var db = new ContextFlowDbContext(options);
        db.Database.EnsureCreated();

        var isolatedCache = new ConcurrentDictionary<string, ToolPolicy>(StringComparer.OrdinalIgnoreCase);
        var provider = new RedisToolPolicyProvider(
            db,
            NullLogger<RedisToolPolicyProvider>.Instance,
            redis: null,
            inMemoryCache: isolatedCache);

        return (provider, db, isolatedCache);
    }

    [Fact]
    public async Task GetPolicyAsyncWhenRedisIsNullFetchesFromDatabase()
    {
        // Arrange
        var (provider, _, _) = CreateProvider();

        // Act
        var policy = await provider.GetPolicyAsync("echo");

        // Assert
        Assert.NotNull(policy);
        Assert.Equal("echo", policy.ToolName);
        Assert.True(policy.IsEnabled);
        Assert.Equal(60, policy.MaxRequestsPerMinute);
        Assert.Equal(0, policy.CacheTtlSeconds);
        Assert.Equal(AuditMode.Full, policy.AuditMode);
    }

    [Fact]
    public async Task GetPolicyAsyncWhenToolNotFoundInDatabaseReturnsNull()
    {
        // Arrange
        var (provider, _, _) = CreateProvider();

        // Act
        var policy = await provider.GetPolicyAsync("non_existent_tool");

        // Assert
        Assert.Null(policy);
    }

    [Fact]
    public async Task GetPolicyAsyncWhenRedisIsOfflineCachesInMemAndServesSubsequentCallsFromMemory()
    {
        // Arrange
        var (provider, db, cache) = CreateProvider();

        // Initial fetch: hits database and populates in-memory cache
        var initialPolicy = await provider.GetPolicyAsync("echo");
        Assert.NotNull(initialPolicy);
        Assert.Equal(60, initialPolicy.MaxRequestsPerMinute);
        Assert.True(cache.ContainsKey("echo"));

        // Mutate the database directly behind the cache's back
        var dbEntity = await db.ToolPolicies.FirstAsync(p => p.ToolName == "echo");
        dbEntity.MaxRequestsPerMinute = 999;
        await db.SaveChangesAsync();

        // Act: second fetch should serve from in-memory cache without hitting DB
        var cachedPolicy = await provider.GetPolicyAsync("echo");

        // Assert: returned policy has the original in-memory cached value (60), not mutated DB value (999)
        Assert.NotNull(cachedPolicy);
        Assert.Equal(60, cachedPolicy.MaxRequestsPerMinute);
    }

    [Fact]
    public async Task InvalidatePolicyCacheAsyncEvictsFromInMemoryCacheRefetchesFromDatabase()
    {
        // Arrange
        var (provider, db, cache) = CreateProvider();

        // Initial fetch into cache
        var policy = await provider.GetPolicyAsync("echo");
        Assert.NotNull(policy);
        Assert.Equal(60, policy.MaxRequestsPerMinute);

        // Mutate database entity
        var dbEntity = await db.ToolPolicies.FirstAsync(p => p.ToolName == "echo");
        dbEntity.MaxRequestsPerMinute = 120;
        await db.SaveChangesAsync();

        // Invalidate cache
        await provider.InvalidatePolicyCacheAsync("echo");
        Assert.False(cache.ContainsKey("echo"));

        // Act: re-fetch after invalidation
        var refreshedPolicy = await provider.GetPolicyAsync("echo");

        // Assert: should query database again and get updated value (120)
        Assert.NotNull(refreshedPolicy);
        Assert.Equal(120, refreshedPolicy.MaxRequestsPerMinute);
        Assert.True(cache.ContainsKey("echo"));
    }

    [Fact]
    public async Task GetAllPoliciesAsyncReturnsAllPoliciesAndPopulatesCache()
    {
        // Arrange
        var (provider, _, cache) = CreateProvider();

        // Act
        var allPolicies = await provider.GetAllPoliciesAsync();

        // Assert
        Assert.NotNull(allPolicies);
        Assert.True(allPolicies.Count >= 4);
        Assert.True(allPolicies.ContainsKey("echo"));
        Assert.True(allPolicies.ContainsKey("get_employee_profile"));
        Assert.True(allPolicies.ContainsKey("query_system_metrics"));
        Assert.True(allPolicies.ContainsKey("fetch_external_exchange_rates"));

        // Verify that in-memory cache was populated
        Assert.True(cache.ContainsKey("echo"));
        Assert.True(cache.ContainsKey("get_employee_profile"));
        Assert.True(cache.ContainsKey("query_system_metrics"));
        Assert.True(cache.ContainsKey("fetch_external_exchange_rates"));
    }

    [Fact]
    public async Task GetPolicyAsyncWhenDatabaseFailsUsesInMemoryFallback()
    {
        // Arrange
        var (provider, db, _) = CreateProvider();

        // Prime the cache
        var initialPolicy = await provider.GetPolicyAsync("echo");
        Assert.NotNull(initialPolicy);

        // Dispose db context to simulate database connection failure
        await db.DisposeAsync();

        // Act: should not throw, should return the cached policy
        var fallbackPolicy = await provider.GetPolicyAsync("echo");

        // Assert
        Assert.NotNull(fallbackPolicy);
        Assert.Equal("echo", fallbackPolicy.ToolName);
    }

    [Fact]
    public async Task GetAllPoliciesAsyncWhenDatabaseFailsUsesInMemoryFallback()
    {
        // Arrange
        var (provider, db, _) = CreateProvider();

        // Prime cache
        var allInitial = await provider.GetAllPoliciesAsync();
        Assert.NotEmpty(allInitial);

        // Dispose db context to simulate database outage
        await db.DisposeAsync();

        // Act: should fallback to in-memory dictionary
        var fallbackAll = await provider.GetAllPoliciesAsync();

        // Assert
        Assert.NotNull(fallbackAll);
        Assert.Equal(allInitial.Count, fallbackAll.Count);
        Assert.True(fallbackAll.ContainsKey("echo"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetPolicyAsyncWithInvalidToolNameThrowsArgumentException(string invalidToolName)
    {
        // Arrange
        var (provider, _, _) = CreateProvider();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetPolicyAsync(invalidToolName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InvalidatePolicyCacheAsyncWithInvalidToolNameThrowsArgumentException(string invalidToolName)
    {
        // Arrange
        var (provider, _, _) = CreateProvider();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => provider.InvalidatePolicyCacheAsync(invalidToolName));
    }

    [Fact]
    public async Task MethodsWhenCancellationRequestedThrowOperationCanceledException()
    {
        // Arrange
        var (provider, _, _) = CreateProvider();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.GetPolicyAsync("echo", cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.GetAllPoliciesAsync(cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.InvalidatePolicyCacheAsync("echo", cts.Token));
    }

    [Fact]
    public async Task GetPolicyAsyncNormalizesToolNameCaseInsensitively()
    {
        // Arrange
        var (provider, _, cache) = CreateProvider();

        // Act
        var policy = await provider.GetPolicyAsync("  ECHO  ");

        // Assert
        Assert.NotNull(policy);
        Assert.Equal("echo", policy.ToolName);
        Assert.True(cache.ContainsKey("echo"));

        // Invalidate with uppercase and verify eviction
        await provider.InvalidatePolicyCacheAsync("ECHO");
        Assert.False(cache.ContainsKey("echo"));
    }

    [Fact]
    public void HasSinglePublicConstructorForUnambiguousDependencyInjection()
    {
        var constructors = typeof(RedisToolPolicyProvider).GetConstructors();
        Assert.Single(constructors);
    }
}

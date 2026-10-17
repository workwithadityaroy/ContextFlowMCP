using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using ContextFlow.Core.Interfaces;
using ContextFlow.Infrastructure.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace ContextFlow.UnitTests;

public sealed class RedisRateLimiterTests
{
    private sealed class TestTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        private DateTimeOffset _currentTime = initialTime;

        public override DateTimeOffset GetUtcNow() => _currentTime;

        public void Advance(TimeSpan delta) => _currentTime = _currentTime.Add(delta);
    }

    [Fact]
    public async Task BurstLimitFirstNSucceedNPlus1FailsWithRetryAfter()
    {
        // Arrange
        var timeProvider = new TestTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new RedisRateLimiter(
            connection: null,
            logger: NullLogger<RedisRateLimiter>.Instance,
            timeProvider: timeProvider);

        const int maxRequests = 5;
        const string user = "user-burst";
        const string tool = "query_system_metrics";

        // Act & Assert: First N requests succeed with descending remaining requests
        for (var i = 1; i <= maxRequests; i++)
        {
            var result = await limiter.CheckRateLimitAsync(user, tool, maxRequests);
            Assert.True(result.IsAllowed, $"Request #{i} should be allowed.");
            Assert.Equal(maxRequests - i, result.RemainingRequests);
            Assert.Equal(0, result.RetryAfterSeconds);
        }

        // Act & Assert: Request N+1 is throttled
        var throttledResult = await limiter.CheckRateLimitAsync(user, tool, maxRequests);
        Assert.False(throttledResult.IsAllowed, "Request N+1 must be rejected.");
        Assert.Equal(0, throttledResult.RemainingRequests);
        Assert.True(throttledResult.RetryAfterSeconds > 0, "RetryAfterSeconds must be greater than zero.");
        Assert.True(throttledResult.RetryAfterSeconds <= 60, "RetryAfterSeconds must not exceed window duration.");
    }

    [Fact]
    public async Task UserIsolationUserAThrottledUserBAllowed()
    {
        // Arrange
        var limiter = new RedisRateLimiter(
            connection: null,
            logger: NullLogger<RedisRateLimiter>.Instance);

        const int maxRequests = 3;
        const string tool = "echo";

        // Exhaust user-a quota
        for (var i = 0; i < maxRequests; i++)
        {
            var allowed = await limiter.CheckRateLimitAsync("user-a", tool, maxRequests);
            Assert.True(allowed.IsAllowed);
        }

        var userABlocked = await limiter.CheckRateLimitAsync("user-a", tool, maxRequests);
        Assert.False(userABlocked.IsAllowed);

        // Act: User B requests the same tool
        var userBResult = await limiter.CheckRateLimitAsync("user-b", tool, maxRequests);

        // Assert: User B has independent quota
        Assert.True(userBResult.IsAllowed);
        Assert.Equal(maxRequests - 1, userBResult.RemainingRequests);
    }

    [Fact]
    public async Task ToolIsolationToolAThrottledToolBAllowed()
    {
        // Arrange
        var limiter = new RedisRateLimiter(
            connection: null,
            logger: NullLogger<RedisRateLimiter>.Instance);

        const int maxRequests = 2;
        const string user = "user-isolated";

        // Exhaust quota on tool-1
        for (var i = 0; i < maxRequests; i++)
        {
            var allowed = await limiter.CheckRateLimitAsync(user, "tool-1", maxRequests);
            Assert.True(allowed.IsAllowed);
        }

        var tool1Blocked = await limiter.CheckRateLimitAsync(user, "tool-1", maxRequests);
        Assert.False(tool1Blocked.IsAllowed);

        // Act: Same user calls tool-2
        var tool2Result = await limiter.CheckRateLimitAsync(user, "tool-2", maxRequests);

        // Assert: tool-2 has independent quota
        Assert.True(tool2Result.IsAllowed);
        Assert.Equal(maxRequests - 1, tool2Result.RemainingRequests);
    }

    [Fact]
    public async Task RedisAbsentFallsBackToInMemoryAndEnforcesLimits()
    {
        // Arrange: Explicitly passing null connection
        var limiter = new RedisRateLimiter(
            connection: null,
            logger: NullLogger<RedisRateLimiter>.Instance);

        // Act
        var result1 = await limiter.CheckRateLimitAsync("user-absent", "tool-test", 1);
        var result2 = await limiter.CheckRateLimitAsync("user-absent", "tool-test", 1);

        // Assert
        Assert.True(result1.IsAllowed);
        Assert.Equal(0, result1.RemainingRequests);

        Assert.False(result2.IsAllowed);
        Assert.True(result2.RetryAfterSeconds >= 1);
    }

    [Fact]
    public async Task SlidingWindowRecoversQuotaAfterWindowExpires()
    {
        // Arrange
        var initialTime = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(initialTime);
        var limiter = new RedisRateLimiter(
            connection: null,
            logger: NullLogger<RedisRateLimiter>.Instance,
            timeProvider: timeProvider);

        const int maxRequests = 2;
        const string user = "user-window";
        const string tool = "test-tool";

        // Fill window at t = 0
        var r1 = await limiter.CheckRateLimitAsync(user, tool, maxRequests);
        var r2 = await limiter.CheckRateLimitAsync(user, tool, maxRequests);
        Assert.True(r1.IsAllowed);
        Assert.True(r2.IsAllowed);

        // t = 10s: still throttled
        timeProvider.Advance(TimeSpan.FromSeconds(10));
        var blockedAt10 = await limiter.CheckRateLimitAsync(user, tool, maxRequests);
        Assert.False(blockedAt10.IsAllowed);
        Assert.Equal(50, blockedAt10.RetryAfterSeconds);

        // Advance past 60s window (t = 61s from start)
        timeProvider.Advance(TimeSpan.FromSeconds(51)); // total +61s from t=0
        var recovered = await limiter.CheckRateLimitAsync(user, tool, maxRequests);

        // Assert: Oldest request from t=0 has expired out of the sliding window
        Assert.True(recovered.IsAllowed);
        Assert.Equal(1, recovered.RemainingRequests);
    }

    [Fact]
    public async Task RedisExceptionFallsBackToInMemoryDegradedMode()
    {
        // Arrange: Create a multiplexer proxy that throws RedisException on script evaluation
        var throwingMultiplexer = ThrowingMultiplexerProxy.Create();
        var limiter = new RedisRateLimiter(
            connection: throwingMultiplexer,
            logger: NullLogger<RedisRateLimiter>.Instance);

        // Act: Call should not throw RedisException, but gracefully fallback to in-memory
        var result1 = await limiter.CheckRateLimitAsync("user-redis-down", "tool-redis", 1);
        var result2 = await limiter.CheckRateLimitAsync("user-redis-down", "tool-redis", 1);

        // Assert
        Assert.True(result1.IsAllowed);
        Assert.False(result2.IsAllowed);
    }

    [Theory]
    [InlineData("", "tool", 10)]
    [InlineData("   ", "tool", 10)]
    [InlineData("user", "", 10)]
    [InlineData("user", "   ", 10)]
    public async Task ArgumentValidationThrowsOnInvalidUserOrTool(string user, string tool, int max)
    {
        var limiter = new RedisRateLimiter(
            connection: null,
            logger: NullLogger<RedisRateLimiter>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            limiter.CheckRateLimitAsync(user, tool, max));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task ArgumentValidationThrowsOnInvalidMaxRequests(int invalidMax)
    {
        var limiter = new RedisRateLimiter(
            connection: null,
            logger: NullLogger<RedisRateLimiter>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            limiter.CheckRateLimitAsync("user", "tool", invalidMax));
    }

    [Fact]
    public async Task CancellationTokenHonorsCancellation()
    {
        var limiter = new RedisRateLimiter(
            connection: null,
            logger: NullLogger<RedisRateLimiter>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            limiter.CheckRateLimitAsync("user", "tool", 10, cts.Token));
    }

    [Fact]
    public async Task ToolNameCasingSharesSameRateLimitBucket()
    {
        var limiter = new RedisRateLimiter(
            logger: NullLogger<RedisRateLimiter>.Instance,
            connection: null);

        var r1 = await limiter.CheckRateLimitAsync("user-case", "echo", 1);
        var r2 = await limiter.CheckRateLimitAsync("user-case", "ECHO", 1);

        Assert.True(r1.IsAllowed);
        Assert.False(r2.IsAllowed);
    }

    [Fact]
    public void HasSinglePublicConstructorForUnambiguousDependencyInjection()
    {
        var constructors = typeof(RedisRateLimiter).GetConstructors();
        Assert.Single(constructors);
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
            if (targetMethod?.Name.Contains("ScriptEvaluate") == true)
            {
                throw new RedisServerException("NOSCRIPT No matching script. Please use EVAL.");
            }

            return null;
        }
    }
}


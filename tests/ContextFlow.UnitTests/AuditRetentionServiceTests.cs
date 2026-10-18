using ContextFlow.Core.Entities;
using ContextFlow.Core.Enums;
using ContextFlow.Infrastructure.Auditing;
using ContextFlow.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContextFlow.UnitTests;

public sealed class AuditRetentionServiceTests
{
    private static (IServiceScopeFactory scopeFactory, DbContextOptions<ContextFlowDbContext> options) CreateScopeFactory()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<ContextFlowDbContext>(options =>
            options.UseInMemoryDatabase(dbName));

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var optionsBuilder = new DbContextOptionsBuilder<ContextFlowDbContext>()
            .UseInMemoryDatabase(dbName);

        return (scopeFactory, optionsBuilder.Options);
    }

    private static ToolExecutionLog CreateLog(string toolName, DateTimeOffset timestamp) => new()
    {
        UserId = "retention-user",
        TokenId = Guid.NewGuid().ToString("N"),
        ToolName = toolName,
        Timestamp = timestamp,
        Outcome = ExecutionOutcome.Success,
        DurationMs = 10,
        CacheHit = false,
        InputJson = "{}",
        OutputJson = "{}",
        OutputBytes = 2
    };

    private static async Task WaitForConditionAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var start = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - start < timeout)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        if (!await condition())
        {
            throw new TimeoutException($"Condition was not satisfied within {timeout.TotalMilliseconds}ms.");
        }
    }

    [Fact]
    public async Task ExecuteAsyncPurgesLogsOlderThanRetentionDaysPreservesRecentLogs()
    {
        // Arrange
        var (scopeFactory, dbOptions) = CreateScopeFactory();
        var now = DateTimeOffset.UtcNow;

        // Seed 4 logs: 2 expired (> 30 days old), 2 valid (<= 30 days old)
        await using (var seedDb = new ContextFlowDbContext(dbOptions))
        {
            seedDb.ToolExecutionLogs.AddRange(
                CreateLog("expired_45d", now.AddDays(-45)),
                CreateLog("expired_32d", now.AddDays(-32)),
                CreateLog("valid_10d", now.AddDays(-10)),
                CreateLog("valid_1h", now.AddHours(-1))
            );
            await seedDb.SaveChangesAsync();
        }

        var service = new AuditRetentionService(
            scopeFactory,
            NullLogger<AuditRetentionService>.Instance,
            retentionDays: 30,
            checkInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StartAsync(cts.Token);

        try
        {
            // Assert: wait for the periodic check to purge the 2 expired logs
            await WaitForConditionAsync(async () =>
            {
                await using var db = new ContextFlowDbContext(dbOptions);
                var count = await db.ToolExecutionLogs.CountAsync();
                return count == 2;
            }, TimeSpan.FromSeconds(3));

            await using var verifyDb = new ContextFlowDbContext(dbOptions);
            var remainingLogs = await verifyDb.ToolExecutionLogs
                .OrderBy(l => l.ToolName)
                .ToListAsync();

            Assert.Equal(2, remainingLogs.Count);
            Assert.Equal("valid_10d", remainingLogs[0].ToolName);
            Assert.Equal("valid_1h", remainingLogs[1].ToolName);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsyncHonorsCustomRetentionDays()
    {
        // Arrange: 7-day retention
        var (scopeFactory, dbOptions) = CreateScopeFactory();
        var now = DateTimeOffset.UtcNow;

        await using (var seedDb = new ContextFlowDbContext(dbOptions))
        {
            seedDb.ToolExecutionLogs.AddRange(
                CreateLog("expired_10d", now.AddDays(-10)),
                CreateLog("valid_3d", now.AddDays(-3))
            );
            await seedDb.SaveChangesAsync();
        }

        var service = new AuditRetentionService(
            scopeFactory,
            NullLogger<AuditRetentionService>.Instance,
            retentionDays: 7,
            checkInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StartAsync(cts.Token);

        try
        {
            await WaitForConditionAsync(async () =>
            {
                await using var db = new ContextFlowDbContext(dbOptions);
                var count = await db.ToolExecutionLogs.CountAsync();
                return count == 1;
            }, TimeSpan.FromSeconds(3));

            await using var verifyDb = new ContextFlowDbContext(dbOptions);
            var remaining = await verifyDb.ToolExecutionLogs.SingleAsync();
            Assert.Equal("valid_3d", remaining.ToolName);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}

using ContextFlow.Core.Entities;
using ContextFlow.Core.Enums;
using ContextFlow.Infrastructure.Auditing;
using ContextFlow.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContextFlow.UnitTests;

public sealed class AuditWriterServiceTests
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

    private static ToolExecutionLog CreateLog(string toolName, string userId = "test-user") => new()
    {
        UserId = userId,
        TokenId = Guid.NewGuid().ToString("N"),
        ToolName = toolName,
        Outcome = ExecutionOutcome.Success,
        DurationMs = 25,
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
    public async Task ExecuteAsyncFlushesWhenBatchSizeThresholdReached()
    {
        // Arrange
        var (scopeFactory, dbOptions) = CreateScopeFactory();
        var queue = new ChannelAuditQueue(capacity: 100);

        // Batch size 5, very long flush interval (10s) so only batch size triggers flush
        var service = new AuditWriterService(
            queue,
            scopeFactory,
            NullLogger<AuditWriterService>.Instance,
            batchSize: 5,
            flushInterval: TimeSpan.FromSeconds(10));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StartAsync(cts.Token);

        try
        {
            // Act: enqueue exactly 5 items
            for (var i = 1; i <= 5; i++)
            {
                queue.TryEnqueue(CreateLog($"tool_{i}"));
            }

            // Assert: wait for 5 items to appear in the database
            await WaitForConditionAsync(async () =>
            {
                await using var db = new ContextFlowDbContext(dbOptions);
                var count = await db.ToolExecutionLogs.CountAsync();
                return count == 5;
            }, TimeSpan.FromSeconds(3));

            await using var verifyDb = new ContextFlowDbContext(dbOptions);
            var logs = await verifyDb.ToolExecutionLogs.OrderBy(l => l.ToolName).ToListAsync();
            Assert.Equal(5, logs.Count);
            Assert.Equal("tool_1", logs[0].ToolName);
            Assert.Equal("tool_5", logs[4].ToolName);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsyncFlushesWhenFlushIntervalElapsesBeforeBatchSize()
    {
        // Arrange
        var (scopeFactory, dbOptions) = CreateScopeFactory();
        var queue = new ChannelAuditQueue(capacity: 100);

        // Large batch size (50), fast flush interval (100ms)
        var service = new AuditWriterService(
            queue,
            scopeFactory,
            NullLogger<AuditWriterService>.Instance,
            batchSize: 50,
            flushInterval: TimeSpan.FromMilliseconds(100));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StartAsync(cts.Token);

        try
        {
            // Act: enqueue only 2 items (well below batchSize of 50)
            queue.TryEnqueue(CreateLog("tool_interval_1"));
            queue.TryEnqueue(CreateLog("tool_interval_2"));

            // Assert: flush interval of 100ms triggers persistence
            await WaitForConditionAsync(async () =>
            {
                await using var db = new ContextFlowDbContext(dbOptions);
                var count = await db.ToolExecutionLogs.CountAsync();
                return count == 2;
            }, TimeSpan.FromSeconds(3));

            await using var verifyDb = new ContextFlowDbContext(dbOptions);
            var count = await verifyDb.ToolExecutionLogs.CountAsync();
            Assert.Equal(2, count);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StopAsyncFlushesRemainingBufferedItemsOnShutdown()
    {
        // Arrange
        var (scopeFactory, dbOptions) = CreateScopeFactory();
        var queue = new ChannelAuditQueue(capacity: 100);

        // Huge batch size (100) and long flush interval (60s)
        var service = new AuditWriterService(
            queue,
            scopeFactory,
            NullLogger<AuditWriterService>.Instance,
            batchSize: 100,
            flushInterval: TimeSpan.FromMinutes(1));

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        // Act: Enqueue 3 items
        queue.TryEnqueue(CreateLog("shutdown_tool_1"));
        queue.TryEnqueue(CreateLog("shutdown_tool_2"));
        queue.TryEnqueue(CreateLog("shutdown_tool_3"));

        // Allow worker to drain into in-memory batch
        await Task.Delay(100);

        // Stop the service - graceful shutdown should flush remaining batch
        await service.StopAsync(CancellationToken.None);

        // Assert
        await using var verifyDb = new ContextFlowDbContext(dbOptions);
        var logs = await verifyDb.ToolExecutionLogs.ToListAsync();
        Assert.Equal(3, logs.Count);
    }
}

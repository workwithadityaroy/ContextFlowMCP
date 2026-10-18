using ContextFlow.Core.Entities;
using ContextFlow.Core.Enums;
using ContextFlow.Infrastructure.Auditing;

namespace ContextFlow.UnitTests;

public sealed class ChannelAuditQueueTests
{
    private static ToolExecutionLog CreateSampleLog(string toolName = "echo", string userId = "user-1") => new()
    {
        UserId = userId,
        TokenId = Guid.NewGuid().ToString("N"),
        ToolName = toolName,
        Outcome = ExecutionOutcome.Success,
        DurationMs = 12,
        CacheHit = false,
        InputJson = "{\"message\":\"hello\"}",
        OutputJson = "{\"message\":\"hello\"}",
        OutputBytes = 19
    };

    [Fact]
    public void ConstructorInitializesEmptyWithZeroDroppedCount()
    {
        var queue = new ChannelAuditQueue(capacity: 50);

        Assert.Equal(0, queue.DroppedRecordsCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ConstructorThrowsArgumentOutOfRangeExceptionWhenCapacityIsZeroOrNegative(int invalidCapacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelAuditQueue(invalidCapacity));
    }

    [Fact]
    public void TryEnqueueThrowsArgumentNullExceptionWhenLogEntryIsNull()
    {
        var queue = new ChannelAuditQueue();

        Assert.Throws<ArgumentNullException>(() => queue.TryEnqueue(null!));
    }

    [Fact]
    public async Task TryEnqueueEnqueuesItemsAndReadAllAsyncDrainsThemInOrder()
    {
        // Arrange
        var queue = new ChannelAuditQueue(capacity: 10);
        var entry1 = CreateSampleLog("echo", "user-1");
        var entry2 = CreateSampleLog("get_employee_profile", "user-2");
        var entry3 = CreateSampleLog("query_system_metrics", "user-3");

        // Act
        Assert.True(queue.TryEnqueue(entry1));
        Assert.True(queue.TryEnqueue(entry2));
        Assert.True(queue.TryEnqueue(entry3));

        // Drain using ReadAllAsync with cancellation token
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var drained = new List<ToolExecutionLog>();

        await foreach (var item in queue.ReadAllAsync(cts.Token))
        {
            drained.Add(item);
            if (drained.Count == 3)
            {
                break;
            }
        }

        // Assert
        Assert.Equal(3, drained.Count);
        Assert.Same(entry1, drained[0]);
        Assert.Same(entry2, drained[1]);
        Assert.Same(entry3, drained[2]);
        Assert.Equal(0, queue.DroppedRecordsCount);
    }

    [Fact]
    public async Task TryEnqueueWhenBurstExceedsCapacityDropsOldestAndIncrementsDroppedRecordsCount()
    {
        // Arrange: capacity = 3
        const int capacity = 3;
        var queue = new ChannelAuditQueue(capacity: capacity);

        var entries = new List<ToolExecutionLog>();
        for (var i = 1; i <= 5; i++)
        {
            entries.Add(CreateSampleLog($"tool_{i}", $"user_{i}"));
        }

        // Act: Enqueue 5 items into capacity-3 queue
        foreach (var entry in entries)
        {
            var enqueued = queue.TryEnqueue(entry);
            Assert.True(enqueued);
        }

        // Assert: 2 items dropped
        Assert.Equal(2, queue.DroppedRecordsCount);

        // Drain remaining 3 items
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var drained = new List<ToolExecutionLog>();

        await foreach (var item in queue.ReadAllAsync(cts.Token))
        {
            drained.Add(item);
            if (drained.Count == capacity)
            {
                break;
            }
        }

        // DropOldest: items 0 and 1 dropped, items 2, 3, 4 retained
        Assert.Equal(3, drained.Count);
        Assert.Equal("tool_3", drained[0].ToolName);
        Assert.Equal("tool_4", drained[1].ToolName);
        Assert.Equal("tool_5", drained[2].ToolName);
    }
}

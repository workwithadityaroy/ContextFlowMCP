using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using ContextFlow.Core.Entities;
using ContextFlow.Core.Interfaces;

namespace ContextFlow.Infrastructure.Auditing;

/// <summary>
/// Bounded in-memory channel implementation of <see cref="IAuditQueue"/> to decouple MCP tool invocations
/// from database writes with a drop-oldest overflow strategy.
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "ChannelAuditQueue implements IAuditQueue per context.md specification.")]
public sealed class ChannelAuditQueue(int capacity = 10_000) : IAuditQueue
{
    private readonly int _capacity = ValidateCapacity(capacity);

    private readonly Channel<ToolExecutionLog> _channel = Channel.CreateBounded<ToolExecutionLog>(
        new BoundedChannelOptions(ValidateCapacity(capacity))
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    private static int ValidateCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        return capacity;
    }

    private long _droppedRecordsCount;

    /// <inheritdoc />
    public bool TryEnqueue(ToolExecutionLog logEntry)
    {
        ArgumentNullException.ThrowIfNull(logEntry);

        if (_channel.Reader.Count >= _capacity)
        {
            Interlocked.Increment(ref _droppedRecordsCount);
        }

        return _channel.Writer.TryWrite(logEntry);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ToolExecutionLog> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        return _channel.Reader.ReadAllAsync(cancellationToken);
    }

    /// <inheritdoc />
    public long DroppedRecordsCount => Interlocked.Read(ref _droppedRecordsCount);
}

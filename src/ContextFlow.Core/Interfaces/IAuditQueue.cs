using System.Diagnostics.CodeAnalysis;
using ContextFlow.Core.Entities;

namespace ContextFlow.Core.Interfaces;

/// <summary>
/// Asynchronous bounded in-memory queue for decoupling MCP request threads from audit log database persistence.
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "IAuditQueue represents an asynchronous bounded queue contract per context.md specification.")]
public interface IAuditQueue
{
    /// <summary>
    /// Attempts to enqueue a tool execution log entry in a non-blocking manner.
    /// </summary>
    /// <param name="logEntry">The log entry to record.</param>
    /// <returns><c>true</c> if successfully queued; otherwise <c>false</c>.</returns>
    bool TryEnqueue(ToolExecutionLog logEntry);

    /// <summary>
    /// Asynchronously reads log entries from the queue as they become available.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to stop reading.</param>
    /// <returns>An asynchronous stream of tool execution log entries.</returns>
    IAsyncEnumerable<ToolExecutionLog> ReadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the total number of audit records dropped due to queue saturation.
    /// </summary>
    long DroppedRecordsCount { get; }
}


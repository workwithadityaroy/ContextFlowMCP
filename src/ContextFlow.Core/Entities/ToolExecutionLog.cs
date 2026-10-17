using ContextFlow.Core.Enums;

namespace ContextFlow.Core.Entities;

/// <summary>
/// Audit trail entry for every governed MCP tool invocation.
/// </summary>
public sealed class ToolExecutionLog
{
    public long Id { get; set; }

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public required string UserId { get; set; }

    public required string TokenId { get; set; }

    public required string ToolName { get; set; }

    public ExecutionOutcome Outcome { get; set; }

    public long DurationMs { get; set; }

    public bool CacheHit { get; set; }

    public string? InputJson { get; set; }

    public string? OutputJson { get; set; }

    public int OutputBytes { get; set; }

    public string? ErrorType { get; set; }
}


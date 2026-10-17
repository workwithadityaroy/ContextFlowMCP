using ContextFlow.Core.Enums;

namespace ContextFlow.Core.Entities;

/// <summary>
/// Runtime governance policy for an MCP tool.
/// </summary>
public sealed class ToolPolicy
{
    public required string ToolName { get; set; }

    public bool IsEnabled { get; set; } = true;

    public int MaxRequestsPerMinute { get; set; } = 60;

    public int CacheTtlSeconds { get; set; }

    public AuditMode AuditMode { get; set; } = AuditMode.Full;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}


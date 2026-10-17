namespace ContextFlow.Core.Enums;

/// <summary>
/// Status outcome of an MCP request filter or tool invocation.
/// </summary>
public enum ExecutionOutcome
{
    /// <summary>
    /// Invocation completed successfully.
    /// </summary>
    Success = 0,

    /// <summary>
    /// Invocation was blocked by authorization filter.
    /// </summary>
    Denied = 1,

    /// <summary>
    /// Tool is disabled in governance policy.
    /// </summary>
    Disabled = 2,

    /// <summary>
    /// Rate limit sliding window quota exceeded.
    /// </summary>
    RateLimited = 3,

    /// <summary>
    /// An unhandled exception or operational failure occurred.
    /// </summary>
    Error = 4
}


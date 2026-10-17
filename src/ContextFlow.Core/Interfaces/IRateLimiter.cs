namespace ContextFlow.Core.Interfaces;

/// <summary>
/// Represents the outcome of evaluating a request against a sliding-window rate limit.
/// </summary>
/// <param name="IsAllowed">True if the request is permitted; false if throttled.</param>
/// <param name="RemainingRequests">The remaining permitted requests in the current sliding window.</param>
/// <param name="RetryAfterSeconds">The number of seconds the caller should wait before retrying if throttled.</param>
public readonly record struct RateLimitResult(bool IsAllowed, int RemainingRequests, int RetryAfterSeconds);

/// <summary>
/// Governs tool execution volume per user and tool using a sliding-window algorithm.
/// </summary>
public interface IRateLimiter
{
    /// <summary>
    /// Checks whether an invocation by the specified user on the target tool is within permitted limits.
    /// </summary>
    /// <param name="userId">Unique identifier of the requesting user.</param>
    /// <param name="toolName">Name of the target MCP tool.</param>
    /// <param name="maxRequestsPerMinute">Maximum permitted requests in a 60-second sliding window.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="RateLimitResult"/> indicating whether the request is allowed.</returns>
    Task<RateLimitResult> CheckRateLimitAsync(
        string userId,
        string toolName,
        int maxRequestsPerMinute,
        CancellationToken cancellationToken = default);
}


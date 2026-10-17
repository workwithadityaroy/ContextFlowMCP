namespace ContextFlow.Core.Interfaces;

/// <summary>
/// Provides distributed cache operations for MCP tool execution results.
/// </summary>
public interface IResultCache
{
    /// <summary>
    /// Retrieves a cached tool execution result for the specified cache key.
    /// </summary>
    /// <param name="key">The cache key identifying the result.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached result string, or <see langword="null"/> if not found or expired.</returns>
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a tool execution result in the cache with the specified time-to-live.
    /// </summary>
    /// <param name="key">The cache key identifying the result.</param>
    /// <param name="value">The serialized result content to cache.</param>
    /// <param name="ttl">The time-to-live duration for the cached entry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a cached tool execution result for the specified cache key.
    /// </summary>
    /// <param name="key">The cache key identifying the result to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}

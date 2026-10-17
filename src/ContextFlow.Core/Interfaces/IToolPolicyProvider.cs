using ContextFlow.Core.Entities;

namespace ContextFlow.Core.Interfaces;

/// <summary>
/// Provider responsible for retrieving and managing cached runtime governance policies for tools.
/// </summary>
public interface IToolPolicyProvider
{
    /// <summary>
    /// Gets the governance policy for the specified tool name, or null if no policy is defined.
    /// </summary>
    /// <param name="toolName">The unique name of the MCP tool.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tool policy if found; otherwise null.</returns>
    Task<ToolPolicy?> GetPolicyAsync(string toolName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all registered governance policies mapped by tool name.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A read-only dictionary of tool names to their corresponding policies.</returns>
    Task<IReadOnlyDictionary<string, ToolPolicy>> GetAllPoliciesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates cached policy entries for the specified tool across cache tiers.
    /// </summary>
    /// <param name="toolName">The unique name of the MCP tool.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task InvalidatePolicyCacheAsync(string toolName, CancellationToken cancellationToken = default);
}

namespace ContextFlow.Core.Interfaces;

/// <summary>
/// Service responsible for minting, validating, and revoking Personal Access Tokens (PATs).
/// </summary>
public interface IPatTokenService
{
    /// <summary>
    /// Mints a new signed HS256 PAT for the specified user and roles.
    /// </summary>
    /// <param name="ownerUserId">Identifier of the token owner.</param>
    /// <param name="name">Descriptive name for the token.</param>
    /// <param name="roles">Assigned roles (e.g. Admin, Developer, Finance).</param>
    /// <param name="lifetime">Optional lifetime; clamped to a maximum of 90 days.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The raw signed JWT token string (never persisted).</returns>
    Task<string> MintTokenAsync(
        string ownerUserId,
        string name,
        IEnumerable<string> roles,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes an existing token by its unique JWT identifier (jti).
    /// </summary>
    /// <param name="jti">JWT identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the token was found and revoked; false otherwise.</returns>
    Task<bool> RevokeTokenAsync(
        string jti,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether the specified token has been revoked.
    /// </summary>
    /// <param name="jti">JWT identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if revoked; false if active.</returns>
    Task<bool> IsTokenRevokedAsync(
        string jti,
        CancellationToken cancellationToken = default);
}


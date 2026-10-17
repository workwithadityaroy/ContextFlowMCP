namespace ContextFlow.Core.Entities;

/// <summary>
/// Metadata for dashboard-minted Personal Access Tokens (PATs).
/// Note: The raw token secret is never stored.
/// </summary>
public sealed class PersonalAccessToken
{
    public required string Jti { get; set; }

    public required string Name { get; set; }

    public required string OwnerUserId { get; set; }

    public required string Roles { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsRevoked => RevokedAt.HasValue;

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;

    public bool IsActive => !IsRevoked && !IsExpired;
}


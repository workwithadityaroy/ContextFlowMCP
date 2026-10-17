using ContextFlow.Core.Entities;
using ContextFlow.Core.Enums;

namespace ContextFlow.UnitTests;

public sealed class EntityModelTests
{
    [Fact]
    public void ToolPolicyDefaultValuesAreCorrect()
    {
        // Act
        var policy = new ToolPolicy
        {
            ToolName = "test_tool"
        };

        // Assert
        Assert.Equal("test_tool", policy.ToolName);
        Assert.True(policy.IsEnabled);
        Assert.Equal(60, policy.MaxRequestsPerMinute);
        Assert.Equal(0, policy.CacheTtlSeconds);
        Assert.Equal(AuditMode.Full, policy.AuditMode);
    }

    [Fact]
    public void PersonalAccessTokenStatePropertiesReflectRevocationAndExpiration()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var activeToken = new PersonalAccessToken
        {
            Jti = "jti-1",
            Name = "Active Dev Token",
            OwnerUserId = "user-1",
            Roles = "Developer",
            CreatedAt = now.AddDays(-1),
            ExpiresAt = now.AddDays(30),
            RevokedAt = null
        };

        var expiredToken = new PersonalAccessToken
        {
            Jti = "jti-2",
            Name = "Expired Token",
            OwnerUserId = "user-2",
            Roles = "Developer",
            CreatedAt = now.AddDays(-60),
            ExpiresAt = now.AddDays(-1),
            RevokedAt = null
        };

        var revokedToken = new PersonalAccessToken
        {
            Jti = "jti-3",
            Name = "Revoked Token",
            OwnerUserId = "user-3",
            Roles = "Admin",
            CreatedAt = now.AddDays(-10),
            ExpiresAt = now.AddDays(20),
            RevokedAt = now.AddDays(-1)
        };

        // Assert
        Assert.True(activeToken.IsActive);
        Assert.False(activeToken.IsExpired);
        Assert.False(activeToken.IsRevoked);

        Assert.False(expiredToken.IsActive);
        Assert.True(expiredToken.IsExpired);
        Assert.False(expiredToken.IsRevoked);

        Assert.False(revokedToken.IsActive);
        Assert.False(revokedToken.IsExpired);
        Assert.True(revokedToken.IsRevoked);
    }
}


using System.IdentityModel.Tokens.Jwt;
using ContextFlow.Infrastructure.Data;
using ContextFlow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContextFlow.UnitTests;

public sealed class PatTokenServiceTests
{
    private const string _validSecret = "contextflow-development-test-secret-key-min-32-bytes!!";

    private static (PatTokenService service, ContextFlowDbContext db) CreateService(string? secret = _validSecret)
    {
        var options = new DbContextOptionsBuilder<ContextFlowDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var db = new ContextFlowDbContext(options);

        var configData = new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "contextflow-test",
            ["Jwt:Audience"] = "contextflow-test-aud",
            ["Jwt:SigningKey"] = secret
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var service = new PatTokenService(db, config, NullLogger<PatTokenService>.Instance);
        return (service, db);
    }

    [Fact]
    public void ConstructorThrowsWhenSecretIsMissingOrTooShort()
    {
        // Missing
        Assert.Throws<InvalidOperationException>(() => CreateService(secret: null));

        // Too short (< 32 bytes)
        Assert.Throws<InvalidOperationException>(() => CreateService(secret: "too-short-key"));
    }

    [Fact]
    public async Task MintTokenAsyncProducesValidJwtAndPersistsMetadata()
    {
        // Arrange
        var (service, db) = CreateService();

        // Act
        var token = await service.MintTokenAsync(
            ownerUserId: "user-123",
            name: "Test Token",
            roles: ["Developer", "Finance"],
            lifetime: TimeSpan.FromDays(14));

        // Assert
        Assert.NotNull(token);

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        Assert.True(handler.CanReadToken(token));

        var jwt = handler.ReadJwtToken(token);
        Assert.Equal("user-123", jwt.Subject);
        Assert.Equal("contextflow-test", jwt.Issuer);
        Assert.Contains("contextflow-test-aud", jwt.Audiences);

        var roleClaims = jwt.Claims.Where(c => c.Type == "roles").Select(c => c.Value).ToList();
        Assert.Contains("Developer", roleClaims);
        Assert.Contains("Finance", roleClaims);

        var jti = jwt.Id;
        Assert.NotNull(jti);

        // Verify database persistence
        var dbToken = await db.PersonalAccessTokens.FirstOrDefaultAsync(t => t.Jti == jti);
        Assert.NotNull(dbToken);
        Assert.Equal("Test Token", dbToken.Name);
        Assert.Equal("user-123", dbToken.OwnerUserId);
        Assert.Contains("Developer", dbToken.Roles);
        Assert.Contains("Finance", dbToken.Roles);
        Assert.Null(dbToken.RevokedAt);
        Assert.True(dbToken.IsActive);
    }

    [Fact]
    public async Task MintTokenAsyncClampsLifetimeToMaximum90Days()
    {
        // Arrange
        var (service, db) = CreateService();

        // Act - request 180 days
        var token = await service.MintTokenAsync(
            ownerUserId: "user-90",
            name: "90 Day Cap Token",
            roles: ["Admin"],
            lifetime: TimeSpan.FromDays(180));

        // Assert
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var jwt = handler.ReadJwtToken(token);
        var jti = jwt.Id;

        var dbToken = await db.PersonalAccessTokens.FirstAsync(t => t.Jti == jti);
        var effectiveDuration = dbToken.ExpiresAt - dbToken.CreatedAt;

        // Effective duration should be clamped to 90 days (allowing minor margin for execution delay)
        Assert.True(effectiveDuration <= TimeSpan.FromDays(90.1));
    }

    [Fact]
    public async Task RevokeTokenAsyncUpdatesRevocationState()
    {
        // Arrange
        var (service, db) = CreateService();
        var token = await service.MintTokenAsync("user-456", "Token to Revoke", ["Admin"]);

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var jti = handler.ReadJwtToken(token).Id;

        // Pre-check
        Assert.False(await service.IsTokenRevokedAsync(jti));

        // Act
        var revokeResult = await service.RevokeTokenAsync(jti);

        // Assert
        Assert.True(revokeResult);
        Assert.True(await service.IsTokenRevokedAsync(jti));

        var dbToken = await db.PersonalAccessTokens.FirstAsync(t => t.Jti == jti);
        Assert.NotNull(dbToken.RevokedAt);
        Assert.False(dbToken.IsActive);
        Assert.True(dbToken.IsRevoked);
    }

    [Fact]
    public async Task RevokeTokenAsyncReturnsFalseForNonExistentToken()
    {
        // Arrange
        var (service, _) = CreateService();

        // Act
        var result = await service.RevokeTokenAsync("non-existent-jti");

        // Assert
        Assert.False(result);
    }
}


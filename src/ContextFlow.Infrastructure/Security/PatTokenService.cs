using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ContextFlow.Core.Entities;
using ContextFlow.Core.Interfaces;
using ContextFlow.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

namespace ContextFlow.Infrastructure.Security;

/// <summary>
/// Service responsible for minting, revoking, and validating Personal Access Tokens (PATs).
/// Tokens are HS256 JWTs carrying owner, role, and lifetime claims, backed by metadata in PostgreSQL
/// and optional revocation mirroring in Redis.
/// </summary>
public sealed partial class PatTokenService : IPatTokenService
{
    private const int _minimumKeySizeBytes = 32; // 256 bits
    private static readonly TimeSpan _maxTokenLifetime = TimeSpan.FromDays(90);
    private static readonly TimeSpan _defaultTokenLifetime = TimeSpan.FromDays(30);
    private const string _revokedTokenPrefix = "contextflow:revoked:";

    private readonly ContextFlowDbContext _dbContext;
    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<PatTokenService> _logger;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly SymmetricSecurityKey _signingKey;

    public PatTokenService(
        ContextFlowDbContext dbContext,
        IConfiguration configuration,
        ILogger<PatTokenService> logger,
        IConnectionMultiplexer? redis = null)
    {
        _dbContext = dbContext;
        _redis = redis;
        _logger = logger;

        _issuer = configuration["Jwt:Issuer"] ?? "contextflow";
        _audience = configuration["Jwt:Audience"] ?? "contextflow-gateway";

        var secret = configuration["Jwt:SigningKey"]
            ?? throw new InvalidOperationException("Configuration 'Jwt:SigningKey' must be configured.");

        var keyBytes = Encoding.UTF8.GetBytes(secret);
        if (keyBytes.Length < _minimumKeySizeBytes)
        {
            throw new InvalidOperationException(
                $"Configuration 'Jwt:SigningKey' must be at least {_minimumKeySizeBytes} bytes (256 bits). Current length is {keyBytes.Length} bytes.");
        }

        _signingKey = new SymmetricSecurityKey(keyBytes);
    }

    public async Task<string> MintTokenAsync(
        string ownerUserId,
        string name,
        IEnumerable<string> roles,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(roles);

        var rolesList = roles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (rolesList.Count == 0)
        {
            throw new ArgumentException("At least one role must be assigned to the token.", nameof(roles));
        }

        var effectiveLifetime = lifetime ?? _defaultTokenLifetime;
        if (effectiveLifetime <= TimeSpan.Zero || effectiveLifetime > _maxTokenLifetime)
        {
            effectiveLifetime = _maxTokenLifetime;
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(effectiveLifetime);
        var jti = Guid.NewGuid().ToString("N");

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, ownerUserId),
            new(JwtRegisteredClaimNames.Jti, jti)
        };

        foreach (var role in rolesList)
        {
            claims.Add(new Claim("roles", role));
        }

        var credentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256);
        var tokenDescriptor = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        var tokenHandler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var rawToken = tokenHandler.WriteToken(tokenDescriptor);

        var rolesFormatted = string.Join(",", rolesList);
        var patEntity = new PersonalAccessToken
        {
            Jti = jti,
            Name = name,
            OwnerUserId = ownerUserId,
            Roles = rolesFormatted,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            RevokedAt = null
        };

        _dbContext.PersonalAccessTokens.Add(patEntity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        LogTokenMinted(_logger, jti, ownerUserId, rolesFormatted, expiresAt);

        return rawToken;
    }

    public async Task<bool> RevokeTokenAsync(string jti, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti);

        var tokenEntity = await _dbContext.PersonalAccessTokens
            .FirstOrDefaultAsync(t => t.Jti == jti, cancellationToken);

        if (tokenEntity is null)
        {
            LogTokenNotFound(_logger, jti);
            return false;
        }

        if (tokenEntity.RevokedAt is not null)
        {
            return true; // Already revoked
        }

        var now = DateTimeOffset.UtcNow;
        tokenEntity.RevokedAt = now;
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Mirror to Redis if available
        if (_redis is not null && _redis.IsConnected)
        {
            try
            {
                var db = _redis.GetDatabase();
                var remainingTtl = tokenEntity.ExpiresAt > now ? tokenEntity.ExpiresAt - now : TimeSpan.FromMinutes(5);
                var redisKey = _revokedTokenPrefix + jti;
                await db.StringSetAsync(redisKey, "revoked", remainingTtl);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                LogRedisRevokeMirrorFailed(_logger, ex, jti);
            }
        }

        LogTokenRevoked(_logger, jti);
        return true;
    }

    public async Task<bool> IsTokenRevokedAsync(string jti, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti);

        // Fast-path check in Redis if available
        if (_redis is not null && _redis.IsConnected)
        {
            try
            {
                var db = _redis.GetDatabase();
                var redisKey = _revokedTokenPrefix + jti;
                if (await db.KeyExistsAsync(redisKey))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                LogRedisRevokeQueryFailed(_logger, ex, jti);
            }
        }

        // Authoritative fallback in PostgreSQL
        return await _dbContext.PersonalAccessTokens
            .AsNoTracking()
            .AnyAsync(t => t.Jti == jti && t.RevokedAt != null, cancellationToken);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Minted Personal Access Token {Jti} for user {UserId} with roles [{Roles}]. Expires at {ExpiresAt}")]
    private static partial void LogTokenMinted(ILogger logger, string jti, string userId, string roles, DateTimeOffset expiresAt);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Revocation failed: Token {Jti} was not found")]
    private static partial void LogTokenNotFound(ILogger logger, string jti);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Failed to mirror revocation of token {Jti} to Redis; database update succeeded")]
    private static partial void LogRedisRevokeMirrorFailed(ILogger logger, Exception ex, string jti);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Successfully revoked Personal Access Token {Jti}")]
    private static partial void LogTokenRevoked(ILogger logger, string jti);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Failed to query Redis for revocation of token {Jti}; falling back to database")]
    private static partial void LogRedisRevokeQueryFailed(ILogger logger, Exception ex, string jti);
}


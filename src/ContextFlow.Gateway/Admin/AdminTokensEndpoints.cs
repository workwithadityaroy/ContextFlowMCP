using System.IdentityModel.Tokens.Jwt;
using ContextFlow.Core.Entities;
using ContextFlow.Core.Interfaces;
using ContextFlow.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ContextFlow.Gateway.Admin;

/// <summary>
/// Admin REST API endpoints for listing, minting, and revoking Personal Access Tokens (PATs).
/// </summary>
public static class AdminTokensEndpoints
{
    private static readonly string[] _allowedRoles = ["Admin", "Developer", "Finance"];

    public static RouteGroupBuilder MapAdminTokensEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/tokens", GetTokensAsync);
        group.MapPost("/tokens", MintTokenAsync);
        group.MapDelete("/tokens/{jti}", RevokeTokenAsync);
        group.MapPost("/tokens/{jti}/revoke", RevokeTokenAsync);

        return group;
    }

    private static async Task<IResult> GetTokensAsync(
        ContextFlowDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.PersonalAccessTokens
            .AsNoTracking()
            .OrderByDescending(t => t.CreatedAt)
            .ThenBy(t => t.Jti)
            .ToListAsync(cancellationToken);

        var items = entities.Select(MapToDto).ToList();
        return TypedResults.Ok<IReadOnlyList<PersonalAccessTokenDto>>(items);
    }

    private static async Task<IResult> MintTokenAsync(
        MintTokenRequest request,
        IPatTokenService tokenService,
        ContextFlowDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
        {
            return Results.Problem(
                title: "Invalid token name.",
                detail: "Token name is required and must not exceed 128 characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(request.OwnerUserId) || request.OwnerUserId.Trim().Length > 128)
        {
            return Results.Problem(
                title: "Invalid owner user ID.",
                detail: "OwnerUserId is required and must not exceed 128 characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.Roles is null || request.Roles.Count == 0)
        {
            return Results.Problem(
                title: "Invalid roles.",
                detail: "At least one role must be specified. Allowed roles are: Admin, Developer, Finance.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var canonicalRoles = new List<string>(request.Roles.Count);
        foreach (var role in request.Roles)
        {
            if (string.IsNullOrWhiteSpace(role))
            {
                return Results.Problem(
                    title: "Invalid roles.",
                    detail: "Role names must not be empty. Allowed roles are: Admin, Developer, Finance.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var matchedRole = _allowedRoles.FirstOrDefault(
                allowed => string.Equals(allowed, role.Trim(), StringComparison.OrdinalIgnoreCase));
            if (matchedRole is null)
            {
                return Results.Problem(
                    title: "Invalid roles.",
                    detail: $"Role '{role}' is not allowed. Allowed roles are: Admin, Developer, Finance.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (!canonicalRoles.Contains(matchedRole, StringComparer.OrdinalIgnoreCase))
            {
                canonicalRoles.Add(matchedRole);
            }
        }

        var effectiveDays = request.LifetimeDays ?? 30;
        if (effectiveDays is < 1 or > 90)
        {
            return Results.Problem(
                title: "Invalid token lifetime.",
                detail: "LifetimeDays must be between 1 and 90 days.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var rawToken = await tokenService.MintTokenAsync(
            request.OwnerUserId.Trim(),
            request.Name.Trim(),
            canonicalRoles,
            TimeSpan.FromDays(effectiveDays),
            cancellationToken);

        var jti = new JwtSecurityTokenHandler().ReadJwtToken(rawToken).Id;
        var entity = await dbContext.PersonalAccessTokens
            .AsNoTracking()
            .FirstAsync(t => t.Jti == jti, cancellationToken);

        return Results.Created($"/api/tokens/{jti}", new MintedTokenResponse(rawToken, MapToDto(entity)));
    }

    private static async Task<IResult> RevokeTokenAsync(
        string jti,
        IPatTokenService tokenService,
        ContextFlowDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jti))
        {
            return Results.Problem(
                title: "Invalid token identifier.",
                detail: "Token JTI must not be empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var normalizedJti = jti.Trim();
        var revoked = await tokenService.RevokeTokenAsync(normalizedJti, cancellationToken);
        if (!revoked)
        {
            return Results.Problem(
                title: "Token not found.",
                detail: $"Personal Access Token with JTI '{normalizedJti}' was not found.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var entity = await dbContext.PersonalAccessTokens
            .AsNoTracking()
            .FirstAsync(t => t.Jti == normalizedJti, cancellationToken);

        return TypedResults.Ok(MapToDto(entity));
    }

    private static PersonalAccessTokenDto MapToDto(PersonalAccessToken entity)
    {
        IReadOnlyList<string> roles = string.IsNullOrWhiteSpace(entity.Roles)
            ? []
            : entity.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new PersonalAccessTokenDto(
            Jti: entity.Jti,
            Name: entity.Name,
            OwnerUserId: entity.OwnerUserId,
            Roles: roles,
            CreatedAt: entity.CreatedAt,
            ExpiresAt: entity.ExpiresAt,
            RevokedAt: entity.RevokedAt,
            IsRevoked: entity.IsRevoked,
            IsExpired: entity.IsExpired,
            IsActive: entity.IsActive);
    }
}

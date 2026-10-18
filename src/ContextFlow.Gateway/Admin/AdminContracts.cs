using System.Text.Json.Serialization;
using ContextFlow.Core.Enums;

namespace ContextFlow.Gateway.Admin;

/// <summary>
/// Single audit log item returned by <c>/api/logs</c>.
/// </summary>
public sealed record ToolExecutionLogDto(
    long Id,
    DateTimeOffset Timestamp,
    string UserId,
    string TokenId,
    string ToolName,
    [property: JsonConverter(typeof(JsonStringEnumConverter<ExecutionOutcome>))] ExecutionOutcome Outcome,
    long DurationMs,
    bool CacheHit,
    string? InputJson,
    string? OutputJson,
    int OutputBytes,
    string? ErrorType);

/// <summary>
/// Paged response wrapper for <c>GET /api/logs</c>.
/// </summary>
public sealed record PagedLogsResponse(
    IReadOnlyList<ToolExecutionLogDto> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

/// <summary>
/// Tool metadata and runtime governance policy returned by <c>/api/tools</c>.
/// </summary>
public sealed record ToolPolicyDto(
    string ToolName,
    string? Description,
    bool IsReadOnly,
    IReadOnlyList<string> AllowedRoles,
    bool IsEnabled,
    int MaxRequestsPerMinute,
    int CacheTtlSeconds,
    [property: JsonConverter(typeof(JsonStringEnumConverter<AuditMode>))] AuditMode AuditMode,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Request payload for <c>PUT /api/tools/{toolName}</c>.
/// </summary>
public sealed record UpdateToolPolicyRequest(
    bool IsEnabled,
    int MaxRequestsPerMinute,
    int CacheTtlSeconds,
    [property: JsonConverter(typeof(JsonStringEnumConverter<AuditMode>))] AuditMode AuditMode);

/// <summary>
/// Metadata representation of a Personal Access Token (never includes the raw token secret).
/// </summary>
public sealed record PersonalAccessTokenDto(
    string Jti,
    string Name,
    string OwnerUserId,
    IReadOnlyList<string> Roles,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    bool IsRevoked,
    bool IsExpired,
    bool IsActive);

/// <summary>
/// Request payload for <c>POST /api/tokens</c>.
/// </summary>
public sealed record MintTokenRequest(
    string Name,
    string OwnerUserId,
    IReadOnlyList<string> Roles,
    int? LifetimeDays = 30);

/// <summary>
/// One-time response returned by <c>POST /api/tokens</c> containing the raw signed JWT alongside its persisted metadata.
/// </summary>
public sealed record MintedTokenResponse(
    string Token,
    PersonalAccessTokenDto Metadata);

/// <summary>
/// Per-tool aggregated metrics returned inside <see cref="SystemMetricsResponse"/>.
/// </summary>
public sealed record ToolMetricsDto(
    string ToolName,
    int TotalCalls,
    int SuccessCount,
    int ErrorCount,
    int DeniedCount,
    int RateLimitedCount,
    int DisabledCount,
    int CacheHitCount,
    double ErrorRate,
    double CacheHitRate,
    double AverageDurationMs,
    long P95DurationMs);

/// <summary>
/// Aggregated governance telemetry returned by <c>GET /api/metrics</c>.
/// Exposes zero hostnames, versions, or connection strings per context.md §4 &amp; §6.
/// </summary>
public sealed record SystemMetricsResponse(
    string Status,
    bool DatabaseHealthy,
    bool RedisConfigured,
    bool RedisConnected,
    int TotalCalls,
    int SuccessCount,
    int ErrorCount,
    int DeniedCount,
    int RateLimitedCount,
    int DisabledCount,
    int CacheHitCount,
    double ErrorRate,
    double CacheHitRate,
    long TotalOutputBytes,
    long DroppedAuditRecords,
    DateTimeOffset TimestampUtc,
    IReadOnlyList<ToolMetricsDto> PerTool);


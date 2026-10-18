using System.Diagnostics.CodeAnalysis;
using ContextFlow.Core.Enums;
using ContextFlow.Core.Interfaces;
using ContextFlow.Infrastructure.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace ContextFlow.Gateway.Admin;

/// <summary>
/// Admin REST endpoint for system health and aggregated governance telemetry (<c>/api/metrics</c>).
/// Exposes zero hostnames, versions, or connection strings per context.md §4 &amp; §6.
/// </summary>
public static class AdminMetricsEndpoints
{
    public static RouteGroupBuilder MapAdminMetricsEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/metrics", GetMetricsAsync);

        return group;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Database connectivity check must degrade gracefully to false without throwing.")]
    public static async Task<IResult> GetMetricsAsync(
        ContextFlowDbContext dbContext,
        IAuditQueue auditQueue,
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(auditQueue);
        ArgumentNullException.ThrowIfNull(services);

        var redis = services.GetService<IConnectionMultiplexer>();
        var redisConfigured = redis is not null;
        var redisConnected = redis is { IsConnected: true };

        bool databaseHealthy;
        try
        {
            databaseHealthy = await dbContext.Database.CanConnectAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            databaseHealthy = false;
        }

        var logs = await dbContext.ToolExecutionLogs
            .AsNoTracking()
            .Select(l => new
            {
                l.ToolName,
                l.Outcome,
                l.DurationMs,
                l.CacheHit,
                l.OutputBytes
            })
            .ToListAsync(cancellationToken);

        var totalCalls = logs.Count;
        var successCount = logs.Count(l => l.Outcome == ExecutionOutcome.Success);
        var errorCount = logs.Count(l => l.Outcome != ExecutionOutcome.Success);
        var deniedCount = logs.Count(l => l.Outcome == ExecutionOutcome.Denied);
        var rateLimitedCount = logs.Count(l => l.Outcome == ExecutionOutcome.RateLimited);
        var disabledCount = logs.Count(l => l.Outcome == ExecutionOutcome.Disabled);
        var cacheHitCount = logs.Count(l => l.CacheHit);

        var errorRate = totalCalls == 0 ? 0.0 : (double)errorCount / totalCalls;
        var cacheHitRate = totalCalls == 0 ? 0.0 : (double)cacheHitCount / totalCalls;
        var totalOutputBytes = logs.Sum(l => (long)l.OutputBytes);
        var droppedAuditRecords = auditQueue.DroppedRecordsCount;

        var perTool = logs
            .GroupBy(l => l.ToolName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var groupItems = group.ToList();
                var toolTotal = groupItems.Count;
                var toolSuccess = groupItems.Count(l => l.Outcome == ExecutionOutcome.Success);
                var toolError = groupItems.Count(l => l.Outcome != ExecutionOutcome.Success);
                var toolDenied = groupItems.Count(l => l.Outcome == ExecutionOutcome.Denied);
                var toolRateLimited = groupItems.Count(l => l.Outcome == ExecutionOutcome.RateLimited);
                var toolDisabled = groupItems.Count(l => l.Outcome == ExecutionOutcome.Disabled);
                var toolCacheHit = groupItems.Count(l => l.CacheHit);

                var toolErrorRate = toolTotal == 0 ? 0.0 : (double)toolError / toolTotal;
                var toolCacheHitRate = toolTotal == 0 ? 0.0 : (double)toolCacheHit / toolTotal;
                var avgDurationMs = toolTotal == 0 ? 0.0 : groupItems.Average(l => l.DurationMs);

                var sortedDurations = groupItems
                    .Select(l => l.DurationMs)
                    .OrderBy(d => d)
                    .ToArray();

                var p95DurationMs = ComputePercentile(sortedDurations, 0.95);

                return new ToolMetricsDto(
                    ToolName: group.Key,
                    TotalCalls: toolTotal,
                    SuccessCount: toolSuccess,
                    ErrorCount: toolError,
                    DeniedCount: toolDenied,
                    RateLimitedCount: toolRateLimited,
                    DisabledCount: toolDisabled,
                    CacheHitCount: toolCacheHit,
                    ErrorRate: toolErrorRate,
                    CacheHitRate: toolCacheHitRate,
                    AverageDurationMs: avgDurationMs,
                    P95DurationMs: p95DurationMs);
            })
            .ToList();

        var status = databaseHealthy && (!redisConfigured || redisConnected)
            ? "healthy"
            : "degraded";

        var response = new SystemMetricsResponse(
            Status: status,
            DatabaseHealthy: databaseHealthy,
            RedisConfigured: redisConfigured,
            RedisConnected: redisConnected,
            TotalCalls: totalCalls,
            SuccessCount: successCount,
            ErrorCount: errorCount,
            DeniedCount: deniedCount,
            RateLimitedCount: rateLimitedCount,
            DisabledCount: disabledCount,
            CacheHitCount: cacheHitCount,
            ErrorRate: errorRate,
            CacheHitRate: cacheHitRate,
            TotalOutputBytes: totalOutputBytes,
            DroppedAuditRecords: droppedAuditRecords,
            TimestampUtc: DateTimeOffset.UtcNow,
            PerTool: perTool);

        return TypedResults.Ok(response);
    }

    public static long ComputePercentile(IReadOnlyList<long> sortedValues, double percentile)
    {
        ArgumentNullException.ThrowIfNull(sortedValues);

        if (sortedValues.Count == 0)
        {
            return 0;
        }

        var clampedPercentile = Math.Clamp(percentile, 0.0, 1.0);
        var index = (int)Math.Ceiling(clampedPercentile * sortedValues.Count) - 1;
        var clampedIndex = Math.Clamp(index, 0, sortedValues.Count - 1);

        return sortedValues[clampedIndex];
    }
}

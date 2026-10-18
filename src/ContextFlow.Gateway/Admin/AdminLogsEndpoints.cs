using ContextFlow.Core.Entities;
using ContextFlow.Core.Enums;
using ContextFlow.Infrastructure.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace ContextFlow.Gateway.Admin;

/// <summary>
/// Admin REST endpoints for querying and inspecting persisted tool execution audit logs (<c>/api/logs</c>).
/// </summary>
public static class AdminLogsEndpoints
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public static RouteGroupBuilder MapAdminLogsEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/logs", GetLogsAsync);
        group.MapGet("/logs/{id:long}", GetLogByIdAsync);

        return group;
    }

    public static async Task<IResult> GetLogsAsync(
        ContextFlowDbContext dbContext,
        int? page = null,
        int? pageSize = null,
        string? outcome = null,
        string? toolName = null,
        string? tool = null,
        string? userId = null,
        string? user = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var effectivePage = page ?? DefaultPage;
        if (effectivePage < 1)
        {
            return Results.Problem(
                title: "Invalid pagination parameters.",
                detail: "Query parameter 'page' must be at least 1.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var effectivePageSize = pageSize ?? DefaultPageSize;
        if (effectivePageSize is < 1 or > MaxPageSize)
        {
            return Results.Problem(
                title: "Invalid pagination parameters.",
                detail: "Query parameter 'pageSize' must be between 1 and 200.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        ExecutionOutcome? parsedOutcomeFilter = null;
        if (!string.IsNullOrWhiteSpace(outcome))
        {
            if (!Enum.TryParse<ExecutionOutcome>(outcome.Trim(), ignoreCase: true, out var parsedOutcome)
                || !Enum.IsDefined(parsedOutcome))
            {
                return Results.Problem(
                    title: "Invalid filter parameter.",
                    detail: $"Query parameter 'outcome' has an invalid value '{outcome}'.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            parsedOutcomeFilter = parsedOutcome;
        }

        if (from.HasValue && to.HasValue && from.Value > to.Value)
        {
            return Results.Problem(
                title: "Invalid date range.",
                detail: "Query parameter 'from' must be earlier than or equal to 'to'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        IQueryable<ToolExecutionLog> query = dbContext.ToolExecutionLogs.AsNoTracking();

        if (parsedOutcomeFilter.HasValue)
        {
            var targetOutcome = parsedOutcomeFilter.Value;
            query = query.Where(l => l.Outcome == targetOutcome);
        }

        var effectiveToolName = !string.IsNullOrWhiteSpace(toolName)
            ? toolName.Trim()
            : (!string.IsNullOrWhiteSpace(tool) ? tool.Trim() : null);

        if (effectiveToolName is not null)
        {
            query = query.Where(l => l.ToolName == effectiveToolName);
        }

        var effectiveUserId = !string.IsNullOrWhiteSpace(userId)
            ? userId.Trim()
            : (!string.IsNullOrWhiteSpace(user) ? user.Trim() : null);

        if (effectiveUserId is not null)
        {
            query = query.Where(l => l.UserId == effectiveUserId);
        }

        if (from.HasValue)
        {
            var fromTimestamp = from.Value;
            query = query.Where(l => l.Timestamp >= fromTimestamp);
        }

        if (to.HasValue)
        {
            var toTimestamp = to.Value;
            query = query.Where(l => l.Timestamp <= toTimestamp);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(l => l.Timestamp)
            .ThenByDescending(l => l.Id)
            .Skip((effectivePage - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .Select(l => new ToolExecutionLogDto(
                l.Id,
                l.Timestamp,
                l.UserId,
                l.TokenId,
                l.ToolName,
                l.Outcome,
                l.DurationMs,
                l.CacheHit,
                l.InputJson,
                l.OutputJson,
                l.OutputBytes,
                l.ErrorType))
            .ToListAsync(cancellationToken);

        var totalPages = totalCount == 0
            ? 0
            : (int)Math.Ceiling(totalCount / (double)effectivePageSize);

        return TypedResults.Ok(new PagedLogsResponse(
            items,
            effectivePage,
            effectivePageSize,
            totalCount,
            totalPages));
    }

    public static async Task<IResult> GetLogByIdAsync(
        long id,
        ContextFlowDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var log = await dbContext.ToolExecutionLogs
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken);

        if (log is null)
        {
            return Results.Problem(
                title: "Audit log not found.",
                detail: $"Audit log entry with ID '{id}' was not found.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return TypedResults.Ok(MapToDto(log));
    }

    public static ToolExecutionLogDto MapToDto(ToolExecutionLog log)
    {
        ArgumentNullException.ThrowIfNull(log);

        return new ToolExecutionLogDto(
            log.Id,
            log.Timestamp,
            log.UserId,
            log.TokenId,
            log.ToolName,
            log.Outcome,
            log.DurationMs,
            log.CacheHit,
            log.InputJson,
            log.OutputJson,
            log.OutputBytes,
            log.ErrorType);
    }
}

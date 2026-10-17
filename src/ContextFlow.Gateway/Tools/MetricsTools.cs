using System.ComponentModel;
using System.Text.Json;
using ContextFlow.Core.Enums;
using ContextFlow.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ContextFlow.Gateway.Tools;

[McpServerToolType]
public sealed class MetricsTools
{
    [McpServerTool]
    [Authorize(Roles = "Admin,Developer")]
    [Description("Queries aggregate MCP server execution metrics (total calls, success rate, error count) from governance logs. Available only to Admin and Developer roles.")]
    public static async Task<CallToolResult> QuerySystemMetrics(
        ContextFlowDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        var totalCalls = await dbContext.ToolExecutionLogs
            .AsNoTracking()
            .CountAsync(cancellationToken);

        var errorCalls = await dbContext.ToolExecutionLogs
            .AsNoTracking()
            .CountAsync(l => l.Outcome != ExecutionOutcome.Success, cancellationToken);

        var metricsJson = JsonSerializer.Serialize(new
        {
            status = "healthy",
            totalLoggedCalls = totalCalls,
            totalErrors = errorCalls,
            successRate = totalCalls > 0 ? (double)(totalCalls - errorCalls) / totalCalls : 1.0,
            timestampUtc = DateTimeOffset.UtcNow
        });

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = metricsJson }]
        };
    }
}

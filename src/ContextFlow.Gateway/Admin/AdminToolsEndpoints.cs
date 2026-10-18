using ContextFlow.Core.Entities;
using ContextFlow.Core.Enums;
using ContextFlow.Core.Interfaces;
using ContextFlow.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace ContextFlow.Gateway.Admin;

/// <summary>
/// Admin REST API endpoints for viewing and updating MCP tool governance policies.
/// </summary>
public static class AdminToolsEndpoints
{
    public static RouteGroupBuilder MapAdminToolsEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/tools", GetToolsAsync);
        group.MapPut("/tools/{toolName}", UpdateToolPolicyAsync);

        return group;
    }

    public static async Task<IResult> GetToolsAsync(
        IToolPolicyProvider policyProvider,
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var policies = await policyProvider.GetAllPoliciesAsync(cancellationToken);
        var registeredTools = services.GetServices<McpServerTool>()
            .Where(t => !string.IsNullOrWhiteSpace(t.ProtocolTool.Name))
            .ToDictionary(t => t.ProtocolTool.Name, t => t, StringComparer.OrdinalIgnoreCase);

        var allToolNames = registeredTools.Keys
            .Union(policies.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

        var items = new List<ToolPolicyDto>();
        foreach (var name in allToolNames)
        {
            registeredTools.TryGetValue(name, out var tool);
            policies.TryGetValue(name, out var policy);

            var canonicalName = tool?.ProtocolTool.Name ?? policy?.ToolName ?? name;
            items.Add(MapToDto(canonicalName, tool, policy));
        }

        return TypedResults.Ok<IReadOnlyList<ToolPolicyDto>>(items);
    }

    public static async Task<IResult> UpdateToolPolicyAsync(
        string toolName,
        UpdateToolPolicyRequest request,
        ContextFlowDbContext dbContext,
        IToolPolicyProvider policyProvider,
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return Results.Problem(
                title: "Invalid tool name.",
                detail: "Tool name must not be empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.MaxRequestsPerMinute is < 1 or > 10_000)
        {
            return Results.Problem(
                title: "Invalid rate limit.",
                detail: "MaxRequestsPerMinute must be between 1 and 10,000.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.CacheTtlSeconds is < 0 or > 86_400)
        {
            return Results.Problem(
                title: "Invalid cache TTL.",
                detail: "CacheTtlSeconds must be between 0 and 86,400.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!Enum.IsDefined(request.AuditMode))
        {
            return Results.Problem(
                title: "Invalid audit mode.",
                detail: "AuditMode must be a defined AuditMode value.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var trimmedToolName = toolName.Trim();
        var registeredTool = services.GetServices<McpServerTool>()
            .FirstOrDefault(t => string.Equals(t.ProtocolTool.Name, trimmedToolName, StringComparison.OrdinalIgnoreCase));

        var allPolicies = await dbContext.ToolPolicies.ToListAsync(cancellationToken);
        var existingPolicy = allPolicies.FirstOrDefault(
            p => string.Equals(p.ToolName, trimmedToolName, StringComparison.OrdinalIgnoreCase));

        if (registeredTool is null && existingPolicy is null)
        {
            return Results.Problem(
                title: "Tool not found.",
                detail: $"Tool '{trimmedToolName}' is not registered.",
                statusCode: StatusCodes.Status404NotFound);
        }

        if (registeredTool is not null
            && registeredTool.ProtocolTool.Annotations?.ReadOnlyHint != true
            && request.CacheTtlSeconds > 0)
        {
            return Results.Problem(
                title: "Invalid cache policy.",
                detail: "Only read-only tools may configure CacheTtlSeconds greater than 0.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var canonicalToolName = existingPolicy?.ToolName ?? registeredTool!.ProtocolTool.Name;
        var now = DateTimeOffset.UtcNow;

        if (existingPolicy is null)
        {
            existingPolicy = new ToolPolicy
            {
                ToolName = canonicalToolName,
                IsEnabled = request.IsEnabled,
                MaxRequestsPerMinute = request.MaxRequestsPerMinute,
                CacheTtlSeconds = request.CacheTtlSeconds,
                AuditMode = request.AuditMode,
                UpdatedAt = now
            };
            dbContext.ToolPolicies.Add(existingPolicy);
        }
        else
        {
            existingPolicy.IsEnabled = request.IsEnabled;
            existingPolicy.MaxRequestsPerMinute = request.MaxRequestsPerMinute;
            existingPolicy.CacheTtlSeconds = request.CacheTtlSeconds;
            existingPolicy.AuditMode = request.AuditMode;
            existingPolicy.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await policyProvider.InvalidatePolicyCacheAsync(canonicalToolName, cancellationToken);

        var updatedDto = MapToDto(canonicalToolName, registeredTool, existingPolicy);
        return TypedResults.Ok(updatedDto);
    }

    private static ToolPolicyDto MapToDto(string toolName, McpServerTool? tool, ToolPolicy? policy) =>
        new(
            ToolName: toolName,
            Description: tool?.ProtocolTool.Description,
            IsReadOnly: tool?.ProtocolTool.Annotations?.ReadOnlyHint == true,
            AllowedRoles: ExtractAllowedRoles(tool),
            IsEnabled: policy?.IsEnabled ?? true,
            MaxRequestsPerMinute: policy?.MaxRequestsPerMinute ?? 60,
            CacheTtlSeconds: policy?.CacheTtlSeconds ?? 0,
            AuditMode: policy?.AuditMode ?? AuditMode.Full,
            UpdatedAt: policy?.UpdatedAt ?? DateTimeOffset.UtcNow);

    private static List<string> ExtractAllowedRoles(McpServerTool? tool)
    {
        if (tool?.Metadata is null || tool.Metadata.Count == 0)
        {
            return [];
        }

        return tool.Metadata
            .OfType<AuthorizeAttribute>()
            .Where(a => !string.IsNullOrWhiteSpace(a.Roles))
            .SelectMany(a => a.Roles!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

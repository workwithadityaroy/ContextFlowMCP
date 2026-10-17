using ContextFlow.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ContextFlow.Gateway.Filters;

/// <summary>
/// MCP filter governing tool visibility and execution based on dynamic runtime ToolPolicy.
/// Hides disabled tools from tools/list and rejects direct tools/call attempts.
/// </summary>
public static class ToolPolicyFilter
{
    public static McpRequestHandler<ListToolsRequestParams, ListToolsResult> FilterListTools(
        McpRequestHandler<ListToolsRequestParams, ListToolsResult> next) =>
        async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken);
            if (result.Tools is null || result.Tools.Count == 0)
            {
                return result;
            }

            var services = request.Services ?? throw new InvalidOperationException("Service provider is unavailable.");
            var policyProvider = services.GetRequiredService<IToolPolicyProvider>();

            var filteredTools = new List<Tool>(result.Tools.Count);
            foreach (var tool in result.Tools)
            {
                if (string.IsNullOrWhiteSpace(tool.Name))
                {
                    continue;
                }

                var policy = await policyProvider.GetPolicyAsync(tool.Name, cancellationToken);
                if (policy is null || policy.IsEnabled)
                {
                    filteredTools.Add(tool);
                }
            }

            return new ListToolsResult
            {
                Tools = filteredTools,
                NextCursor = result.NextCursor,
                Meta = result.Meta
            };
        };

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> FilterCallTool(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            var toolName = request.Params?.Name;
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return await next(request, cancellationToken);
            }

            var services = request.Services ?? throw new InvalidOperationException("Service provider is unavailable.");
            var policyProvider = services.GetRequiredService<IToolPolicyProvider>();
            var policy = await policyProvider.GetPolicyAsync(toolName, cancellationToken);

            if (policy is not null && !policy.IsEnabled)
            {
                return new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = $"Tool '{toolName}' is currently disabled by administrator policy." }]
                };
            }

            return await next(request, cancellationToken);
        };
}

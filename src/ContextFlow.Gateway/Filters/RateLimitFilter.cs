using System.Security.Claims;
using ContextFlow.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ContextFlow.Gateway.Filters;

/// <summary>
/// MCP filter enforcing sliding-window rate limits per (user, tool) via IRateLimiter.
/// Returns actionable CallToolResult with IsError=true and retry-after seconds when throttled.
/// </summary>
public static class RateLimitFilter
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> FilterCallTool(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            var toolName = request.Params?.Name;
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return await next(request, cancellationToken);
            }

            var userId = request.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? request.User?.FindFirst("sub")?.Value
                ?? "anonymous";

            var services = request.Services ?? throw new InvalidOperationException("Service provider is unavailable.");
            var policyProvider = services.GetRequiredService<IToolPolicyProvider>();
            var policy = await policyProvider.GetPolicyAsync(toolName, cancellationToken);

            var maxRequestsPerMinute = policy?.MaxRequestsPerMinute ?? 60;
            if (maxRequestsPerMinute > 0)
            {
                var rateLimiter = services.GetRequiredService<IRateLimiter>();
                var rateLimitResult = await rateLimiter.CheckRateLimitAsync(
                    userId,
                    toolName,
                    maxRequestsPerMinute,
                    cancellationToken);

                if (!rateLimitResult.IsAllowed)
                {
                    return new CallToolResult
                    {
                        IsError = true,
                        Content = [new TextContentBlock { Text = $"Quota exceeded for tool '{toolName}'. Retry after {rateLimitResult.RetryAfterSeconds} seconds." }]
                    };
                }
            }

            return await next(request, cancellationToken);
        };
}

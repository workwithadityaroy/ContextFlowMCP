using ContextFlow.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ContextFlow.Gateway.Filters;

/// <summary>
/// MCP filter that trims tool execution results by removing null properties from JSON payloads
/// and collapsing redundant whitespace, reducing token consumption and storage overhead.
/// </summary>
public static class TrimFilter
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> FilterCallTool(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken);
            if (result.Content is null || result.Content.Count == 0)
            {
                return result;
            }

            var services = request.Services ?? throw new InvalidOperationException("Service provider is unavailable.");
            var trimmer = services.GetRequiredService<IContextTrimmer>();

            var totalOriginalBytes = 0;
            var totalTrimmedBytes = 0;

            for (var i = 0; i < result.Content.Count; i++)
            {
                if (result.Content[i] is TextContentBlock textBlock && !string.IsNullOrEmpty(textBlock.Text))
                {
                    var trimResult = trimmer.Trim(textBlock.Text);
                    textBlock.Text = trimResult.Content;
                    totalOriginalBytes += trimResult.OriginalBytes;
                    totalTrimmedBytes += trimResult.TrimmedBytes;
                }
            }

            result.Meta ??= [];
            result.Meta["rawBytes"] = totalOriginalBytes;
            result.Meta["trimmedBytes"] = totalTrimmedBytes;
            result.Meta["bytesSaved"] = Math.Max(0, totalOriginalBytes - totalTrimmedBytes);

            return result;
        };
}

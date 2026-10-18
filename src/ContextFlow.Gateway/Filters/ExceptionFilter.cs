using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ContextFlow.Gateway.Filters;

/// <summary>
/// Innermost MCP filter (registered after <see cref="TrimFilter"/>) that converts unhandled tool exceptions
/// into a sanitized <see cref="CallToolResult"/> with <c>IsError = true</c>. The full exception is logged
/// server-side only; the client never sees exception messages, type names, stack traces, paths or connection
/// details. Cancellation is never swallowed.
/// </summary>
public static partial class ExceptionFilter
{
    /// <summary>
    /// Generic message returned to clients. Must not contain any keyword used by
    /// <see cref="AuditFilter.ClassifyResult"/> for non-error outcomes ("Quota exceeded", "is disabled",
    /// "forbidden", "unauthorized") so that the audit filter records <c>ExecutionOutcome.Error</c>.
    /// </summary>
    public const string GenericErrorMessage = "An internal error occurred while processing the tool request.";

    private const string LoggerCategory = "ContextFlow.Gateway.Filters.ExceptionFilter";

    private const string SdkInvocationErrorPrefix = "An error occurred invoking '";

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> FilterCallTool(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            try
            {
                var result = await next(request, cancellationToken);
                if (IsUnhandledSdkToolExceptionResult(result))
                {
                    return CreateGenericErrorResult();
                }

                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TryLog(request, ex);
                return CreateGenericErrorResult();
            }
        };

    private static bool IsUnhandledSdkToolExceptionResult(CallToolResult result) =>
        result.IsError == true
        && result.Content is [TextContentBlock { Text: not null } block]
        && block.Text.StartsWith(SdkInvocationErrorPrefix, StringComparison.Ordinal);

    private static CallToolResult CreateGenericErrorResult() => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = GenericErrorMessage }]
    };

    private static void TryLog(RequestContext<CallToolRequestParams> request, Exception exception)
    {
        try
        {
            var loggerFactory = request.Services?.GetService<ILoggerFactory>();
            if (loggerFactory is null)
            {
                return;
            }

            var logger = loggerFactory.CreateLogger(LoggerCategory);
            LogToolException(logger, request.Params?.Name ?? "unknown", exception);
        }
        catch (Exception)
        {
            // Logging must never turn a sanitized error into a failure.
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "Unhandled exception while executing tool '{ToolName}'.")]
    private static partial void LogToolException(ILogger logger, string toolName, Exception exception);
}

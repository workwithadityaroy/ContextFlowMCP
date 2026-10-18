using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContextFlow.Core.Entities;
using ContextFlow.Core.Enums;
using ContextFlow.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ContextFlow.Gateway.Filters;

/// <summary>
/// Outermost MCP filter that audits every tools/call request, capturing user identity,
/// execution duration, cache hits, outcome classifications (success, denied, rate-limited,
/// disabled, tool error), and redacted payload json according to per-tool AuditMode.
/// </summary>
public static partial class AuditFilter
{
#pragma warning disable MCPEXP002
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> FilterCallTool(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            var resultOrAlternate = await FilterCallToolWithAlternate(
                request,
                async (req, ct) => await next(req, ct),
                cancellationToken);

            return resultOrAlternate.Result ?? new CallToolResult();
        };

    public static async ValueTask<ResultOrAlternate<CallToolResult>> FilterCallToolWithAlternate(
        RequestContext<CallToolRequestParams> request,
        McpRequestHandler<CallToolRequestParams, ResultOrAlternate<CallToolResult>> next,
        CancellationToken cancellationToken)
    {
        var toolName = request.Params?.Name ?? "unknown";
        var userId = request.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? request.User?.FindFirst("sub")?.Value
            ?? "anonymous";
        var tokenId = request.User?.FindFirst("jti")?.Value ?? "no-token";

        var services = request.Services ?? throw new InvalidOperationException("Service provider is unavailable.");
        var policyProvider = services.GetRequiredService<IToolPolicyProvider>();
        var auditQueue = services.GetRequiredService<IAuditQueue>();
        var redactor = services.GetRequiredService<IRedactor>();

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var resultOrAlternate = await next(request, cancellationToken);
            stopwatch.Stop();

            var policy = await policyProvider.GetPolicyAsync(toolName, cancellationToken);
            var auditMode = policy?.AuditMode ?? AuditMode.Full;
            var inputJson = ProcessInput(request.Params?.Arguments, auditMode, redactor);

            if (!resultOrAlternate.IsAlternate && resultOrAlternate.Result is not null)
            {
                var result = resultOrAlternate.Result;
                var (outcome, errorType) = ClassifyResult(result);
                var cacheHit = outcome == ExecutionOutcome.Success && DetectCacheHit(result);
                var (outputJson, outputBytes) = outcome == ExecutionOutcome.Success
                    ? ProcessOutput(result, auditMode, redactor)
                    : (null, 0);

                auditQueue.TryEnqueue(new ToolExecutionLog
                {
                    UserId = userId,
                    TokenId = tokenId,
                    ToolName = toolName,
                    Outcome = outcome,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    CacheHit = cacheHit,
                    InputJson = inputJson,
                    OutputJson = outputJson,
                    OutputBytes = outputBytes,
                    ErrorType = errorType,
                    Timestamp = DateTimeOffset.UtcNow
                });
            }
            else
            {
                auditQueue.TryEnqueue(new ToolExecutionLog
                {
                    UserId = userId,
                    TokenId = tokenId,
                    ToolName = toolName,
                    Outcome = ExecutionOutcome.Success,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    CacheHit = false,
                    InputJson = inputJson,
                    OutputJson = null,
                    OutputBytes = 0,
                    ErrorType = null,
                    Timestamp = DateTimeOffset.UtcNow
                });
            }

            return resultOrAlternate;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            var policy = await policyProvider.GetPolicyAsync(toolName, CancellationToken.None);
            var auditMode = policy?.AuditMode ?? AuditMode.Full;
            var inputJson = ProcessInput(request.Params?.Arguments, auditMode, redactor);
            var (outcome, errorType) = ClassifyException(ex);

            auditQueue.TryEnqueue(new ToolExecutionLog
            {
                UserId = userId,
                TokenId = tokenId,
                ToolName = toolName,
                Outcome = outcome,
                DurationMs = stopwatch.ElapsedMilliseconds,
                CacheHit = false,
                InputJson = inputJson,
                OutputJson = null,
                OutputBytes = 0,
                ErrorType = errorType,
                Timestamp = DateTimeOffset.UtcNow
            });

            throw;
        }
    }
#pragma warning restore MCPEXP002

    public static (ExecutionOutcome Outcome, string? ErrorType) ClassifyResult(CallToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsError != true)
        {
            return (ExecutionOutcome.Success, null);
        }

        var errorText = result.Content is not null
            ? string.Join(" ", result.Content.OfType<TextContentBlock>().Select(b => b.Text))
            : string.Empty;

        if (errorText.Contains("Quota exceeded", StringComparison.OrdinalIgnoreCase))
        {
            return (ExecutionOutcome.RateLimited, "RateLimited");
        }

        if (errorText.Contains("is disabled", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("is currently disabled", StringComparison.OrdinalIgnoreCase))
        {
            return (ExecutionOutcome.Disabled, "Disabled");
        }

        if (errorText.Contains("Access forbidden", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("forbidden", StringComparison.OrdinalIgnoreCase))
        {
            return (ExecutionOutcome.Denied, "Forbidden");
        }

        return (ExecutionOutcome.Error, "ToolError");
    }

    public static (ExecutionOutcome Outcome, string? ErrorType) ClassifyException(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (ex is UnauthorizedAccessException
            || ex.Message.Contains("Access forbidden", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return (ExecutionOutcome.Denied, "Forbidden");
        }

        return (ExecutionOutcome.Error, ex.GetType().Name);
    }

    public static bool DetectCacheHit(CallToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Meta is null)
        {
            return false;
        }

        if (result.Meta.TryGetPropertyValue("cacheHit", out var hitNode) && hitNode is not null)
        {
            if (hitNode.GetValueKind() == JsonValueKind.True)
            {
                return true;
            }

            if (hitNode is JsonValue jv && jv.TryGetValue<bool>(out var b) && b)
            {
                return true;
            }
        }

        return false;
    }

    public static string? ProcessInput(object? arguments, AuditMode auditMode, IRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);

        if (auditMode == AuditMode.MetadataOnly || arguments is null)
        {
            return null;
        }

        var rawInput = JsonSerializer.Serialize(arguments);
        return redactor.Redact(rawInput);
    }

    public static (string? OutputJson, int OutputBytes) ProcessOutput(CallToolResult result, AuditMode auditMode, IRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(redactor);

        if (auditMode == AuditMode.MetadataOnly)
        {
            return (null, 0);
        }

        if (auditMode == AuditMode.InputsOnly)
        {
            if (result.Content is not null && result.Content.Count > 0)
            {
                var rawText = string.Join(string.Empty, result.Content.OfType<TextContentBlock>().Select(b => b.Text));
                var bytes = !string.IsNullOrEmpty(rawText)
                    ? Encoding.UTF8.GetByteCount(rawText)
                    : Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result.Content));
                return (null, bytes);
            }

            return (null, 0);
        }

        // AuditMode.Full
        if (result.Content is not null)
        {
            var rawOutput = JsonSerializer.Serialize(result.Content);
            var outputJson = redactor.Redact(rawOutput);
            var outputBytes = Encoding.UTF8.GetByteCount(outputJson ?? string.Empty);
            return (outputJson, outputBytes);
        }

        return (null, 0);
    }
}

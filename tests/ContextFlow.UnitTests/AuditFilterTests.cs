using System.Text.Json.Nodes;
using ContextFlow.Core.Enums;
using ContextFlow.Core.Interfaces;
using ContextFlow.Gateway.Filters;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ContextFlow.UnitTests;

public sealed class AuditFilterTests
{
    private sealed class PassthroughRedactor : IRedactor
    {
        public string? Redact(string? input) => input;
    }

    private sealed class MaskingRedactor : IRedactor
    {
        public string? Redact(string? input) => input?.Replace("secret_password", "[REDACTED]", StringComparison.Ordinal);
    }

    [Fact]
    public void ClassifyResultThrowsWhenResultIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => AuditFilter.ClassifyResult(null!));
    }

    [Fact]
    public void ClassifyResultIdentifiesSuccessfulExecution()
    {
        var result = new CallToolResult
        {
            IsError = false,
            Content = [new TextContentBlock { Text = "hello world" }]
        };

        var (outcome, errorType) = AuditFilter.ClassifyResult(result);

        Assert.Equal(ExecutionOutcome.Success, outcome);
        Assert.Null(errorType);
    }

    [Fact]
    public void ClassifyResultIdentifiesRateLimitedOutcome()
    {
        var result = new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "Quota exceeded for tool 'echo'. Retry after 15 seconds." }]
        };

        var (outcome, errorType) = AuditFilter.ClassifyResult(result);

        Assert.Equal(ExecutionOutcome.RateLimited, outcome);
        Assert.Equal("RateLimited", errorType);
    }

    [Fact]
    public void ClassifyResultIdentifiesDisabledOutcome()
    {
        var result = new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "Tool 'echo' is currently disabled by administrator policy." }]
        };

        var (outcome, errorType) = AuditFilter.ClassifyResult(result);

        Assert.Equal(ExecutionOutcome.Disabled, outcome);
        Assert.Equal("Disabled", errorType);
    }

    [Theory]
    [InlineData("Access forbidden for tool 'get_employee_profile'.")]
    [InlineData("Caller is unauthorized to execute this action.")]
    public void ClassifyResultIdentifiesDeniedOutcome(string message)
    {
        var result = new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = message }]
        };

        var (outcome, errorType) = AuditFilter.ClassifyResult(result);

        Assert.Equal(ExecutionOutcome.Denied, outcome);
        Assert.Equal("Forbidden", errorType);
    }

    [Fact]
    public void ClassifyResultIdentifiesGeneralToolErrorOutcome()
    {
        var result = new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "External FX service connection timed out." }]
        };

        var (outcome, errorType) = AuditFilter.ClassifyResult(result);

        Assert.Equal(ExecutionOutcome.Error, outcome);
        Assert.Equal("ToolError", errorType);
    }

    [Fact]
    public void ClassifyExceptionThrowsWhenExceptionIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => AuditFilter.ClassifyException(null!));
    }

    [Fact]
    public void ClassifyExceptionIdentifiesMcpProtocolAccessForbiddenAsDenied()
    {
        var ex = new McpProtocolException("Access forbidden: required role 'Admin' not held.", McpErrorCode.InvalidRequest);

        var (outcome, errorType) = AuditFilter.ClassifyException(ex);

        Assert.Equal(ExecutionOutcome.Denied, outcome);
        Assert.Equal("Forbidden", errorType);
    }

    [Fact]
    public void ClassifyExceptionIdentifiesUnauthorizedAccessExceptionAsDenied()
    {
        var ex = new UnauthorizedAccessException("Forbidden action");

        var (outcome, errorType) = AuditFilter.ClassifyException(ex);

        Assert.Equal(ExecutionOutcome.Denied, outcome);
        Assert.Equal("Forbidden", errorType);
    }

    [Fact]
    public void ClassifyExceptionIdentifiesGeneralExceptionAsError()
    {
        var ex = new InvalidOperationException("Database connection string invalid.");

        var (outcome, errorType) = AuditFilter.ClassifyException(ex);

        Assert.Equal(ExecutionOutcome.Error, outcome);
        Assert.Equal("InvalidOperationException", errorType);
    }

    [Fact]
    public void ClassifyExceptionIdentifiesNonAuthorizationMcpProtocolExceptionAsError()
    {
        var ex = new McpProtocolException("Invalid tool arguments provided.", McpErrorCode.InvalidParams);

        var (outcome, errorType) = AuditFilter.ClassifyException(ex);

        Assert.Equal(ExecutionOutcome.Error, outcome);
        Assert.Equal("McpProtocolException", errorType);
    }

    [Fact]
    public void DetectCacheHitIdentifiesCacheHitFromMetadata()
    {
        var hitResult = new CallToolResult
        {
            Meta = new JsonObject { ["cacheHit"] = true }
        };
        var missResult = new CallToolResult
        {
            Meta = new JsonObject { ["cacheHit"] = false }
        };
        var noMetaResult = new CallToolResult();

        Assert.True(AuditFilter.DetectCacheHit(hitResult));
        Assert.False(AuditFilter.DetectCacheHit(missResult));
        Assert.False(AuditFilter.DetectCacheHit(noMetaResult));
    }

    [Fact]
    public void ProcessInputHonorsAuditModeMetadataOnlyAndMasksSecrets()
    {
        var redactor = new MaskingRedactor();
        var arguments = new Dictionary<string, object?>
        {
            ["password"] = "secret_password",
            ["user"] = "alice"
        };

        var metadataOnlyResult = AuditFilter.ProcessInput(arguments, AuditMode.MetadataOnly, redactor);
        var fullResult = AuditFilter.ProcessInput(arguments, AuditMode.Full, redactor);
        var nullArgsResult = AuditFilter.ProcessInput(null, AuditMode.Full, redactor);

        Assert.Null(metadataOnlyResult);
        Assert.Null(nullArgsResult);
        Assert.NotNull(fullResult);
        Assert.Contains("[REDACTED]", fullResult);
        Assert.DoesNotContain("secret_password", fullResult);
    }

    [Fact]
    public void ProcessOutputHonorsInputsOnlyByOmittingOutputJson()
    {
        var redactor = new PassthroughRedactor();
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "Sensitive Profile Payload with Salary: 150000" }]
        };

        var (outputJson, outputBytes) = AuditFilter.ProcessOutput(result, AuditMode.InputsOnly, redactor);

        Assert.Null(outputJson);
        Assert.True(outputBytes > 0);
    }

    [Fact]
    public void ProcessOutputHonorsMetadataOnlyByOmittingAllOutputs()
    {
        var redactor = new PassthroughRedactor();
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "Profile text" }]
        };

        var (outputJson, outputBytes) = AuditFilter.ProcessOutput(result, AuditMode.MetadataOnly, redactor);

        Assert.Null(outputJson);
        Assert.Equal(0, outputBytes);
    }

    [Fact]
    public void ProcessOutputFullModeRedactsAndCalculatesBytes()
    {
        var redactor = new MaskingRedactor();
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "Password was secret_password" }]
        };

        var (outputJson, outputBytes) = AuditFilter.ProcessOutput(result, AuditMode.Full, redactor);

        Assert.NotNull(outputJson);
        Assert.Contains("[REDACTED]", outputJson);
        Assert.DoesNotContain("secret_password", outputJson);
        Assert.True(outputBytes > 0);
    }
}

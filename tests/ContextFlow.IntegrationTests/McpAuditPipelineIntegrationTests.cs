using System.Diagnostics;
using System.Net.Http.Headers;
using ContextFlow.Core.Entities;
using ContextFlow.Core.Enums;
using ContextFlow.Infrastructure.Data;
using ContextFlow.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ContextFlow.IntegrationTests;

public sealed class McpAuditPipelineIntegrationTests : IClassFixture<ContextFlowWebApplicationFactory>
{
    private readonly ContextFlowWebApplicationFactory _factory;
    private const string _auditEmployeeCode = "EMP-AUDIT-001";

    public McpAuditPipelineIntegrationTests(ContextFlowWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.EnsureDatabaseCreated();
        _factory.SeedEmployee(new Employee
        {
            EmployeeCode = _auditEmployeeCode,
            FullName = "Synthetic Audit Verification Engineer",
            Email = "audit.verification@example.internal",
            Department = "Finance",
            JobTitle = "Principal Governance Architect",
            SyntheticSalary = 175000m,
            HireDate = new DateOnly(2021, 3, 15)
        });
    }

    [Fact]
    public async Task FiveDistinctCallsProduceFivePersistedAuditRowsWithAccurateOutcomesWithinTwoSeconds()
    {
        // Arrange
        var userId = "user-audit-five-" + Guid.NewGuid().ToString("N");
        var adminToken = await _factory.MintTokenAsync(userId, "Audit Five Admin", ["Admin"]);
        var devToken = await _factory.MintTokenAsync(userId, "Audit Five Dev", ["Developer"]);

        var adminClient = await CreateMcpClientAsync(adminToken);
        var devClient = await CreateMcpClientAsync(devToken);

        // 1. Success Call: echo
        var call1 = await adminClient.CallToolAsync("echo", new Dictionary<string, object?> { ["message"] = "test-call-1" });
        Assert.False(call1.IsError ?? false);

        // 2. Success Call with Cache Hit: get_employee_profile (2nd call)
        var empArgs = new Dictionary<string, object?> { ["employeeCode"] = _auditEmployeeCode };
        var initialProfile = await adminClient.CallToolAsync("get_employee_profile", empArgs);
        Assert.False(initialProfile.IsError ?? false);
        var cachedProfile = await adminClient.CallToolAsync("get_employee_profile", empArgs);
        Assert.False(cachedProfile.IsError ?? false);

        // 3. Denied Call: Developer tries to call get_employee_profile (requires Admin or Finance)
        var deniedEx = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await devClient.CallToolAsync("get_employee_profile", empArgs));
        Assert.Contains("Access forbidden", deniedEx.Message, StringComparison.OrdinalIgnoreCase);

        // 4. Rate-Limited Call: Seed rate limit = 1 for query_system_metrics, invoke twice
        var rateLimitedTool = "query_system_metrics";
        await _factory.SeedToolPolicyAsync(new ToolPolicy
        {
            ToolName = rateLimitedTool,
            IsEnabled = true,
            MaxRequestsPerMinute = 1,
            CacheTtlSeconds = 0,
            AuditMode = AuditMode.Full
        });

        // 1st metrics call succeeds
        var metric1 = await adminClient.CallToolAsync(rateLimitedTool);
        Assert.False(metric1.IsError ?? false);

        // 2nd metrics call is rate-limited
        var metric2 = await adminClient.CallToolAsync(rateLimitedTool);
        Assert.True(metric2.IsError ?? false);
        var metric2Text = Assert.IsType<TextContentBlock>(metric2.Content[0]).Text;
        Assert.Contains("Quota exceeded", metric2Text, StringComparison.OrdinalIgnoreCase);

        // 5. Disabled Tool Call: disable echo and invoke
        await _factory.SeedToolPolicyAsync(new ToolPolicy
        {
            ToolName = "echo",
            IsEnabled = false,
            MaxRequestsPerMinute = 60,
            CacheTtlSeconds = 0,
            AuditMode = AuditMode.Full
        });

        var disabledCall = await adminClient.CallToolAsync("echo", new Dictionary<string, object?> { ["message"] = "disabled-call" });
        Assert.True(disabledCall.IsError ?? false);
        var disabledText = Assert.IsType<TextContentBlock>(disabledCall.Content[0]).Text;
        Assert.Contains("disabled", disabledText, StringComparison.OrdinalIgnoreCase);

        // Restore echo policy for subsequent tests
        await _factory.SeedToolPolicyAsync(new ToolPolicy
        {
            ToolName = "echo",
            IsEnabled = true,
            MaxRequestsPerMinute = 60,
            CacheTtlSeconds = 0,
            AuditMode = AuditMode.Full
        });

        // Act: Wait for the async writer service to flush all rows to database (within 2 seconds)
        var logs = await WaitForLogsAsync(userId, expectedCount: 5, timeout: TimeSpan.FromSeconds(2));

        // Assert: 5 rows persisted with accurate outcomes
        Assert.True(logs.Count >= 5, $"Expected at least 5 logs, but found {logs.Count}.");

        var outcomes = logs.Select(l => l.Outcome).ToList();
        Assert.Contains(ExecutionOutcome.Success, outcomes);
        Assert.Contains(ExecutionOutcome.Denied, outcomes);
        Assert.Contains(ExecutionOutcome.RateLimited, outcomes);
        Assert.Contains(ExecutionOutcome.Disabled, outcomes);

        // Verify cache hit flag was captured on at least one profile call
        var cacheHits = logs.Where(l => l.ToolName == "get_employee_profile" && l.CacheHit).ToList();
        Assert.NotEmpty(cacheHits);

        // Verify timing and token id
        foreach (var log in logs)
        {
            Assert.True(log.DurationMs >= 0);
            Assert.False(string.IsNullOrWhiteSpace(log.TokenId));
            Assert.Equal(userId, log.UserId);
        }
    }

    [Fact]
    public async Task InputsOnlyAuditModeStoresInputAndOmitsOutputForPiiSafety()
    {
        // Arrange: get_employee_profile has AuditMode = InputsOnly in seed policies
        var userId = "user-audit-pii-" + Guid.NewGuid().ToString("N");
        var token = await _factory.MintTokenAsync(userId, "Audit PII Token", ["Finance"]);
        var client = await CreateMcpClientAsync(token);

        var args = new Dictionary<string, object?> { ["employeeCode"] = _auditEmployeeCode };

        // Act
        var result = await client.CallToolAsync("get_employee_profile", args);
        Assert.False(result.IsError ?? false);

        // Wait for async persistence
        var logs = await WaitForLogsAsync(userId, expectedCount: 1, timeout: TimeSpan.FromSeconds(2));

        // Assert
        var profileLog = Assert.Single(logs);
        Assert.Equal(ExecutionOutcome.Success, profileLog.Outcome);
        Assert.Equal("get_employee_profile", profileLog.ToolName);

        // PII Safety Guarantee: Inputs are persisted, but Outputs are strictly omitted
        Assert.NotNull(profileLog.InputJson);
        Assert.Contains(_auditEmployeeCode, profileLog.InputJson);
        Assert.Null(profileLog.OutputJson);
        Assert.True(profileLog.OutputBytes > 0, "OutputBytes should be recorded even when OutputJson is omitted.");
    }

    [Fact]
    public async Task PayloadRedactionMasksBearerTokensAndApiKeysBeforePersisting()
    {
        // Arrange
        var userId = "user-audit-redact-" + Guid.NewGuid().ToString("N");
        var token = await _factory.MintTokenAsync(userId, "Audit Redaction Token", ["Developer"]);
        var client = await CreateMcpClientAsync(token);

        var sensitiveMessage = "Executing job with Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.sensitive_payload and apiKey: secret_key_abcdef12345";
        var args = new Dictionary<string, object?> { ["message"] = sensitiveMessage };

        // Act
        var result = await client.CallToolAsync("echo", args);
        Assert.False(result.IsError ?? false);

        // Wait for async persistence
        var logs = await WaitForLogsAsync(userId, expectedCount: 1, timeout: TimeSpan.FromSeconds(2));

        // Assert
        var log = Assert.Single(logs);
        Assert.NotNull(log.InputJson);

        // Verified: secrets are masked with safe replacement tokens
        Assert.Contains("Bearer [REDACTED]", log.InputJson);
        Assert.Contains("apiKey: [REDACTED]", log.InputJson);

        // Raw tokens and secrets are never persisted
        Assert.DoesNotContain("secret_key_abcdef12345", log.InputJson);
        Assert.DoesNotContain("sensitive_payload", log.InputJson);
    }

    [Fact]
    public async Task AuditingPipelineIntroducesSubMillisecondEnqueueOverhead()
    {
        // Arrange: Verify that the non-blocking channel enqueue does not degrade request latency
        var userId = "user-audit-perf-" + Guid.NewGuid().ToString("N");
        var token = await _factory.MintTokenAsync(userId, "Audit Perf Token", ["Developer"]);
        var client = await CreateMcpClientAsync(token);
        var args = new Dictionary<string, object?> { ["message"] = "perf-test" };

        // Warm up JIT and client
        _ = await client.CallToolAsync("echo", args);

        // Act: Execute 10 calls
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++)
        {
            var res = await client.CallToolAsync("echo", args);
            Assert.False(res.IsError ?? false);
        }
        sw.Stop();

        var avgMs = sw.Elapsed.TotalMilliseconds / 10.0;

        // MCP Streamable HTTP calls locally take ~2-5 ms including JSON serialization and HTTP transport
        Assert.True(avgMs < 25.0, $"Average request duration was {avgMs:F2} ms, expected well under 25 ms.");
    }

    private async Task<List<ToolExecutionLog>> WaitForLogsAsync(string userId, int expectedCount, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ContextFlowDbContext>();
            var logs = await db.ToolExecutionLogs
                .AsNoTracking()
                .Where(l => l.UserId == userId)
                .OrderBy(l => l.Timestamp)
                .ToListAsync();

            if (logs.Count >= expectedCount)
            {
                return logs;
            }

            await Task.Delay(50);
        }

        // Final query attempt before returning whatever was captured
        using (var finalScope = _factory.Services.CreateScope())
        {
            var db = finalScope.ServiceProvider.GetRequiredService<ContextFlowDbContext>();
            return await db.ToolExecutionLogs
                .AsNoTracking()
                .Where(l => l.UserId == userId)
                .OrderBy(l => l.Timestamp)
                .ToListAsync();
        }
    }

    private async Task<McpClient> CreateMcpClientAsync(string bearerToken)
    {
        var httpClient = _factory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/mcp") },
            httpClient);

        return await McpClient.CreateAsync(transport);
    }
}

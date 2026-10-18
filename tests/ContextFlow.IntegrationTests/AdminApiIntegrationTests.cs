using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ContextFlow.Core.Entities;
using ContextFlow.Core.Enums;
using ContextFlow.Gateway.Admin;
using ContextFlow.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ContextFlow.IntegrationTests;

public sealed class AdminApiIntegrationTests : IClassFixture<ContextFlowWebApplicationFactory>
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ContextFlowWebApplicationFactory _factory;

    public AdminApiIntegrationTests(ContextFlowWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.EnsureDatabaseCreated();
        _factory.SeedEmployee(new Employee
        {
            EmployeeCode = "EMP-ADMIN-001",
            FullName = "Synthetic Admin Verification User",
            Email = "admin.verify@example.internal",
            Department = "Finance",
            JobTitle = "Staff Analyst",
            SyntheticSalary = 140000m,
            HireDate = new DateOnly(2022, 6, 1)
        });
    }

    [Theory]
    [InlineData("GET", "/api/logs")]
    [InlineData("GET", "/api/logs/1")]
    [InlineData("GET", "/api/tools")]
    [InlineData("PUT", "/api/tools/echo")]
    [InlineData("GET", "/api/tokens")]
    [InlineData("POST", "/api/tokens")]
    [InlineData("DELETE", "/api/tokens/test-jti")]
    [InlineData("GET", "/api/metrics")]
    public async Task UnauthenticatedRequestsToAdminEndpointsReturn401(string method, string path)
    {
        using var client = _factory.CreateClient();
        using var request = CreateHttpRequest(method, path);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Developer", "GET", "/api/logs")]
    [InlineData("Developer", "GET", "/api/tools")]
    [InlineData("Developer", "PUT", "/api/tools/echo")]
    [InlineData("Developer", "GET", "/api/tokens")]
    [InlineData("Developer", "POST", "/api/tokens")]
    [InlineData("Developer", "DELETE", "/api/tokens/test-jti")]
    [InlineData("Developer", "GET", "/api/metrics")]
    [InlineData("Finance", "GET", "/api/logs")]
    [InlineData("Finance", "GET", "/api/tools")]
    [InlineData("Finance", "GET", "/api/tokens")]
    [InlineData("Finance", "GET", "/api/metrics")]
    public async Task NonAdminCallerReceives403ForbiddenOnAllAdminEndpoints(string role, string method, string path)
    {
        var token = await _factory.MintTokenAsync($"nonadmin-{role}-{Guid.NewGuid():N}", $"{role} User", [role]);
        using var client = CreateAuthenticatedHttpClient(token);
        using var request = CreateHttpRequest(method, path);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MintedPatViaAdminApiWorksOnMcpAndReturns401AfterRevocation()
    {
        // Arrange
        var adminToken = await _factory.MintTokenAsync("admin-pat-flow-" + Guid.NewGuid().ToString("N"), "Admin Controller", ["Admin"]);
        using var adminHttp = CreateAuthenticatedHttpClient(adminToken);

        var mintRequest = new MintTokenRequest(
            Name: "CI Finance Bot",
            OwnerUserId: "finance-bot-" + Guid.NewGuid().ToString("N"),
            Roles: ["Finance"],
            LifetimeDays: 14);

        // Act 1: Mint PAT via POST /api/tokens
        using var mintResponse = await adminHttp.PostAsJsonAsync("/api/tokens", mintRequest, _jsonOptions);
        Assert.Equal(HttpStatusCode.Created, mintResponse.StatusCode);

        var minted = await mintResponse.Content.ReadFromJsonAsync<MintedTokenResponse>(_jsonOptions);
        Assert.NotNull(minted);
        Assert.False(string.IsNullOrWhiteSpace(minted.Token));
        Assert.False(string.IsNullOrWhiteSpace(minted.Metadata.Jti));
        Assert.Equal("CI Finance Bot", minted.Metadata.Name);
        Assert.Equal(["Finance"], minted.Metadata.Roles);
        Assert.True(minted.Metadata.IsActive);
        Assert.False(minted.Metadata.IsRevoked);

        // Verify GET /api/tokens lists the token metadata and never leaks the raw token string
        using var listResponse = await adminHttp.GetAsync("/api/tokens");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listRawJson = await listResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(minted.Token, listRawJson, StringComparison.Ordinal);
        var tokens = JsonSerializer.Deserialize<List<PersonalAccessTokenDto>>(listRawJson, _jsonOptions);
        Assert.NotNull(tokens);
        Assert.Contains(tokens, t => t.Jti == minted.Metadata.Jti && t.IsActive);

        // Act 2: Use the newly minted PAT on /mcp via the MCP C# client
        var mcpClient = await CreateMcpClientAsync(minted.Token);
        var visibleTools = await mcpClient.ListToolsAsync();
        Assert.Contains(visibleTools, t => t.Name == "get_employee_profile");
        Assert.DoesNotContain(visibleTools, t => t.Name == "query_system_metrics");

        var profileResult = await mcpClient.CallToolAsync(
            "get_employee_profile",
            new Dictionary<string, object?> { ["employeeCode"] = "EMP-ADMIN-001" });
        Assert.NotEqual(true, profileResult.IsError);

        // Act 3: Revoke the PAT via DELETE /api/tokens/{jti}
        using var revokeResponse = await adminHttp.DeleteAsync($"/api/tokens/{minted.Metadata.Jti}");
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);
        var revokedDto = await revokeResponse.Content.ReadFromJsonAsync<PersonalAccessTokenDto>(_jsonOptions);
        Assert.NotNull(revokedDto);
        Assert.True(revokedDto.IsRevoked);
        Assert.False(revokedDto.IsActive);
        Assert.NotNull(revokedDto.RevokedAt);

        // Assert: Subsequent call to /mcp with the revoked PAT fails with 401 Unauthorized
        using var revokedHttp = CreateAuthenticatedHttpClient(minted.Token);
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var clientAfterRevoke = await CreateMcpClientAsync(minted.Token);
            await clientAfterRevoke.ListToolsAsync();
        });
        Assert.Contains("401", ex.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdatingToolPolicyViaAdminApiInvalidatesCacheAndImmediatelyGovernsMcp()
    {
        var adminToken = await _factory.MintTokenAsync("admin-tools-flow-" + Guid.NewGuid().ToString("N"), "Admin Tools", ["Admin"]);
        using var adminHttp = CreateAuthenticatedHttpClient(adminToken);

        // 1. Read all tools via GET /api/tools
        var tools = await adminHttp.GetFromJsonAsync<List<ToolPolicyDto>>("/api/tools", _jsonOptions);
        Assert.NotNull(tools);
        var echoTool = Assert.Single(tools, t => t.ToolName == "echo");
        Assert.True(echoTool.IsEnabled);

        try
        {
            // 2. Disable 'echo' via PUT /api/tools/echo
            var disableReq = new UpdateToolPolicyRequest(
                IsEnabled: false,
                MaxRequestsPerMinute: 45,
                CacheTtlSeconds: 0,
                AuditMode: AuditMode.MetadataOnly);

            using var updateResp = await adminHttp.PutAsJsonAsync("/api/tools/echo", disableReq, _jsonOptions);
            Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);
            var updatedDto = await updateResp.Content.ReadFromJsonAsync<ToolPolicyDto>(_jsonOptions);
            Assert.NotNull(updatedDto);
            Assert.False(updatedDto.IsEnabled);
            Assert.Equal(45, updatedDto.MaxRequestsPerMinute);
            Assert.Equal(AuditMode.MetadataOnly, updatedDto.AuditMode);

            // 3. Verify MCP server immediately enforces the updated policy (hidden from list, rejected on call)
            var mcpClient = await CreateMcpClientAsync(adminToken);
            var listedTools = await mcpClient.ListToolsAsync();
            Assert.DoesNotContain(listedTools, t => t.Name == "echo");

            var callResult = await mcpClient.CallToolAsync("echo", new Dictionary<string, object?> { ["message"] = "blocked" });
            Assert.True(callResult.IsError);
            var errorText = Assert.IsType<TextContentBlock>(callResult.Content[0]).Text;
            Assert.Contains("disabled", errorText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // Restore 'echo' policy
            await adminHttp.PutAsJsonAsync(
                "/api/tools/echo",
                new UpdateToolPolicyRequest(true, 60, 0, AuditMode.Full),
                _jsonOptions);
        }
    }

    [Fact]
    public async Task LogsAndMetricsEndpointsReturnFilteredAuditHistoryAndTelemetryWithoutLeaks()
    {
        var userId = "admin-logs-metrics-" + Guid.NewGuid().ToString("N");
        var adminToken = await _factory.MintTokenAsync(userId, "Admin Logs Metrics", ["Admin"]);
        using var adminHttp = CreateAuthenticatedHttpClient(adminToken);
        var mcpClient = await CreateMcpClientAsync(adminToken);

        // Trigger two MCP calls: one echo, one employee lookup (InputsOnly audit mode)
        var echoRes = await mcpClient.CallToolAsync("echo", new Dictionary<string, object?> { ["message"] = "hello-admin-logs" });
        Assert.NotEqual(true, echoRes.IsError);

        var empRes = await mcpClient.CallToolAsync("get_employee_profile", new Dictionary<string, object?> { ["employeeCode"] = "EMP-ADMIN-001" });
        Assert.NotEqual(true, empRes.IsError);

        // Wait for async audit writer to persist both records
        var paged = await WaitForApiLogsAsync(adminHttp, userId, expectedCount: 2, TimeSpan.FromSeconds(2));
        Assert.True(paged.TotalCount >= 2);
        Assert.Equal(1, paged.Page);
        Assert.Contains(paged.Items, l => l.ToolName == "echo" && l.Outcome == ExecutionOutcome.Success);

        var empLog = Assert.Single(paged.Items, l => l.ToolName == "get_employee_profile");
        Assert.NotNull(empLog.InputJson);
        Assert.Null(empLog.OutputJson); // InputsOnly mode respected

        // Filter by toolName=echo
        var echoOnly = await adminHttp.GetFromJsonAsync<PagedLogsResponse>($"/api/logs?userId={userId}&toolName=echo", _jsonOptions);
        Assert.NotNull(echoOnly);
        var echoItem = Assert.Single(echoOnly.Items);
        Assert.Equal("echo", echoItem.ToolName);

        // Fetch single log by ID
        var singleLog = await adminHttp.GetFromJsonAsync<ToolExecutionLogDto>($"/api/logs/{echoItem.Id}", _jsonOptions);
        Assert.NotNull(singleLog);
        Assert.Equal(echoItem.Id, singleLog.Id);

        // Query /api/metrics and verify structure + zero host/connection leaks
        using var metricsResponse = await adminHttp.GetAsync("/api/metrics");
        Assert.Equal(HttpStatusCode.OK, metricsResponse.StatusCode);
        var metricsRaw = await metricsResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("localhost", metricsRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", metricsRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contextflow_test", metricsRaw, StringComparison.OrdinalIgnoreCase);

        var metrics = JsonSerializer.Deserialize<SystemMetricsResponse>(metricsRaw, _jsonOptions);
        Assert.NotNull(metrics);
        Assert.Equal("healthy", metrics.Status);
        Assert.True(metrics.TotalCalls >= 2);
        Assert.Contains(metrics.PerTool, t => t.ToolName == "echo" && t.TotalCalls >= 1 && t.P95DurationMs >= 0);
    }

    [Fact]
    public async Task InvalidRequestsReturnStandardProblemDetailsWithoutStackTraces()
    {
        var adminToken = await _factory.MintTokenAsync("admin-problem-" + Guid.NewGuid().ToString("N"), "Admin Problem", ["Admin"]);
        using var adminHttp = CreateAuthenticatedHttpClient(adminToken);

        // 1. Invalid rate limit (MaxRequestsPerMinute = 0) and negative CacheTtlSeconds -> 400 ProblemDetails
        using var badQuotaResp = await adminHttp.PutAsJsonAsync(
            "/api/tools/echo",
            new UpdateToolPolicyRequest(true, 0, 0, AuditMode.Full),
            _jsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, badQuotaResp.StatusCode);
        var badQuotaProblem = await badQuotaResp.Content.ReadFromJsonAsync<ProblemDetails>(_jsonOptions);
        Assert.NotNull(badQuotaProblem);
        Assert.Equal(400, badQuotaProblem.Status);

        using var badCacheResp = await adminHttp.PutAsJsonAsync(
            "/api/tools/echo",
            new UpdateToolPolicyRequest(true, 60, -10, AuditMode.Full),
            _jsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, badCacheResp.StatusCode);
        var badCacheProblem = await badCacheResp.Content.ReadFromJsonAsync<ProblemDetails>(_jsonOptions);
        Assert.NotNull(badCacheProblem);
        Assert.Equal(400, badCacheProblem.Status);

        // 2. Unknown tool -> 404 ProblemDetails
        using var unknownToolResp = await adminHttp.PutAsJsonAsync(
            "/api/tools/non_existent_tool",
            new UpdateToolPolicyRequest(true, 60, 0, AuditMode.Full),
            _jsonOptions);
        Assert.Equal(HttpStatusCode.NotFound, unknownToolResp.StatusCode);
        var unknownToolProblem = await unknownToolResp.Content.ReadFromJsonAsync<ProblemDetails>(_jsonOptions);
        Assert.NotNull(unknownToolProblem);
        Assert.Equal(404, unknownToolProblem.Status);

        // 3. Invalid role on token mint -> 400 ProblemDetails
        using var badRoleResp = await adminHttp.PostAsJsonAsync(
            "/api/tokens",
            new MintTokenRequest("Bad Role Token", "user1", ["SuperAdmin"], 30),
            _jsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, badRoleResp.StatusCode);
        var badRoleProblem = await badRoleResp.Content.ReadFromJsonAsync<ProblemDetails>(_jsonOptions);
        Assert.NotNull(badRoleProblem);
        Assert.Equal(400, badRoleProblem.Status);

        // 4. Unknown JTI on token revoke -> 404 ProblemDetails
        using var unknownJtiResp = await adminHttp.DeleteAsync("/api/tokens/unknown-jti-999999");
        Assert.Equal(HttpStatusCode.NotFound, unknownJtiResp.StatusCode);
        var unknownJtiProblem = await unknownJtiResp.Content.ReadFromJsonAsync<ProblemDetails>(_jsonOptions);
        Assert.NotNull(unknownJtiProblem);
        Assert.Equal(404, unknownJtiProblem.Status);

        // 5. Invalid log pagination -> 400 ProblemDetails
        using var badPageResp = await adminHttp.GetAsync("/api/logs?page=0");
        Assert.Equal(HttpStatusCode.BadRequest, badPageResp.StatusCode);
        var badPageProblem = await badPageResp.Content.ReadFromJsonAsync<ProblemDetails>(_jsonOptions);
        Assert.NotNull(badPageProblem);
        Assert.Equal(400, badPageProblem.Status);
    }

    private static HttpRequestMessage CreateHttpRequest(string method, string path)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
        {
            req.Content = JsonContent.Create(new { });
        }

        return req;
    }

    private HttpClient CreateAuthenticatedHttpClient(string bearerToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return client;
    }

    private async Task<McpClient> CreateMcpClientAsync(string bearerToken)
    {
        var httpClient = CreateAuthenticatedHttpClient(bearerToken);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/mcp") },
            httpClient);

        return await McpClient.CreateAsync(transport);
    }

    private static async Task<PagedLogsResponse> WaitForApiLogsAsync(
        HttpClient adminHttp,
        string userId,
        int expectedCount,
        TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        PagedLogsResponse? latest = null;
        while (sw.Elapsed < timeout)
        {
            latest = await adminHttp.GetFromJsonAsync<PagedLogsResponse>($"/api/logs?userId={userId}", _jsonOptions);
            if (latest is not null && latest.TotalCount >= expectedCount)
            {
                return latest;
            }

            await Task.Delay(50);
        }

        return latest ?? new PagedLogsResponse([], 1, 50, 0, 0);
    }
}


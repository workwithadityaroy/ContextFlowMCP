using System.Net.Http.Headers;
using ContextFlow.Core.Entities;
using ContextFlow.Core.Enums;
using ContextFlow.IntegrationTests.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ContextFlow.IntegrationTests;

public sealed class McpPolicyAndRateLimitIntegrationTests(ContextFlowWebApplicationFactory factory)
    : IClassFixture<ContextFlowWebApplicationFactory>
{
    private readonly ContextFlowWebApplicationFactory _factory = factory;

    [Fact]
    public async Task DisabledToolIsHiddenFromToolsList()
    {
        // Arrange
        var token = await _factory.MintTokenAsync("admin-policy-list", "Admin Policy Test", ["Admin"]);
        var client = await CreateMcpClientAsync(token);

        try
        {
            // Pre-check: query_system_metrics should be listed
            var initialTools = await client.ListToolsAsync();
            Assert.Contains(initialTools, t => string.Equals(t.Name, "query_system_metrics", StringComparison.OrdinalIgnoreCase));

            // Disable query_system_metrics
            await _factory.SeedToolPolicyAsync(new ToolPolicy
            {
                ToolName = "query_system_metrics",
                IsEnabled = false,
                MaxRequestsPerMinute = 60,
                CacheTtlSeconds = 0,
                AuditMode = AuditMode.Full
            });

            // Act
            var toolsAfterDisable = await client.ListToolsAsync();

            // Assert
            Assert.DoesNotContain(toolsAfterDisable, t => string.Equals(t.Name, "query_system_metrics", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await _factory.SeedToolPolicyAsync(new ToolPolicy
            {
                ToolName = "query_system_metrics",
                IsEnabled = true,
                MaxRequestsPerMinute = 60,
                CacheTtlSeconds = 0,
                AuditMode = AuditMode.Full
            });
        }
    }

    [Fact]
    public async Task CallingDisabledToolReturnsActionableErrorResult()
    {
        // Arrange
        var token = await _factory.MintTokenAsync("admin-policy-call", "Admin Policy Call Test", ["Admin"]);
        var client = await CreateMcpClientAsync(token);

        try
        {
            // Disable get_employee_profile
            await _factory.SeedToolPolicyAsync(new ToolPolicy
            {
                ToolName = "get_employee_profile",
                IsEnabled = false,
                MaxRequestsPerMinute = 60,
                CacheTtlSeconds = 0,
                AuditMode = AuditMode.Full
            });

            // Act
            var result = await client.CallToolAsync(
                "get_employee_profile",
                new Dictionary<string, object?> { ["employeeCode"] = "EMP001" });

            // Assert
            Assert.True(result.IsError);
            Assert.NotNull(result.Content);
            var textContent = Assert.IsType<TextContentBlock>(result.Content[0]);
            Assert.Contains("disabled by administrator policy", textContent.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await _factory.SeedToolPolicyAsync(new ToolPolicy
            {
                ToolName = "get_employee_profile",
                IsEnabled = true,
                MaxRequestsPerMinute = 60,
                CacheTtlSeconds = 0,
                AuditMode = AuditMode.Full
            });
        }
    }

    [Fact]
    public async Task RateLimitEnforcesQuotaWithinSlidingWindowAndIsolatesUsers()
    {
        // Arrange - set quota of 10 requests per minute on fetch_external_exchange_rates
        await _factory.SeedToolPolicyAsync(new ToolPolicy
        {
            ToolName = "fetch_external_exchange_rates",
            IsEnabled = true,
            MaxRequestsPerMinute = 10,
            CacheTtlSeconds = 0,
            AuditMode = AuditMode.Full
        });

        var userAId = "rate-limit-user-a-" + Guid.NewGuid().ToString("N");
        var userBId = "rate-limit-user-b-" + Guid.NewGuid().ToString("N");

        var tokenA = await _factory.MintTokenAsync(userAId, "User A Token", ["Finance"]);
        var tokenB = await _factory.MintTokenAsync(userBId, "User B Token", ["Finance"]);

        var clientA = await CreateMcpClientAsync(tokenA);
        var clientB = await CreateMcpClientAsync(tokenB);

        var args = new Dictionary<string, object?> { ["baseCurrency"] = "USD" };

        // Act - User A calls 10 times (calls 1 to 10 should succeed)
        for (var i = 1; i <= 10; i++)
        {
            var callResult = await clientA.CallToolAsync("fetch_external_exchange_rates", args);
            Assert.False(callResult.IsError ?? false, $"Call {i} for User A should succeed within quota.");
        }

        // Call 11 for User A must be throttled
        var throttledResult = await clientA.CallToolAsync("fetch_external_exchange_rates", args);
        Assert.True(throttledResult.IsError, "Call 11 for User A must be throttled.");
        var errorBlock = Assert.IsType<TextContentBlock>(throttledResult.Content[0]);
        Assert.Contains("Quota exceeded", errorBlock.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Retry after", errorBlock.Text, StringComparison.OrdinalIgnoreCase);

        // User B must be unaffected by User A's quota exhaustion
        var userBResult = await clientB.CallToolAsync("fetch_external_exchange_rates", args);
        Assert.False(userBResult.IsError ?? false, "User B call must succeed despite User A being throttled.");
    }

    [Fact]
    public async Task ReEnablingToolRestoresDiscoveryAndExecution()
    {
        // Arrange
        var token = await _factory.MintTokenAsync("admin-policy-restore", "Admin Policy Restore Test", ["Admin"]);
        var client = await CreateMcpClientAsync(token);

        // Disable
        await _factory.SeedToolPolicyAsync(new ToolPolicy
        {
            ToolName = "echo",
            IsEnabled = false,
            MaxRequestsPerMinute = 60
        });

        var disabledCall = await client.CallToolAsync("echo", new Dictionary<string, object?> { ["message"] = "test" });
        Assert.True(disabledCall.IsError);

        // Re-enable
        await _factory.SeedToolPolicyAsync(new ToolPolicy
        {
            ToolName = "echo",
            IsEnabled = true,
            MaxRequestsPerMinute = 60
        });

        // Act
        var tools = await client.ListToolsAsync();
        var reEnabledCall = await client.CallToolAsync("echo", new Dictionary<string, object?> { ["message"] = "test" });

        // Assert
        Assert.Contains(tools, t => string.Equals(t.Name, "Echo", StringComparison.OrdinalIgnoreCase));
        Assert.False(reEnabledCall.IsError ?? false);
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

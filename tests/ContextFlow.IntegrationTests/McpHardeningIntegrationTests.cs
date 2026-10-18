using System.Diagnostics;
using System.Net.Http.Headers;
using ContextFlow.Core.Entities;
using ContextFlow.Core.Enums;
using ContextFlow.Infrastructure.Data;
using ContextFlow.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ContextFlow.IntegrationTests;

public sealed class McpHardeningIntegrationTests : IClassFixture<ContextFlowWebApplicationFactory>
{
    private const string ToolName = "fetch_external_exchange_rates";
    private static readonly string[] _leakMarkers =
    [
        "Password=", "hunter2", "internal-db", "Exception", "   at ", "StackTrace", "10.20.30.40", ".cs:line"
    ];

    private readonly ContextFlowWebApplicationFactory _factory;

    public McpHardeningIntegrationTests(ContextFlowWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.EnsureDatabaseCreated();
    }

    [Fact]
    public async Task ExchangeRateToolReturnsUpstreamRatesThroughConfiguredUrlOnly()
    {
        // Arrange
        _factory.ExchangeRateHandler.Reset();
        await SeedExchangePolicyAsync();
        var client = await CreateMcpClientAsync(await _factory.MintTokenAsync("hardening-ok-" + Guid.NewGuid().ToString("N"), "Hardening OK", ["Developer"]));

        // Act
        var result = await client.CallToolAsync(ToolName, new Dictionary<string, object?> { ["baseCurrency"] = "usd" });

        // Assert
        Assert.NotEqual(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("\"EUR\":0.92", text, StringComparison.Ordinal);
        Assert.Contains("\"baseCurrency\":\"USD\"", text, StringComparison.Ordinal);
        Assert.Equal("api.frankfurter.dev", _factory.ExchangeRateHandler.LastRequestUri?.Host);
        Assert.Equal("?base=USD", _factory.ExchangeRateHandler.LastRequestUri?.Query);
    }

    [Fact]
    public async Task InjectionAttemptInCurrencyIsRejectedBeforeAnyOutboundCall()
    {
        // Arrange
        _factory.ExchangeRateHandler.Reset();
        await SeedExchangePolicyAsync();
        var client = await CreateMcpClientAsync(await _factory.MintTokenAsync("hardening-ssrf-" + Guid.NewGuid().ToString("N"), "Hardening SSRF", ["Admin"]));
        var before = _factory.ExchangeRateHandler.CallCount;

        // Act
        var result = await client.CallToolAsync(ToolName, new Dictionary<string, object?> { ["baseCurrency"] = "http://169.254.169.254/latest/meta-data" });

        // Assert
        Assert.True(result.IsError);
        Assert.Equal(before, _factory.ExchangeRateHandler.CallCount);
        Assert.DoesNotContain("169.254", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedToolFailureReturnsGenericErrorWithoutLeaksAndIsAuditedAsError()
    {
        // Arrange - an unexpected (non-HTTP) exception escapes the tool with sensitive-looking text.
        var userId = "hardening-throw-" + Guid.NewGuid().ToString("N");
        _factory.ExchangeRateHandler.Reset();
        _factory.ExchangeRateHandler.Responder = _ =>
            throw new InvalidOperationException("Npgsql failure Host=internal-db;Password=hunter2 at 10.20.30.40 in Foo.cs:line 42");
        await SeedExchangePolicyAsync();
        var client = await CreateMcpClientAsync(await _factory.MintTokenAsync(userId, "Hardening Throw", ["Admin"]));

        try
        {
            // Act
            var result = await client.CallToolAsync(ToolName, new Dictionary<string, object?> { ["baseCurrency"] = "EUR" });

            // Assert
            Assert.True(result.IsError);
            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
            Assert.Equal(Gateway.Filters.ExceptionFilter.GenericErrorMessage, text);
            foreach (var marker in _leakMarkers)
            {
                Assert.DoesNotContain(marker, text, StringComparison.OrdinalIgnoreCase);
            }

            var logs = await WaitForLogsAsync(userId, expectedCount: 1, TimeSpan.FromSeconds(2));
            var log = Assert.Single(logs);
            Assert.Equal(ExecutionOutcome.Error, log.Outcome);
            Assert.Null(log.OutputJson);
            Assert.DoesNotContain("hunter2", log.InputJson ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            _factory.ExchangeRateHandler.Reset();
        }
    }

    [Fact]
    public async Task UpstreamConnectionFailureReturnsActionableErrorWithoutNetworkDetails()
    {
        // Arrange
        var userId = "hardening-conn-" + Guid.NewGuid().ToString("N");
        _factory.ExchangeRateHandler.Reset();
        _factory.ExchangeRateHandler.Responder = _ =>
            throw new HttpRequestException("No connection could be made to 10.20.30.40:443 (internal-db)");
        await SeedExchangePolicyAsync();
        var client = await CreateMcpClientAsync(await _factory.MintTokenAsync(userId, "Hardening Conn", ["Finance"]));

        try
        {
            // Act
            var result = await client.CallToolAsync(ToolName, new Dictionary<string, object?> { ["baseCurrency"] = "GBP" });

            // Assert
            Assert.True(result.IsError);
            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
            Assert.Contains("currently unavailable", text, StringComparison.OrdinalIgnoreCase);
            foreach (var marker in _leakMarkers)
            {
                Assert.DoesNotContain(marker, text, StringComparison.OrdinalIgnoreCase);
            }

            var logs = await WaitForLogsAsync(userId, expectedCount: 1, TimeSpan.FromSeconds(2));
            Assert.Equal(ExecutionOutcome.Error, Assert.Single(logs).Outcome);
        }
        finally
        {
            _factory.ExchangeRateHandler.Reset();
        }
    }

    private Task SeedExchangePolicyAsync() => _factory.SeedToolPolicyAsync(new ToolPolicy
    {
        ToolName = ToolName,
        IsEnabled = true,
        MaxRequestsPerMinute = 600,
        CacheTtlSeconds = 0,
        AuditMode = AuditMode.Full
    });

    private async Task<List<ToolExecutionLog>> WaitForLogsAsync(string userId, int expectedCount, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        List<ToolExecutionLog> logs;
        do
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ContextFlowDbContext>();
            logs = await db.ToolExecutionLogs.AsNoTracking().Where(l => l.UserId == userId).ToListAsync();
            if (logs.Count >= expectedCount)
            {
                return logs;
            }

            await Task.Delay(50);
        }
        while (sw.Elapsed < timeout);

        return logs;
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


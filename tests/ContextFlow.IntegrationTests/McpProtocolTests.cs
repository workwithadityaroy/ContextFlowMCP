using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ContextFlow.IntegrationTests;

public sealed class McpProtocolTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public McpProtocolTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CanConnectListToolsAndCallEchoTool()
    {
        var httpClient = _factory.CreateClient();
        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp")
        };

        var transport = new HttpClientTransport(transportOptions, httpClient);
        await using var client = await McpClient.CreateAsync(transport);

        // 1. List tools
        var tools = await client.ListToolsAsync();
        Assert.NotNull(tools);
        Assert.NotEmpty(tools);
        Assert.Contains(tools, t => string.Equals(t.Name, "Echo", StringComparison.OrdinalIgnoreCase));

        // 2. Call echo tool
        var toolToCall = tools.First(t => string.Equals(t.Name, "Echo", StringComparison.OrdinalIgnoreCase));
        var callResult = await client.CallToolAsync(
            toolToCall.Name,
            new Dictionary<string, object?> { ["message"] = "Integration Test" });

        Assert.NotNull(callResult);
        Assert.False(callResult.IsError ?? false);
        Assert.NotNull(callResult.Content);
        Assert.Single(callResult.Content);

        var textContent = Assert.IsType<TextContentBlock>(callResult.Content[0]);
        Assert.Equal("Echo: Integration Test", textContent.Text);
    }
}


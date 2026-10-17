using ContextFlow.Gateway.Tools;
using ModelContextProtocol.Protocol;

namespace ContextFlow.UnitTests;

public sealed class EchoToolTests
{
    [Fact]
    public void EchoWithValidMessageReturnsExpectedContent()
    {
        // Arrange
        const string inputMessage = "Hello MCP!";

        // Act
        var result = EchoTool.Echo(inputMessage);

        // Assert
        Assert.NotNull(result);
        Assert.False(result.IsError ?? false);
        Assert.NotNull(result.Content);
        Assert.Single(result.Content);

        var textContent = Assert.IsType<TextContentBlock>(result.Content[0]);
        Assert.Equal("Echo: Hello MCP!", textContent.Text);
    }
}


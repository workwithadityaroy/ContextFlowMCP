using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ContextFlow.Gateway.Tools;

[McpServerToolType]
public sealed class EchoTool
{
    [McpServerTool(ReadOnly = true)]
    [Description("Echoes the provided message back to the caller.")]
    public static CallToolResult Echo(
        [Description("The message to echo back")] string message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"Echo: {message}" }]
        };
    }
}


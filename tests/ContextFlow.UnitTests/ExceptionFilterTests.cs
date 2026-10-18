using ContextFlow.Core.Enums;
using ContextFlow.Gateway.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Constructing RequestContext/McpServer by hand requires experimental (MCPEXP002) and obsolete (MCP9003, MCP9005)
// SDK members (the stub must override obsolete McpServer.LoggingLevel: CS0672); NullReferenceException is thrown on
// purpose to prove the filter hides runtime-reserved exceptions (CA2201).
#pragma warning disable MCP9003, MCP9005, MCPEXP002, CS0672, CA2201

namespace ContextFlow.UnitTests;

public sealed class ExceptionFilterTests
{
    private const string LeakyMessage =
        "Npgsql.NpgsqlException: Failed to connect to Host=db.internal.example;Port=5432;Database=contextflow;Username=svc;Password=hunter2 "
        + "at ContextFlow.Infrastructure.Data.ToolsReadOnlyDbContext.Query() in C:\\src\\ContextFlow\\Data.cs:line 42";

    [Fact]
    public async Task ThrowingNextReturnsGenericSanitizedErrorResult()
    {
        var logs = new CollectingLoggerProvider();
        var handler = ExceptionFilter.FilterCallTool((_, _) => throw new InvalidOperationException(LeakyMessage));

        var result = await handler(CreateRequest("get_exchange_rate", logs), CancellationToken.None);

        Assert.True(result.IsError);
        var block = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Equal(ExceptionFilter.GenericErrorMessage, block.Text);
        Assert.DoesNotContain("hunter2", block.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", block.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(LeakyMessage, block.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), block.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("at ", block.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", block.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\", block.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThrownExceptionDoesNotLeakIntoAnySerializedPartOfResult()
    {
        var handler = ExceptionFilter.FilterCallTool((_, _) => throw new InvalidOperationException(LeakyMessage));

        var result = await handler(CreateRequest("echo", new CollectingLoggerProvider()), CancellationToken.None);

        var serialized = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("hunter2", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", serialized, StringComparison.Ordinal);
    }

    public static TheoryData<Exception> HandledExceptions => new()
    {
        new InvalidOperationException(LeakyMessage),
        new HttpRequestException(LeakyMessage),
        new NullReferenceException(LeakyMessage),
        new TimeoutException(LeakyMessage),
        new AggregateException(new InvalidOperationException(LeakyMessage)),
    };

    [Theory]
    [MemberData(nameof(HandledExceptions))]
    public async Task VariousExceptionTypesAreConvertedToGenericError(Exception exception)
    {
        var handler = ExceptionFilter.FilterCallTool((_, _) => throw exception);

        var result = await handler(CreateRequest("echo", new CollectingLoggerProvider()), CancellationToken.None);

        Assert.True(result.IsError);
        var block = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Equal(ExceptionFilter.GenericErrorMessage, block.Text);
    }

    [Fact]
    public async Task OperationCanceledExceptionPropagates()
    {
        var handler = ExceptionFilter.FilterCallTool((_, _) => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await handler(CreateRequest("echo", new CollectingLoggerProvider()), CancellationToken.None));
    }

    [Fact]
    public async Task TaskCanceledExceptionPropagates()
    {
        var handler = ExceptionFilter.FilterCallTool((_, _) => throw new TaskCanceledException());

        await Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await handler(CreateRequest("echo", new CollectingLoggerProvider()), CancellationToken.None));
    }

    [Fact]
    public async Task SuccessfulResultIsReturnedUnchanged()
    {
        var expected = new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
        var handler = ExceptionFilter.FilterCallTool((_, _) => ValueTask.FromResult(expected));
        var logs = new CollectingLoggerProvider();

        var result = await handler(CreateRequest("echo", logs), CancellationToken.None);

        Assert.Same(expected, result);
        Assert.NotEqual(true, result.IsError);
        Assert.Empty(logs.Entries);
    }

    [Fact]
    public async Task ExceptionIsLoggedServerSideAtErrorLevelWithToolNameOnly()
    {
        var logs = new CollectingLoggerProvider();
        var exception = new InvalidOperationException(LeakyMessage);
        var handler = ExceptionFilter.FilterCallTool((_, _) => throw exception);

        await handler(CreateRequest("get_exchange_rate", logs), CancellationToken.None);

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(exception, entry.Exception);
        Assert.Equal("ContextFlow.Gateway.Filters.ExceptionFilter", entry.Category);
        Assert.Contains("get_exchange_rate", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingLoggerFactoryStillReturnsGenericError()
    {
        var handler = ExceptionFilter.FilterCallTool((_, _) => throw new InvalidOperationException(LeakyMessage));

        var result = await handler(CreateRequest("echo", services: new ServiceCollection().BuildServiceProvider()), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(ExceptionFilter.GenericErrorMessage, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task NullServicesStillReturnsGenericError()
    {
        var handler = ExceptionFilter.FilterCallTool((_, _) => throw new InvalidOperationException(LeakyMessage));

        var result = await handler(CreateRequest("echo", services: null), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(ExceptionFilter.GenericErrorMessage, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task GenericResultIsClassifiedAsToolErrorByAuditFilter()
    {
        var handler = ExceptionFilter.FilterCallTool((_, _) => throw new InvalidOperationException(LeakyMessage));

        var result = await handler(CreateRequest("echo", new CollectingLoggerProvider()), CancellationToken.None);
        var (outcome, errorType) = AuditFilter.ClassifyResult(result);

        Assert.Equal(ExecutionOutcome.Error, outcome);
        Assert.Equal("ToolError", errorType);
    }

    [Fact]
    public void GenericMessageContainsNoAuditClassificationKeywords()
    {
        foreach (var keyword in new[] { "Quota exceeded", "is disabled", "is currently disabled", "forbidden", "unauthorized" })
        {
            Assert.DoesNotContain(keyword, ExceptionFilter.GenericErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static RequestContext<CallToolRequestParams> CreateRequest(string toolName, CollectingLoggerProvider logs) =>
        CreateRequest(toolName, BuildServices(logs));

    private static RequestContext<CallToolRequestParams> CreateRequest(string toolName, IServiceProvider? services)
    {
        var request = new RequestContext<CallToolRequestParams>(new StubMcpServer(), new JsonRpcRequest { Method = RequestMethods.ToolsCall })
        {
            Params = new CallToolRequestParams { Name = toolName },
            Services = services,
        };

        return request;
    }

    private static ServiceProvider BuildServices(CollectingLoggerProvider logs)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        return services.BuildServiceProvider();
    }

    /// <summary>Minimal inert server; the filter under test never touches it.</summary>
    private sealed class StubMcpServer : McpServer
    {
        public override string? SessionId => null;

        public override string? NegotiatedProtocolVersion => null;

        public override ClientCapabilities? ClientCapabilities => null;

        public override Implementation? ClientInfo => null;

        public override McpServerOptions ServerOptions { get; } = new();

        public override IServiceProvider? Services => null;

        public override LoggingLevel? LoggingLevel => null;

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public override IAsyncDisposable RegisterNotificationHandler(
            string method,
            Func<JsonRpcNotification, CancellationToken, ValueTask> handler) =>
            throw new NotSupportedException();

        public override Task RunAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public override Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public override Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }


    internal sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

    internal sealed class CollectingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CollectingLogger(categoryName, this);

        public void Dispose()
        {
        }

        private void Add(LogEntry entry)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }

        private sealed class CollectingLogger(string category, CollectingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Add(new LogEntry(category, logLevel, formatter(state, exception), exception));
        }
    }
}

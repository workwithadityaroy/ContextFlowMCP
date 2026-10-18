using System.Net;
using System.Text;
using ContextFlow.Gateway.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace ContextFlow.UnitTests;

public sealed class ExchangeRateToolsTests
{
    private const string ValidBody = """{"amount":1.0,"base":"USD","date":"2026-10-16","rates":{"EUR":0.92,"GBP":0.79,"BAD":"x","toolong":1.5}}""";

    [Fact]
    public async Task ValidCurrencyReturnsParsedRatesAndBuildsUrlFromConfigurationOnly()
    {
        // Arrange
        var handler = new StubHandler(_ => Json(ValidBody));
        var factory = new StubFactory(handler);

        // Act
        var result = await Invoke(factory, "usd");

        // Assert
        Assert.NotEqual(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        Assert.Equal("USD", doc.RootElement.GetProperty("baseCurrency").GetString());
        Assert.Equal("2026-10-16", doc.RootElement.GetProperty("date").GetString());
        var rates = doc.RootElement.GetProperty("rates");
        Assert.Equal(0.92m, rates.GetProperty("EUR").GetDecimal());
        Assert.Equal(0.79m, rates.GetProperty("GBP").GetDecimal());
        Assert.False(rates.TryGetProperty("BAD", out _));
        Assert.False(rates.TryGetProperty("toolong", out _));
        Assert.Equal("https://api.example.test/v1/latest?base=USD", handler.LastUri?.ToString());
        Assert.Equal(ExchangeRateOptions.HttpClientName, factory.LastClientName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData("U$D")]
    [InlineData("123")]
    [InlineData("usd/../../")]
    [InlineData("USD&base=EUR")]
    [InlineData("http://169.254.169.254/")]
    public async Task InvalidCurrencyIsRejectedWithoutCallingUpstream(string input)
    {
        // Arrange
        var handler = new StubHandler(_ => Json(ValidBody));

        // Act
        var result = await Invoke(new StubFactory(handler), input);

        // Assert
        Assert.True(result.IsError);
        Assert.Equal(0, handler.CallCount);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.DoesNotContain("169.254", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task UnsupportedCurrencyReturnsActionableError(HttpStatusCode status)
    {
        var result = await Invoke(new StubFactory(new StubHandler(_ => Json("{}", status))), "ZZZ");

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("ZZZ", text, StringComparison.Ordinal);
        Assert.Contains("not supported", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpstreamServerErrorReturnsGenericUnavailableMessage()
    {
        var result = await Invoke(new StubFactory(new StubHandler(_ => Json("internal db at 10.0.0.5 exploded", HttpStatusCode.InternalServerError))), "USD");

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("currently unavailable", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("10.0.0.5", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionFailureReturnsGenericMessageWithoutLeakingDetails()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("Connection refused to api.example.test:443 (10.1.2.3)"));

        var result = await Invoke(new StubFactory(handler), "USD");

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("currently unavailable", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("10.1.2.3", text, StringComparison.Ordinal);
        Assert.DoesNotContain("api.example.test", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpstreamTimeoutReturnsGenericMessage()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("timeout"));

        var result = await Invoke(new StubFactory(handler), "USD");

        Assert.True(result.IsError);
        Assert.Contains("currently unavailable", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"rates":[1,2]}""")]
    [InlineData("""{"amount":1}""")]
    public async Task MalformedUpstreamPayloadReturnsGenericMessage(string body)
    {
        var result = await Invoke(new StubFactory(new StubHandler(_ => Json(body))), "USD");

        Assert.True(result.IsError);
        Assert.Contains("currently unavailable", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new StubHandler(_ => Json(ValidBody), honorCancellation: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Invoke(new StubFactory(handler), "USD", cts.Token));
    }

    [Fact]
    public void OptionsDefaultToHttpsEndpointWithShortTimeout()
    {
        var options = new ExchangeRateOptions();

        Assert.StartsWith("https://", options.BaseUrl, StringComparison.Ordinal);
        Assert.Equal(10, options.TimeoutSeconds);
        Assert.Empty(options.AllowedHosts);
    }

    private static Task<CallToolResult> Invoke(IHttpClientFactory factory, string currency, CancellationToken cancellationToken = default) =>
        ExchangeRateTools.FetchExternalExchangeRates(
            factory,
            Options.Create(new ExchangeRateOptions { BaseUrl = "https://api.example.test/v1/latest" }),
            NullLogger<ExchangeRateTools>.Instance,
            currency,
            cancellationToken);

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder, bool honorCancellation = false) : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (honorCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            Interlocked.Increment(ref _callCount);
            LastUri = request.RequestUri;
            return Task.FromResult(responder(request));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public string? LastClientName { get; private set; }

        public HttpClient CreateClient(string name)
        {
            LastClientName = name;
            return new HttpClient(handler, disposeHandler: false);
        }
    }
}


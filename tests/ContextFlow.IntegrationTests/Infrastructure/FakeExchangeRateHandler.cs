using System.Net;
using System.Text;

namespace ContextFlow.IntegrationTests.Infrastructure;

/// <summary>
/// Offline stand-in for the external exchange-rate provider. Integration tests never touch the public network.
/// </summary>
public sealed class FakeExchangeRateHandler : HttpMessageHandler
{
    public const string DefaultBody = """{"amount":1.0,"base":"USD","date":"2026-10-16","rates":{"EUR":0.92,"GBP":0.79,"JPY":154.5,"CHF":null}}""";

    private int _callCount;

    /// <summary>Overrides the response; defaults to a canned successful payload.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = _ => Json(DefaultBody);

    public int CallCount => Volatile.Read(ref _callCount);

    public Uri? LastRequestUri { get; private set; }

    public void Reset()
    {
        Responder = _ => Json(DefaultBody);
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        LastRequestUri = request.RequestUri;
        return Task.FromResult(Responder(request));
    }
}


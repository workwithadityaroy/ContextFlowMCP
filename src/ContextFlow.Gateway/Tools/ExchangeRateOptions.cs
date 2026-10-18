namespace ContextFlow.Gateway.Tools;

/// <summary>
/// Configuration for the <c>fetch_external_exchange_rates</c> tool. The base URL is operator-controlled
/// and is never influenced by LLM input.
/// </summary>
public sealed class ExchangeRateOptions
{
    public const string SectionName = "ExchangeRate";
    public const string HttpClientName = "ExchangeRates";

    /// <summary>Absolute https URL of the rates endpoint, e.g. <c>https://api.frankfurter.dev/v1/latest</c>.</summary>
    public string BaseUrl { get; set; } = "https://api.frankfurter.dev/v1/latest";

    /// <summary>Hosts the outbound client may connect to (SSRF allowlist). Defaults to the host of <see cref="BaseUrl"/>.</summary>
    public string[] AllowedHosts { get; set; } = [];

    /// <summary>Per-request timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 10;
}


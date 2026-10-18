using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ContextFlow.Gateway.Tools;

[McpServerToolType]
public sealed partial class ExchangeRateTools
{
    private const string UpstreamUnavailableMessage = "The exchange rate service is currently unavailable. Please try again later.";

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyRegex();

    [McpServerTool(ReadOnly = true)]
    [Authorize(Roles = "Admin,Developer,Finance")]
    [Description("Fetches current foreign exchange rates for a standard ISO 4217 3-letter base currency code (e.g. USD, EUR) from the configured external rates provider. Available to Admin, Developer, and Finance roles.")]
    public static async Task<CallToolResult> FetchExternalExchangeRates(
        IHttpClientFactory httpClientFactory,
        IOptions<ExchangeRateOptions> options,
        ILogger<ExchangeRateTools> logger,
        [Description("ISO 4217 3-letter currency code (e.g. USD, EUR)")] string baseCurrency,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(baseCurrency))
        {
            return Error("Base currency code is required.");
        }

        var normalized = baseCurrency.Trim().ToUpperInvariant();
        if (!CurrencyRegex().IsMatch(normalized))
        {
            return Error("Invalid currency code. Must be a 3-letter ISO 4217 code (e.g. USD, EUR).");
        }

        // The URL is built exclusively from operator configuration plus the validated currency code.
        var requestUri = new Uri($"{options.Value.BaseUrl}?base={normalized}", UriKind.Absolute);

        try
        {
            using var client = httpClientFactory.CreateClient(ExchangeRateOptions.HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);

            if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.UnprocessableEntity)
            {
                return Error($"Currency '{normalized}' is not supported by the exchange rate provider.");
            }

            if (!response.IsSuccessStatusCode)
            {
                LogUpstreamStatus(logger, (int)response.StatusCode);
                return Error(UpstreamUnavailableMessage);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return Success(normalized, body);
        }
        catch (HttpRequestException ex)
        {
            LogUpstreamFailure(logger, ex);
            return Error(UpstreamUnavailableMessage);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient timeout (not caller cancellation).
            LogUpstreamFailure(logger, ex);
            return Error(UpstreamUnavailableMessage);
        }
        catch (JsonException ex)
        {
            LogUpstreamFailure(logger, ex);
            return Error(UpstreamUnavailableMessage);
        }
    }

    private static CallToolResult Success(string baseCurrency, string upstreamJson)
    {
        using var document = JsonDocument.Parse(upstreamJson);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("rates", out var ratesElement)
            || ratesElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Upstream response did not contain a rates object.");
        }

        var rates = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var property in ratesElement.EnumerateObject())
        {
            if (CurrencyRegex().IsMatch(property.Name)
                && property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetDecimal(out var rate))
            {
                rates[property.Name] = rate;
            }
        }

        string? date = null;
        if (root.TryGetProperty("date", out var dateElement) && dateElement.ValueKind == JsonValueKind.String)
        {
            var raw = dateElement.GetString();
            if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                date = parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }

        var json = JsonSerializer.Serialize(new
        {
            baseCurrency,
            date,
            rates
        });

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = json }]
        };
    }

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }]
    };

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Exchange rate provider returned HTTP {StatusCode}.")]
    private static partial void LogUpstreamStatus(ILogger logger, int statusCode);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Exchange rate provider call failed.")]
    private static partial void LogUpstreamFailure(ILogger logger, Exception exception);
}

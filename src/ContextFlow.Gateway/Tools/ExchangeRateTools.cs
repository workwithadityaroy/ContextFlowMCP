using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ContextFlow.Gateway.Tools;

[McpServerToolType]
public sealed partial class ExchangeRateTools
{
    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyRegex();

    [McpServerTool(ReadOnly = true)]
    [Authorize(Roles = "Admin,Developer,Finance")]
    [Description("Fetches mock foreign exchange rates for a standard ISO 4217 3-letter currency code (e.g. USD, EUR). Available to Admin, Developer, and Finance roles.")]
    public static CallToolResult FetchExternalExchangeRates(
        [Description("ISO 4217 3-letter currency code (e.g. USD, EUR)")] string baseCurrency,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(baseCurrency))
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "Base currency code is required." }]
            };
        }

        var normalized = baseCurrency.Trim().ToUpperInvariant();
        if (!CurrencyRegex().IsMatch(normalized))
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = $"Invalid currency code '{normalized}'. Must be a 3-letter ISO 4217 code (e.g. USD, EUR)." }]
            };
        }

        var rateData = JsonSerializer.Serialize(new
        {
            baseCurrency = normalized,
            rates = new Dictionary<string, decimal>
            {
                ["USD"] = normalized == "USD" ? 1.0m : 1.08m,
                ["EUR"] = normalized == "EUR" ? 1.0m : 0.92m,
                ["GBP"] = 0.79m,
                ["JPY"] = 154.50m
            },
            updatedAt = DateTimeOffset.UtcNow
        });

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = rateData }]
        };
    }
}


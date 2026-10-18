using System.Text.RegularExpressions;
using ContextFlow.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ContextFlow.Infrastructure.Security;

/// <summary>
/// Sanitizes sensitive patterns (bearer tokens, passwords, API keys, card numbers, SSNs, and connection strings)
/// from audit payloads before persistence.
/// </summary>
public sealed partial class RegexRedactor(ILogger<RegexRedactor>? logger = null) : IRedactor
{
    private const int RegexTimeoutMs = 1000;
    private readonly ILogger<RegexRedactor>? _logger = logger;

    /// <inheritdoc />
    public string? Redact(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        try
        {
            var result = input;
            result = BearerTokenRegex().Replace(result, "Bearer [REDACTED]");
            result = ConnectionStringRegex().Replace(result, "$1=[REDACTED]");
            result = ApiKeyRegex().Replace(result, "$1: [REDACTED]");
            result = CreditCardRegex().Replace(result, "[REDACTED_CARD]");
            result = SsnRegex().Replace(result, "[REDACTED_SSN]");
            return result;
        }
        catch (RegexMatchTimeoutException ex)
        {
            if (_logger is not null)
            {
                LogRegexTimeout(_logger, ex);
            }

            return "[REDACTED_TIMEOUT]";
        }
        catch (Exception ex)
        {
            if (_logger is not null)
            {
                LogRedactionError(_logger, ex);
            }

            return input;
        }
    }

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: RegexTimeoutMs)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"(?i)(Password|Pwd|User Id|Uid)\s*=\s*[^;""'\r\n\\}]+", RegexOptions.None, matchTimeoutMilliseconds: RegexTimeoutMs)]
    private static partial Regex ConnectionStringRegex();

    [GeneratedRegex(@"(?i)(?:\\[""']|[""'])?(api[_-]?key|client[_-]?secret|secret|password|passwd|pwd)(?:\\[""']|[""'])?\s*[:=]\s*(?:\\[""']|[""'])?([^\s""',;\[\]\\}]+)(?:\\[""']|[""'])?", RegexOptions.None, matchTimeoutMilliseconds: RegexTimeoutMs)]
    private static partial Regex ApiKeyRegex();

    [GeneratedRegex(@"\b(?:\d{4}[ -]?){3}\d{4}\b", RegexOptions.None, matchTimeoutMilliseconds: RegexTimeoutMs)]
    private static partial Regex CreditCardRegex();

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.None, matchTimeoutMilliseconds: RegexTimeoutMs)]
    private static partial Regex SsnRegex();

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Regex match timed out during payload redaction.")]
    private static partial void LogRegexTimeout(ILogger logger, Exception ex);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Unexpected error occurred during payload redaction.")]
    private static partial void LogRedactionError(ILogger logger, Exception ex);
}

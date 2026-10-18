namespace ContextFlow.Core.Interfaces;

/// <summary>
/// Sanitizes sensitive patterns (bearer tokens, passwords, API keys, card numbers, SSNs) from audit payloads.
/// </summary>
public interface IRedactor
{
    /// <summary>
    /// Redacts sensitive values in the input text with safe replacement tokens.
    /// </summary>
    /// <param name="input">The raw text or JSON string to sanitize.</param>
    /// <returns>The sanitized string with sensitive patterns redacted, or null/empty if the input was null/empty.</returns>
    string? Redact(string? input);
}


namespace ContextFlow.Core.Interfaces;

/// <summary>
/// Represents the result of a context trimming operation.
/// </summary>
/// <param name="Content">The trimmed text or JSON content.</param>
/// <param name="OriginalBytes">The original UTF-8 byte count before trimming.</param>
/// <param name="TrimmedBytes">The UTF-8 byte count after trimming.</param>
public readonly record struct TrimResult(string Content, int OriginalBytes, int TrimmedBytes)
{
    /// <summary>
    /// Gets the number of bytes saved by the trimming operation.
    /// </summary>
    public int BytesSaved => Math.Max(0, OriginalBytes - TrimmedBytes);
}

/// <summary>
/// Trims tool execution output and context payloads to minimize token consumption and storage overhead.
/// </summary>
public interface IContextTrimmer
{
    /// <summary>
    /// Trims the provided content by stripping null properties from JSON or collapsing redundant whitespace in plain text.
    /// </summary>
    /// <param name="content">The raw content string to trim.</param>
    /// <returns>A <see cref="TrimResult"/> containing the trimmed content and byte metrics.</returns>
    TrimResult Trim(string content);
}

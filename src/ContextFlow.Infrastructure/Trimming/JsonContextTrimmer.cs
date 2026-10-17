using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using ContextFlow.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ContextFlow.Infrastructure.Trimming;

/// <summary>
/// Trims JSON context and tool results by stripping null properties and formatting compactly.
/// Falls back to whitespace and redundant line normalization when content is not valid JSON.
/// </summary>
public sealed partial class JsonContextTrimmer(ILogger<JsonContextTrimmer>? logger = null) : IContextTrimmer
{
    private static readonly JsonDocumentOptions _documentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonWriterOptions _writerOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ILogger<JsonContextTrimmer>? _logger = logger;

    /// <inheritdoc />
    public TrimResult Trim(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return new TrimResult(string.Empty, 0, 0);
        }

        var originalBytes = Encoding.UTF8.GetByteCount(content);

        try
        {
            using var document = JsonDocument.Parse(content, _documentOptions);
            using var memoryStream = new MemoryStream(originalBytes);
            using (var writer = new Utf8JsonWriter(memoryStream, _writerOptions))
            {
                WriteElement(document.RootElement, writer);
                writer.Flush();
            }

            var trimmedBytes = (int)memoryStream.Length;
            var trimmedContent = Encoding.UTF8.GetString(memoryStream.GetBuffer(), 0, trimmedBytes);
            return new TrimResult(trimmedContent, originalBytes, trimmedBytes);
        }
        catch (JsonException ex)
        {
            if (_logger is not null)
            {
                LogFallbackToPlainText(_logger, ex);
            }

            return TrimPlainText(content, originalBytes);
        }
    }

    private static void WriteElement(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Null)
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteElement(property.Value, writer);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(item, writer);
                }
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static TrimResult TrimPlainText(string content, int originalBytes)
    {
        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (normalized.Length == 0)
        {
            return new TrimResult(string.Empty, originalBytes, 0);
        }

        var collapsed = ConsecutiveBlankLinesRegex().Replace(normalized, "\n\n");
        var trimmedBytes = Encoding.UTF8.GetByteCount(collapsed);
        return new TrimResult(collapsed, originalBytes, trimmedBytes);
    }

    [GeneratedRegex(@"\n[ \t]*\n(?:[ \t]*\n)+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ConsecutiveBlankLinesRegex();

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Debug,
        Message = "Content is not valid JSON; falling back to plain text trimming.")]
    private static partial void LogFallbackToPlainText(ILogger logger, Exception ex);
}

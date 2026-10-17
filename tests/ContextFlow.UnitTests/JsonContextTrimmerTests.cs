using System.Text;
using ContextFlow.Core.Interfaces;
using ContextFlow.Infrastructure.Trimming;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContextFlow.UnitTests;

public sealed class JsonContextTrimmerTests
{
    private readonly JsonContextTrimmer _trimmer = new();

    [Fact]
    public void HasSinglePublicConstructorForUnambiguousDependencyInjection()
    {
        var constructors = typeof(JsonContextTrimmer).GetConstructors();
        Assert.Single(constructors);
    }

    [Fact]
    public void StripNullFieldsFromRootObjectRemovesNullFieldsAndPreservesNonNullFields()
    {
        // Arrange
        const string input = """
            {
                "id": 1,
                "name": "ContextFlow",
                "description": null,
                "active": true,
                "extra": null
            }
            """;

        // Act
        var result = _trimmer.Trim(input);

        // Assert
        Assert.Equal("""{"id":1,"name":"ContextFlow","active":true}""", result.Content);
        Assert.True(result.BytesSaved > 0);
        Assert.Equal(Encoding.UTF8.GetByteCount(input), result.OriginalBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(result.Content), result.TrimmedBytes);
        Assert.Equal(result.OriginalBytes - result.TrimmedBytes, result.BytesSaved);
    }

    [Fact]
    public void StripNullFieldsFromNestedObjectsRemovesNullFieldsAtAllLevels()
    {
        // Arrange
        const string input = """
            {
                "user": {
                    "id": 42,
                    "email": null,
                    "details": {
                        "role": "admin",
                        "department": null
                    }
                },
                "metadata": null
            }
            """;

        // Act
        var result = _trimmer.Trim(input);

        // Assert
        Assert.Equal("""{"user":{"id":42,"details":{"role":"admin"}}}""", result.Content);
        Assert.True(result.BytesSaved > 0);
    }

    [Fact]
    public void StripNullFieldsWhenAllObjectFieldsAreNullReturnsEmptyObject()
    {
        // Arrange
        const string input = """{"field1": null, "field2": null}""";

        // Act
        var result = _trimmer.Trim(input);

        // Assert
        Assert.Equal("{}", result.Content);
        Assert.Equal(Encoding.UTF8.GetByteCount(input), result.OriginalBytes);
        Assert.Equal(2, result.TrimmedBytes);
        Assert.Equal(result.OriginalBytes - 2, result.BytesSaved);
    }

    [Fact]
    public void TrimArrayWithNullElementsPreservesNullElementsInArray()
    {
        // Arrange
        const string input = """[1, null, "item", false, null]""";

        // Act
        var result = _trimmer.Trim(input);

        // Assert
        Assert.Equal("""[1,null,"item",false,null]""", result.Content);
    }

    [Fact]
    public void TrimArrayContainingObjectsWithNullFieldsStripsNullFieldsInsideObjects()
    {
        // Arrange
        const string input = """
            [
                {"id": 1, "name": "Alpha", "tag": null},
                {"id": 2, "name": null, "tag": "Beta"}
            ]
            """;

        // Act
        var result = _trimmer.Trim(input);

        // Assert
        Assert.Equal("""[{"id":1,"name":"Alpha"},{"id":2,"tag":"Beta"}]""", result.Content);
    }

    [Fact]
    public void TrimNestedArraysAndObjectsWithNullsCorrectlyStripsObjectNullsWhilePreservingArrayNulls()
    {
        // Arrange
        const string input = """
            {
                "items": [
                    null,
                    {"valid": true, "nullProp": null},
                    [1, null, 2]
                ]
            }
            """;

        // Act
        var result = _trimmer.Trim(input);

        // Assert
        Assert.Equal("""{"items":[null,{"valid":true},[1,null,2]]}""", result.Content);
    }

    [Fact]
    public void TrimIndentedJsonBecomesCompactAndReducesByteSize()
    {
        // Arrange
        const string input = """
            {
                "server": "ContextFlow",
                "port": 5000,
                "enabled": true
            }
            """;

        // Act
        var result = _trimmer.Trim(input);

        // Assert
        Assert.Equal("""{"server":"ContextFlow","port":5000,"enabled":true}""", result.Content);
        Assert.True(result.TrimmedBytes < result.OriginalBytes);
        Assert.True(result.BytesSaved > 0);
        Assert.Equal(result.OriginalBytes - result.TrimmedBytes, result.BytesSaved);
    }

    [Fact]
    public void TrimPlainTextWithConsecutiveBlankLinesCollapsesTwoOrMoreBlankLinesToOne()
    {
        // Arrange
        const string input = "Paragraph one.\n\n\n\nParagraph two.\n\n\nParagraph three.";

        // Act
        var result = _trimmer.Trim(input);

        // Assert
        Assert.Equal("Paragraph one.\n\nParagraph two.\n\nParagraph three.", result.Content);
        Assert.True(result.BytesSaved > 0);
    }

    [Fact]
    public void TrimPlainTextWithWhitespaceInBlankLinesCollapsesToSingleBlankLine()
    {
        // Arrange
        const string input = "Section 1\n   \n\t  \nSection 2";

        // Act
        var result = _trimmer.Trim(input);

        // Assert
        Assert.Equal("Section 1\n\nSection 2", result.Content);
        Assert.True(result.BytesSaved > 0);
    }

    [Fact]
    public void TrimPlainTextWithSingleBlankLinePreservesSingleBlankLine()
    {
        // Arrange
        const string input = "Line 1\n\nLine 2";

        // Act
        var result = _trimmer.Trim(input);

        // Assert
        Assert.Equal("Line 1\n\nLine 2", result.Content);
        Assert.Equal(0, result.BytesSaved);
    }

    [Fact]
    public void TrimPlainTextWithLeadingAndTrailingWhitespaceTrimsStartAndEnd()
    {
        // Arrange
        const string input = "   \r\n\r\nHeader\nBody\r\n\r\n   ";

        // Act
        var result = _trimmer.Trim(input);

        // Assert
        Assert.Equal("Header\nBody", result.Content);
        Assert.True(result.BytesSaved > 0);
    }

    [Fact]
    public void TrimPlainTextLineBreakNormalizationNormalizesCrLfAndCrToLf()
    {
        // Arrange
        const string input = "Line 1\r\nLine 2\rLine 3\nLine 4";

        // Act
        var result = _trimmer.Trim(input);

        // Assert
        Assert.Equal("Line 1\nLine 2\nLine 3\nLine 4", result.Content);
        Assert.Equal(Encoding.UTF8.GetByteCount(input), result.OriginalBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount("Line 1\nLine 2\nLine 3\nLine 4"), result.TrimmedBytes);
        Assert.True(result.BytesSaved > 0);
    }

    [Theory]
    [InlineData("{ invalid json without closing brace")]
    [InlineData("{ key: 'unquoted', broken ")]
    [InlineData("[1, 2, 3, missing_bracket")]
    [InlineData("Just a plain text error message from an external service.")]
    public void TrimMalformedJsonFallsBackToPlainTextTrimmingWithoutThrowing(string malformedInput)
    {
        // Act
        var result = _trimmer.Trim(malformedInput);

        // Assert
        Assert.False(string.IsNullOrEmpty(result.Content));
        Assert.Equal(Encoding.UTF8.GetByteCount(malformedInput), result.OriginalBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(result.Content), result.TrimmedBytes);
    }

    [Fact]
    public void TrimNullOrEmptyStringReturnsEmptyResultWithZeroBytes()
    {
        // Act
        var emptyResult = _trimmer.Trim(string.Empty);
        var nullResult = _trimmer.Trim(null!);

        // Assert
        Assert.Equal(string.Empty, emptyResult.Content);
        Assert.Equal(0, emptyResult.OriginalBytes);
        Assert.Equal(0, emptyResult.TrimmedBytes);
        Assert.Equal(0, emptyResult.BytesSaved);

        Assert.Equal(string.Empty, nullResult.Content);
        Assert.Equal(0, nullResult.OriginalBytes);
        Assert.Equal(0, nullResult.TrimmedBytes);
        Assert.Equal(0, nullResult.BytesSaved);
    }

    [Fact]
    public void TrimWhitespaceOnlyStringReturnsEmptyContentAndAccurateByteCalculations()
    {
        // Arrange
        const string input = "   \r\n\t  \r\n   ";

        // Act
        var result = _trimmer.Trim(input);

        // Assert
        Assert.Equal(string.Empty, result.Content);
        Assert.Equal(Encoding.UTF8.GetByteCount(input), result.OriginalBytes);
        Assert.Equal(0, result.TrimmedBytes);
        Assert.Equal(result.OriginalBytes, result.BytesSaved);
    }

    [Fact]
    public void TrimJsonWithUtf8MultiByteCharactersMeasuresUtf8BytesAccurately()
    {
        // Arrange: 🌍 (4 bytes) and 🚀 (4 bytes)
        const string input = """{"message": "Hello 🌍 World 🚀", "nullProp": null}""";

        // Act
        var result = _trimmer.Trim(input);

        // Assert
        Assert.Equal("""{"message":"Hello 🌍 World 🚀"}""", result.Content);
        Assert.Equal(Encoding.UTF8.GetByteCount(input), result.OriginalBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(result.Content), result.TrimmedBytes);
        Assert.Equal(result.OriginalBytes - result.TrimmedBytes, result.BytesSaved);
    }

    [Fact]
    public void TrimPrimitivesAndBooleansPreservedVerbatim()
    {
        // Arrange
        const string input = """{"flagTrue": true, "flagFalse": false, "num": -42.5, "zero": 0, "emptyStr": ""}""";

        // Act
        var result = _trimmer.Trim(input);

        // Assert
        Assert.Equal("""{"flagTrue":true,"flagFalse":false,"num":-42.5,"zero":0,"emptyStr":""}""", result.Content);
    }

    [Theory]
    [InlineData("42", "42")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("null", "null")]
    [InlineData("\"hello world\"", "\"hello world\"")]
    public void TrimJsonPrimitivesAtRootPreservedVerbatim(string input, string expected)
    {
        // Act
        var result = _trimmer.Trim(input);

        // Assert
        Assert.Equal(expected, result.Content);
        Assert.Equal(Encoding.UTF8.GetByteCount(input), result.OriginalBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(expected), result.TrimmedBytes);
    }

    [Fact]
    public void TrimWithLoggerProvidedWorksIdenticallyAndDoesNotThrow()
    {
        // Arrange
        var logger = NullLogger<JsonContextTrimmer>.Instance;
        var trimmerWithLogger = new JsonContextTrimmer(logger);

        // Act: Should log fallback at Debug level without throwing
        var jsonResult = trimmerWithLogger.Trim("""{"a": null, "b": 1}""");
        var textResult = trimmerWithLogger.Trim("Not valid JSON content\n\n\nMore content");

        // Assert
        Assert.Equal("""{"b":1}""", jsonResult.Content);
        Assert.Equal("Not valid JSON content\n\nMore content", textResult.Content);
    }

    [Fact]
    public void TrimResultBytesSavedNeverReturnsNegativeNumber()
    {
        // Arrange
        var result = new TrimResult("LongerContent", 10, 15);

        // Assert
        Assert.Equal(0, result.BytesSaved);
    }
}

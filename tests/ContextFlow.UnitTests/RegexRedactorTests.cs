using ContextFlow.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContextFlow.UnitTests;

public sealed class RegexRedactorTests
{
    private readonly RegexRedactor _redactor = new();

    [Fact]
    public void HasSinglePublicConstructorForUnambiguousDependencyInjection()
    {
        var constructors = typeof(RegexRedactor).GetConstructors();
        Assert.Single(constructors);
    }

    [Fact]
    public void RedactBearerTokenRedactsTokenValue()
    {
        // Arrange
        const string input = "Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";

        // Act
        var result = _redactor.Redact(input);

        // Assert
        Assert.Equal("Authorization: Bearer [REDACTED]", result);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", result);
    }

    [Fact]
    public void RedactStandaloneBearerTokenRedactsTokenValue()
    {
        // Arrange
        const string input = "Bearer eyJhbGciOi...";

        // Act
        var result = _redactor.Redact(input);

        // Assert
        Assert.Equal("Bearer [REDACTED]", result);
    }

    [Fact]
    public void RedactApiKeyAndPasswordInJsonPayloadRedactsBothSensitiveValues()
    {
        // Arrange
        const string input = """{"apiKey": "secret123", "password": "supersecret"}""";

        // Act
        var result = _redactor.Redact(input);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("apiKey: [REDACTED]", result);
        Assert.Contains("password: [REDACTED]", result);
        Assert.DoesNotContain("secret123", result);
        Assert.DoesNotContain("supersecret", result);
    }

    [Theory]
    [InlineData("api_key: secret123", "api_key: [REDACTED]", "secret123")]
    [InlineData("client_secret: 'topSecretValue'", "client_secret: [REDACTED]", "topSecretValue")]
    [InlineData("passwd=MyHiddenPassword;", "passwd: [REDACTED];", "MyHiddenPassword")]
    [InlineData("secret: my-vault-secret", "secret: [REDACTED]", "my-vault-secret")]
    [InlineData("client-secret: def456", "client-secret: [REDACTED]", "def456")]
    public void RedactVariousApiKeyAndSecretPatternsRedactsValues(string input, string expectedSubstring, string sensitiveValue)
    {
        // Act
        var result = _redactor.Redact(input);

        // Assert
        Assert.NotNull(result);
        Assert.Contains(expectedSubstring, result);
        Assert.DoesNotContain(sensitiveValue, result);
    }

    [Fact]
    public void RedactCreditCardWithSpacesRedactsCardNumber()
    {
        // Arrange
        const string input = "Payment processed with card 4111 2222 3333 4444 successfully.";

        // Act
        var result = _redactor.Redact(input);

        // Assert
        Assert.Equal("Payment processed with card [REDACTED_CARD] successfully.", result);
        Assert.DoesNotContain("4111 2222 3333 4444", result);
    }

    [Fact]
    public void RedactCreditCardWithHyphensRedactsCardNumber()
    {
        // Arrange
        const string input = "Customer card 4111-2222-3333-4444 on file.";

        // Act
        var result = _redactor.Redact(input);

        // Assert
        Assert.Equal("Customer card [REDACTED_CARD] on file.", result);
        Assert.DoesNotContain("4111-2222-3333-4444", result);
    }

    [Fact]
    public void RedactCreditCardContinuousDigitsRedactsCardNumber()
    {
        // Arrange
        const string input = "Card number: 4111222233334444";

        // Act
        var result = _redactor.Redact(input);

        // Assert
        Assert.Equal("Card number: [REDACTED_CARD]", result);
        Assert.DoesNotContain("4111222233334444", result);
    }

    [Fact]
    public void RedactSocialSecurityNumberRedactsSsn()
    {
        // Arrange
        const string input = "Applicant SSN: 123-45-6789 verified.";

        // Act
        var result = _redactor.Redact(input);

        // Assert
        Assert.Equal("Applicant SSN: [REDACTED_SSN] verified.", result);
        Assert.DoesNotContain("123-45-6789", result);
    }

    [Fact]
    public void RedactConnectionStringWithPwdAndUidRedactsSensitiveFields()
    {
        // Arrange
        const string input = "Server=db;Database=app;Uid=admin;Pwd=TopSecretPass;";

        // Act
        var result = _redactor.Redact(input);

        // Assert
        Assert.Equal("Server=db;Database=app;Uid=[REDACTED];Pwd=[REDACTED];", result);
        Assert.DoesNotContain("TopSecretPass", result);
        Assert.DoesNotContain("admin", result);
    }

    [Fact]
    public void RedactConnectionStringWithPasswordAndUserIdRedactsSensitiveFields()
    {
        // Arrange
        const string input = "Server=prod-sql.internal;Database=ContextFlow;User Id=service_user;Password=SuperComplexPass123!;";

        // Act
        var result = _redactor.Redact(input);

        // Assert
        Assert.Equal("Server=prod-sql.internal;Database=ContextFlow;User Id=[REDACTED];Password=[REDACTED];", result);
        Assert.DoesNotContain("SuperComplexPass123!", result);
        Assert.DoesNotContain("service_user", result);
    }

    [Fact]
    public void RedactCleanNonSensitivePayloadPassesThroughUnmodified()
    {
        // Arrange
        const string input = """{"status": "success", "count": 100, "message": "Everything normal"}""";

        // Act
        var result = _redactor.Redact(input);

        // Assert
        Assert.Equal(input, result);
        Assert.Same(input, result);
    }

    [Fact]
    public void RedactPlainTextWithoutSecretsReturnsSameInstance()
    {
        // Arrange
        const string input = "The quick brown fox jumps over the lazy dog.";

        // Act
        var result = _redactor.Redact(input);

        // Assert
        Assert.Equal(input, result);
        Assert.Same(input, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n  ")]
    public void RedactNullEmptyOrWhitespaceStringReturnsInputAsIs(string? input)
    {
        // Act
        var result = _redactor.Redact(input);

        // Assert
        Assert.Equal(input, result);
    }

    [Fact]
    public void RedactCombinedPayloadWithAllSensitiveTypesRedactsEveryMatch()
    {
        // Arrange
        const string input = """
            Bearer eyJhbGciOi...
            Conn: Server=db;Uid=admin;Pwd=TopSecretPass;
            Auth: apiKey: mySecret123
            Card: 4111-2222-3333-4444
            SSN: 123-45-6789
            """;

        // Act
        var result = _redactor.Redact(input);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("Bearer [REDACTED]", result);
        Assert.Contains("Uid=[REDACTED]", result);
        Assert.Contains("Pwd=[REDACTED]", result);
        Assert.Contains("apiKey: [REDACTED]", result);
        Assert.Contains("[REDACTED_CARD]", result);
        Assert.Contains("[REDACTED_SSN]", result);
        Assert.DoesNotContain("TopSecretPass", result);
        Assert.DoesNotContain("mySecret123", result);
        Assert.DoesNotContain("4111-2222-3333-4444", result);
        Assert.DoesNotContain("123-45-6789", result);
    }

    [Fact]
    public void RedactWithLoggerProvidedWorksIdenticallyAndDoesNotThrow()
    {
        // Arrange
        var logger = NullLogger<RegexRedactor>.Instance;
        var redactorWithLogger = new RegexRedactor(logger);
        const string input = "Bearer eyJhbGciOi... with password: mypassword";

        // Act
        var result = redactorWithLogger.Redact(input);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("Bearer [REDACTED]", result);
        Assert.Contains("password: [REDACTED]", result);
        Assert.DoesNotContain("mypassword", result);
    }

    [Fact]
    public void RedactEscapedQuotesInSerializedJsonContentRedactsSecrets()
    {
        // Arrange: simulates JsonSerializer.Serialize(result.Content) where Text contains embedded JSON
        const string input = """[{"type":"text","text":"{\"apiKey\":\"secret_escaped_123\",\"password\":\"escaped_pwd_456\"}"}]""";

        // Act
        var result = _redactor.Redact(input);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("apiKey: [REDACTED]", result);
        Assert.Contains("password: [REDACTED]", result);
        Assert.DoesNotContain("secret_escaped_123", result);
        Assert.DoesNotContain("escaped_pwd_456", result);
    }

    [Fact]
    public void RedactMultipleSecretsInOneStringRedactsEveryOccurrence()
    {
        // Arrange
        const string input = "first password: alpha1 then second password: beta2 and apiKey=gamma3 plus Bearer tok.en-123";

        // Act
        var result = _redactor.Redact(input);

        // Assert
        Assert.NotNull(result);
        Assert.DoesNotContain("alpha1", result);
        Assert.DoesNotContain("beta2", result);
        Assert.DoesNotContain("gamma3", result);
        Assert.DoesNotContain("tok.en-123", result);
        Assert.Equal(2, CountOccurrences(result, "password: [REDACTED]"));
        Assert.Contains("apiKey: [REDACTED]", result);
        Assert.Contains("Bearer [REDACTED]", result);
    }

    [Theory]
    [InlineData("BEARER abc123def456")]
    [InlineData("bearer abc123def456")]
    [InlineData("BeArEr abc123def456")]
    public void RedactBearerSchemeIsCaseInsensitive(string input)
    {
        // Act
        var result = _redactor.Redact(input);

        // Assert
        Assert.Equal("Bearer [REDACTED]", result);
        Assert.DoesNotContain("abc123def456", result);
    }

    [Fact]
    public void RedactJwtInsideJsonStringRedactsTokenAndKeepsJsonStructure()
    {
        // Arrange
        const string jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
        var input = $$"""{"header":"Bearer {{jwt}}","user":"alice"}""";

        // Act
        var result = _redactor.Redact(input);

        // Assert
        Assert.NotNull(result);
        Assert.DoesNotContain(jwt, result);
        Assert.DoesNotContain("SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c", result);
        Assert.Equal("""{"header":"Bearer [REDACTED]","user":"alice"}""", result);
    }

    [Fact]
    public void RedactPasswordInConnectionStringStyleExceptionTextRedactsPassword()
    {
        // Arrange
        const string input = "Failed to connect to Host=db.internal;Database=contextflow;Username=svc;Password=hunter2";

        // Act
        var result = _redactor.Redact(input);

        // Assert
        Assert.NotNull(result);
        Assert.DoesNotContain("hunter2", result);
        Assert.Contains("Password=[REDACTED]", result);
    }

    [Theory]
    [InlineData("PASSWORD: UpperCaseSecret", "UpperCaseSecret")]
    [InlineData("APIKEY=ABCDEF123", "ABCDEF123")]
    [InlineData("Client_Secret: MixedCaseValue", "MixedCaseValue")]
    public void RedactKeywordsAreCaseInsensitive(string input, string sensitiveValue)
    {
        // Act
        var result = _redactor.Redact(input);

        // Assert
        Assert.NotNull(result);
        Assert.DoesNotContain(sensitiveValue, result);
        Assert.Contains("[REDACTED]", result);
    }

    [Fact]
    public void RedactAlreadyRedactedOutputIsIdempotent()
    {
        // Arrange
        const string input = "Authorization: Bearer abc.def password: hunter2 card 4111-2222-3333-4444 ssn 123-45-6789";

        // Act
        var once = _redactor.Redact(input);
        var twice = _redactor.Redact(once);

        // Assert
        Assert.Equal(once, twice);
    }

    [Fact]
    public void RedactJsonWithoutSensitiveKeysButSimilarWordsIsUnchanged()
    {
        // Arrange
        const string input = """{"note":"keep calm and carry on","amount":1234,"currency":"USD"}""";

        // Act
        var result = _redactor.Redact(input);

        // Assert
        Assert.Same(input, result);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}

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
}

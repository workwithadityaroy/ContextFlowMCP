using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using ContextFlow.Core.Interfaces;
using ContextFlow.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ContextFlow.IntegrationTests;

public sealed class McpAuthorizationIntegrationTests(ContextFlowWebApplicationFactory factory)
    : IClassFixture<ContextFlowWebApplicationFactory>
{
    private readonly ContextFlowWebApplicationFactory _factory = factory;

    [Fact]
    public async Task UnauthenticatedRequestToMcpEndpointReturns401()
    {
        // Arrange
        var client = _factory.CreateClient();
        var content = new StringContent("{}", Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/mcp", content);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredTokenReturns401()
    {
        // Arrange - generate an expired JWT
        var token = CreateTestToken(
            issuer: ContextFlowWebApplicationFactory.TestIssuer,
            audience: ContextFlowWebApplicationFactory.TestAudience,
            signingKey: ContextFlowWebApplicationFactory.TestSigningKey,
            roles: ["Developer"],
            expiresAt: DateTime.UtcNow.AddMinutes(-5));

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var content = new StringContent("{}", Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/mcp", content);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WrongAudienceTokenReturns401()
    {
        // Arrange - generate token with invalid audience
        var token = CreateTestToken(
            issuer: ContextFlowWebApplicationFactory.TestIssuer,
            audience: "wrong-audience",
            signingKey: ContextFlowWebApplicationFactory.TestSigningKey,
            roles: ["Admin"],
            expiresAt: DateTime.UtcNow.AddHours(1));

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var content = new StringContent("{}", Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/mcp", content);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RevokedTokenReturns401()
    {
        // Arrange
        var token = await _factory.MintTokenAsync("user-revoked", "Revocation Test", ["Admin"]);
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var jti = handler.ReadJwtToken(token).Id;

        // Revoke the token
        using (var scope = _factory.Services.CreateScope())
        {
            var patService = scope.ServiceProvider.GetRequiredService<IPatTokenService>();
            var revoked = await patService.RevokeTokenAsync(jti);
            Assert.True(revoked);
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var content = new StringContent("{}", Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/mcp", content);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RoleSegregationFiltersToolsListAccurately()
    {
        // 1. Developer PAT
        var devToken = await _factory.MintTokenAsync("dev-user", "Dev Token", ["Developer"]);
        var devClient = await CreateMcpClientAsync(devToken);
        var devTools = await devClient.ListToolsAsync();

        Assert.Contains(devTools, t => MatchesTool(t.Name, "Echo"));
        Assert.Contains(devTools, t => MatchesTool(t.Name, "QuerySystemMetrics"));
        Assert.Contains(devTools, t => MatchesTool(t.Name, "FetchExternalExchangeRates"));
        Assert.DoesNotContain(devTools, t => MatchesTool(t.Name, "GetEmployeeProfile"));

        // 2. Finance PAT
        var financeToken = await _factory.MintTokenAsync("fin-user", "Finance Token", ["Finance"]);
        var financeClient = await CreateMcpClientAsync(financeToken);
        var financeTools = await financeClient.ListToolsAsync();

        Assert.Contains(financeTools, t => MatchesTool(t.Name, "Echo"));
        Assert.Contains(financeTools, t => MatchesTool(t.Name, "GetEmployeeProfile"));
        Assert.Contains(financeTools, t => MatchesTool(t.Name, "FetchExternalExchangeRates"));
        Assert.DoesNotContain(financeTools, t => MatchesTool(t.Name, "QuerySystemMetrics"));

        // 3. Admin PAT
        var adminToken = await _factory.MintTokenAsync("admin-user", "Admin Token", ["Admin"]);
        var adminClient = await CreateMcpClientAsync(adminToken);
        var adminTools = await adminClient.ListToolsAsync();

        Assert.Contains(adminTools, t => MatchesTool(t.Name, "Echo"));
        Assert.Contains(adminTools, t => MatchesTool(t.Name, "GetEmployeeProfile"));
        Assert.Contains(adminTools, t => MatchesTool(t.Name, "QuerySystemMetrics"));
        Assert.Contains(adminTools, t => MatchesTool(t.Name, "FetchExternalExchangeRates"));
    }

    [Fact]
    public async Task CallingUnauthorizedHiddenToolReturnsAccessDenied()
    {
        // Arrange - Developer PAT (cannot access GetEmployeeProfile)
        var devToken = await _factory.MintTokenAsync("dev-caller", "Dev Caller Token", ["Developer"]);
        var devClient = await CreateMcpClientAsync(devToken);

        // Act & Assert - Call employee profile directly as Developer (requires Admin or Finance)
        var ex = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await devClient.CallToolAsync(
                "get_employee_profile",
                new Dictionary<string, object?> { ["employeeCode"] = "EMP001" }));

        Assert.Contains("Access forbidden", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthorizedRolesCanInvokePermittedToolsEndToEnd()
    {
        // Arrange - Seed an employee record for GetEmployeeProfile
        _factory.SeedEmployee(new ContextFlow.Core.Entities.Employee
        {
            EmployeeCode = "EMP777",
            FullName = "Priya Sharma",
            Email = "priya.sharma@contextflow.example",
            Department = "Finance",
            JobTitle = "Principal Analyst",
            SyntheticSalary = 145000m,
            HireDate = new DateOnly(2023, 4, 10)
        });

        var adminToken = await _factory.MintTokenAsync("admin-e2e", "Admin E2E Token", ["Admin"]);
        var adminClient = await CreateMcpClientAsync(adminToken);

        // 1. Call get_employee_profile
        var empResult = await adminClient.CallToolAsync(
            "get_employee_profile",
            new Dictionary<string, object?> { ["employeeCode"] = "EMP777" });
        Assert.NotEqual(true, empResult.IsError);
        var empText = empResult.Content.OfType<TextContentBlock>().First().Text;
        Assert.Contains("Priya Sharma", empText);

        // 2. Call query_system_metrics
        var metricsResult = await adminClient.CallToolAsync("query_system_metrics");
        Assert.NotEqual(true, metricsResult.IsError);
        var metricsText = metricsResult.Content.OfType<TextContentBlock>().First().Text;
        Assert.Contains("healthy", metricsText);

        // 3. Call fetch_external_exchange_rates
        var fxResult = await adminClient.CallToolAsync(
            "fetch_external_exchange_rates",
            new Dictionary<string, object?> { ["baseCurrency"] = "USD" });
        Assert.NotEqual(true, fxResult.IsError);
        var fxText = fxResult.Content.OfType<TextContentBlock>().First().Text;
        Assert.Contains("EUR", fxText);
    }

    private async Task<McpClient> CreateMcpClientAsync(string bearerToken)
    {
        var httpClient = _factory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/mcp") },
            httpClient);

        return await McpClient.CreateAsync(transport);
    }

    private static bool MatchesTool(string toolName, string expected) =>
        string.Equals(toolName, expected, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName.Replace("_", ""), expected.Replace("_", ""), StringComparison.OrdinalIgnoreCase);

    private static string CreateTestToken(
        string issuer,
        string audience,
        string signingKey,
        string[] roles,
        DateTime expiresAt)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, "test-user"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };

        foreach (var role in roles)
        {
            claims.Add(new Claim("roles", role));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: expiresAt.AddDays(-1),
            expires: expiresAt,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler { MapInboundClaims = false }.WriteToken(token);
    }
}

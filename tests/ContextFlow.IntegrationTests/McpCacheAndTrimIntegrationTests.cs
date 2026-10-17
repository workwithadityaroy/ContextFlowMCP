using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using ContextFlow.Core.Entities;
using ContextFlow.IntegrationTests.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ContextFlow.IntegrationTests;

public sealed class McpCacheAndTrimIntegrationTests : IClassFixture<ContextFlowWebApplicationFactory>
{
    private readonly ContextFlowWebApplicationFactory _factory;
    private const string _testEmployeeCode = "EMP-CACHE-777";

    public McpCacheAndTrimIntegrationTests(ContextFlowWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.EnsureDatabaseCreated();
        _factory.SeedEmployee(new Employee
        {
            EmployeeCode = _testEmployeeCode,
            FullName = "Synthetic Cache Tester",
            Email = "cache.tester@example.internal",
            Department = "Finance",
            JobTitle = "Staff Performance Engineer",
            SyntheticSalary = 160000m,
            HireDate = new DateOnly(2022, 5, 20)
        });
    }

    [Fact]
    public async Task SecondIdenticalCallReturnsCachedResultWithoutQueryingDatabaseAndUnder5Ms()
    {
        // Arrange
        var userId = "user-cache-hit-" + Guid.NewGuid().ToString("N");
        var token = await _factory.MintTokenAsync(userId, "Cache Hit Test Token", ["Finance"]);
        var client = await CreateMcpClientAsync(token);
        var args = new Dictionary<string, object?> { ["employeeCode"] = _testEmployeeCode };

        // Act 1: Initial call (cache miss -> queries DB)
        var firstResult = await client.CallToolAsync("get_employee_profile", args);

        Assert.False(firstResult.IsError ?? false);
        Assert.NotNull(firstResult.Content);
        Assert.NotEmpty(firstResult.Content);
        var firstText = Assert.IsType<TextContentBlock>(firstResult.Content[0]).Text;
        Assert.Contains("Staff Performance Engineer", firstText);

        // Mutate the database record behind the cache's back to prove call #2 does NOT query the DB
        _factory.UpdateEmployeeJobTitle(_testEmployeeCode, "Mutated In Database Only");

        try
        {
            // Warm up client/JIT for accurate sub-5ms cache-hit measurement
            _ = await client.CallToolAsync("get_employee_profile", args);

            // Act 2: Timed cache hit call with identical arguments
            var sw = Stopwatch.StartNew();
            var secondResult = await client.CallToolAsync("get_employee_profile", args);
            sw.Stop();

            // Assert: Returned from cache (still has original pre-mutation title -> zero DB query!)
            Assert.False(secondResult.IsError ?? false);
            Assert.NotNull(secondResult.Content);

            var secondText = Assert.IsType<TextContentBlock>(secondResult.Content[0]).Text;
            Assert.Equal(firstText, secondText);
            Assert.Contains("Staff Performance Engineer", secondText);
            Assert.DoesNotContain("Mutated In Database Only", secondText);

            // Verify cache hit metadata
            Assert.NotNull(secondResult.Meta);
            var isCacheHit = secondResult.Meta["cacheHit"]?.GetValue<bool>();
            Assert.True(isCacheHit, "Second identical request must return cacheHit = true.");

            // Local cache-hit latency < 5 ms (per context.md §7 Day 5 verification)
            Assert.True(sw.Elapsed.TotalMilliseconds < 5.0, $"Local cache hit must be < 5 ms; took {sw.Elapsed.TotalMilliseconds:F2} ms.");
        }
        finally
        {
            _factory.UpdateEmployeeJobTitle(_testEmployeeCode, "Staff Performance Engineer");
        }
    }

    [Fact]
    public async Task DifferentUserMissesCacheForSameToolAndArguments()
    {
        // Arrange
        var userAId = "user-a-" + Guid.NewGuid().ToString("N");
        var userBId = "user-b-" + Guid.NewGuid().ToString("N");

        var tokenA = await _factory.MintTokenAsync(userAId, "User A Token", ["Finance"]);
        var tokenB = await _factory.MintTokenAsync(userBId, "User B Token", ["Finance"]);

        var clientA = await CreateMcpClientAsync(tokenA);
        var clientB = await CreateMcpClientAsync(tokenB);

        var args = new Dictionary<string, object?> { ["employeeCode"] = _testEmployeeCode };

        // User A primes the cache
        var resultA = await clientA.CallToolAsync("get_employee_profile", args);
        Assert.False(resultA.IsError ?? false);

        // Act: User B requests the exact same tool and arguments
        var resultB = await clientB.CallToolAsync("get_employee_profile", args);

        // Assert: User B gets a cache miss due to user isolation
        Assert.False(resultB.IsError ?? false);
        Assert.NotNull(resultB.Meta);
        var cacheHitB = resultB.Meta["cacheHit"]?.GetValue<bool>();
        Assert.False(cacheHitB, "User B should miss User A's cache entry.");
    }

    [Fact]
    public async Task ToolWithZeroCacheTtlBypassesCacheEntirely()
    {
        // Arrange: echo has CacheTtlSeconds = 0 in policy
        var userId = "user-echo-ttl-" + Guid.NewGuid().ToString("N");
        var token = await _factory.MintTokenAsync(userId, "Echo TTL Test", ["Developer"]);
        var client = await CreateMcpClientAsync(token);
        var args = new Dictionary<string, object?> { ["message"] = "test zero ttl" };

        // Act
        var result1 = await client.CallToolAsync("echo", args);
        var result2 = await client.CallToolAsync("echo", args);

        // Assert
        Assert.False(result1.IsError ?? false);
        Assert.False(result2.IsError ?? false);

        // Cache hit metadata should not be set because caching is bypassed
        var cacheHit = result2.Meta?["cacheHit"];
        Assert.Null(cacheHit);
    }

    [Fact]
    public async Task ToolErrorResultsAreNeverCached()
    {
        // Arrange
        var userId = "user-err-nocache-" + Guid.NewGuid().ToString("N");
        var token = await _factory.MintTokenAsync(userId, "Error Cache Test", ["Admin"]);
        var client = await CreateMcpClientAsync(token);
        var invalidArgs = new Dictionary<string, object?> { ["employeeCode"] = "NONEXISTENT_CODE" };

        // Act 1: Call with non-existent code returns IsError = true
        var result1 = await client.CallToolAsync("get_employee_profile", invalidArgs);
        Assert.True(result1.IsError);

        // Act 2: Immediate follow-up call
        var result2 = await client.CallToolAsync("get_employee_profile", invalidArgs);
        Assert.True(result2.IsError);

        // Assert: Errors are never cached
        var cacheHit = result2.Meta?["cacheHit"];
        Assert.Null(cacheHit);
    }

    [Fact]
    public async Task TrimFilterStripsNullFieldsAndReducesOutputBytes()
    {
        // Arrange
        var userId = "user-trim-metric-" + Guid.NewGuid().ToString("N");
        var token = await _factory.MintTokenAsync(userId, "Trim Metric Test", ["Admin", "Finance"]);
        var client = await CreateMcpClientAsync(token);
        var args = new Dictionary<string, object?> { ["employeeCode"] = _testEmployeeCode };

        // Act
        var result = await client.CallToolAsync("get_employee_profile", args);

        // Assert
        Assert.False(result.IsError ?? false);
        Assert.NotNull(result.Content);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;

        // Verify null fields (ManagerCode, TerminationDate, Notes) were stripped by TrimFilter
        Assert.DoesNotContain("ManagerCode", text);
        Assert.DoesNotContain("TerminationDate", text);
        Assert.DoesNotContain("Notes", text);
        Assert.DoesNotContain("null", text);

        // Verify strict byte reduction (trimmed output is smaller than raw)
        Assert.NotNull(result.Meta);
        var rawBytes = result.Meta["rawBytes"]?.GetValue<int>();
        var trimmedBytes = result.Meta["trimmedBytes"]?.GetValue<int>();
        var bytesSaved = result.Meta["bytesSaved"]?.GetValue<int>();

        Assert.NotNull(rawBytes);
        Assert.NotNull(trimmedBytes);
        Assert.NotNull(bytesSaved);
        Assert.True(trimmedBytes < rawBytes, $"Trimmed bytes ({trimmedBytes}) must be strictly smaller than raw bytes ({rawBytes}).");
        Assert.True(bytesSaved > 0);
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
}

using System.Text.Json;
using ContextFlow.Gateway.Filters;

namespace ContextFlow.UnitTests;

public sealed class CacheFilterTests
{
    [Fact]
    public void BuildCacheKeyProducesDeterministicHashRegardlessOfArgumentKeyOrder()
    {
        var doc1 = JsonDocument.Parse("""{"b": 2, "a": 1}""");
        var doc2 = JsonDocument.Parse("""{"a": 1, "b": 2}""");

        var args1 = doc1.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        var args2 = doc2.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

        var key1 = CacheFilter.BuildCacheKey("get_employee_profile", args1, "user1", ["Admin", "Finance"]);
        var key2 = CacheFilter.BuildCacheKey("get_employee_profile", args2, "user1", ["Finance", "Admin"]);

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void BuildCacheKeyIsolatesDifferentUsersAndRoles()
    {
        var doc = JsonDocument.Parse("""{"code": "EMP001"}""");
        var args = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

        var keyUser1 = CacheFilter.BuildCacheKey("tool", args, "user-1", ["Admin"]);
        var keyUser2 = CacheFilter.BuildCacheKey("tool", args, "user-2", ["Admin"]);
        var keyRoleFinance = CacheFilter.BuildCacheKey("tool", args, "user-1", ["Finance"]);

        Assert.NotEqual(keyUser1, keyUser2);
        Assert.NotEqual(keyUser1, keyRoleFinance);
    }

    [Fact]
    public void BuildCacheKeyHandlesNullAndEmptyArgumentsAndRoles()
    {
        var keyEmptyArgs = CacheFilter.BuildCacheKey("tool", null, "user-1", []);
        Assert.StartsWith("contextflow:cache:tool:empty:user-1:none", keyEmptyArgs);
    }

    [Fact]
    public void BuildCacheKeyNormalizesToolNameCasing()
    {
        var keyLower = CacheFilter.BuildCacheKey("echo", null, "user", ["Admin"]);
        var keyUpper = CacheFilter.BuildCacheKey("ECHO", null, "user", ["Admin"]);
        Assert.Equal(keyLower, keyUpper);
    }
}


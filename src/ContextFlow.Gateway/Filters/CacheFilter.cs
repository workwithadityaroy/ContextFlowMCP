using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ContextFlow.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ContextFlow.Gateway.Filters;

/// <summary>
/// MCP filter caching successful tool execution results for read-only tools with CacheTtlSeconds > 0.
/// Uses composite user-aware keys (tool + argsHash + userId + rolesHash) to prevent cross-tenant leakage.
/// Never caches error results.
/// </summary>
public static partial class CacheFilter
{
    private const string _cacheKeyPrefix = "contextflow:cache:";

    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> FilterCallTool(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            var toolName = request.Params?.Name;
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return await next(request, cancellationToken);
            }

            var services = request.Services ?? throw new InvalidOperationException("Service provider is unavailable.");
            if (!IsReadOnlyTool(services, toolName))
            {
                return await next(request, cancellationToken);
            }

            var policyProvider = services.GetRequiredService<IToolPolicyProvider>();
            var policy = await policyProvider.GetPolicyAsync(toolName, cancellationToken);

            if (policy is null || policy.CacheTtlSeconds <= 0)
            {
                return await next(request, cancellationToken);
            }

            var userId = request.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? request.User?.FindFirst("sub")?.Value
                ?? "anonymous";

            var roles = (request.User?.FindAll(ClaimTypes.Role) ?? [])
                .Concat(request.User?.FindAll("roles") ?? [])
                .Select(c => c.Value)
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var cacheKey = BuildCacheKey(toolName, request.Params?.Arguments, userId, roles);
            var resultCache = services.GetRequiredService<IResultCache>();
            var logger = services.GetService<ILoggerFactory>()?.CreateLogger("ContextFlow.Gateway.Filters.CacheFilter");

            // 1. Try cache
            var cachedJson = await resultCache.GetAsync(cacheKey, cancellationToken);
            if (!string.IsNullOrEmpty(cachedJson))
            {
                try
                {
                    var cachedResult = JsonSerializer.Deserialize<CallToolResult>(cachedJson, _serializerOptions);
                    if (cachedResult is not null)
                    {
                        cachedResult.Meta ??= [];
                        cachedResult.Meta["cacheHit"] = true;
                        return cachedResult;
                    }
                }
                catch (JsonException ex)
                {
                    if (logger is not null)
                    {
                        LogCacheDeserializationFailed(logger, ex, cacheKey);
                    }
                }
            }

            // 2. Cache miss -> invoke downstream
            var result = await next(request, cancellationToken);

            // 3. Cache successful results only (never cache IsError results)
            if (result.IsError != true)
            {
                try
                {
                    var jsonToCache = JsonSerializer.Serialize(result, _serializerOptions);
                    await resultCache.SetAsync(cacheKey, jsonToCache, TimeSpan.FromSeconds(policy.CacheTtlSeconds), cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (logger is not null)
                    {
                        LogCacheWriteFailed(logger, ex, cacheKey);
                    }
                }

                result.Meta ??= [];
                result.Meta["cacheHit"] = false;
            }

            return result;
        };

    private static bool IsReadOnlyTool(IServiceProvider services, string toolName)
    {
        var registeredTools = services.GetServices<McpServerTool>();
        var matchedTool = registeredTools.FirstOrDefault(
            t => string.Equals(t.ProtocolTool.Name, toolName, StringComparison.OrdinalIgnoreCase));

        // If the tool is registered in MCP server metadata, enforce ReadOnlyHint == true per context.md §3 & §6.5
        return matchedTool is null || matchedTool.ProtocolTool.Annotations?.ReadOnlyHint == true;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to deserialize cached tool result for key '{CacheKey}'; falling back to fresh tool execution.")]
    private static partial void LogCacheDeserializationFailed(ILogger logger, Exception ex, string cacheKey);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Failed to write tool result to cache for key '{CacheKey}'; failing open.")]
    private static partial void LogCacheWriteFailed(ILogger logger, Exception ex, string cacheKey);

    public static string BuildCacheKey(
        string toolName,
        IDictionary<string, JsonElement>? arguments,
        string userId,
        IReadOnlyList<string> roles)
    {
        var normalizedTool = toolName.Trim().ToLowerInvariant();
        var normalizedUser = userId.Trim();

        var argsHash = ComputeArgumentsHash(arguments);
        var rolesHash = ComputeRolesHash(roles);

        return $"{_cacheKeyPrefix}{normalizedTool}:{argsHash}:{normalizedUser}:{rolesHash}";
    }

    private static string ComputeArgumentsHash(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return "empty";
        }

        var sortedArgs = arguments
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        var json = JsonSerializer.Serialize(sortedArgs, _serializerOptions);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(hashBytes);
    }

    private static string ComputeRolesHash(IReadOnlyList<string> roles)
    {
        if (roles is null || roles.Count == 0)
        {
            return "none";
        }

        var normalizedRoles = string.Join(",", roles
            .Select(r => r.Trim().ToUpperInvariant())
            .OrderBy(r => r, StringComparer.Ordinal));

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRoles));
        return Convert.ToHexStringLower(hashBytes);
    }
}

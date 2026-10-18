using System.Text;
using ContextFlow.Core.Interfaces;
using ContextFlow.Gateway.Filters;
using ContextFlow.Infrastructure.Auditing;
using ContextFlow.Infrastructure.Data;
using ContextFlow.Infrastructure.Redis;
using ContextFlow.Infrastructure.Security;
using ContextFlow.Infrastructure.Trimming;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var governanceConnection = builder.Configuration.GetConnectionString("ContextFlowDb")
    ?? throw new InvalidOperationException("Connection string 'ContextFlowDb' was not found in configuration.");

var readOnlyConnection = builder.Configuration.GetConnectionString("ToolsReadOnlyDb")
    ?? throw new InvalidOperationException("Connection string 'ToolsReadOnlyDb' was not found in configuration.");

builder.Services.AddDbContext<ContextFlowDbContext>(options =>
    options.UseNpgsql(governanceConnection));

builder.Services.AddDbContext<ToolsReadOnlyDbContext>(options =>
    options.UseNpgsql(readOnlyConnection));

// Optional Redis distributed cache / state (fail-open if unreachable at startup)
var redisConnection = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    {
        var redisOptions = ConfigurationOptions.Parse(redisConnection);
        redisOptions.AbortOnConnectFail = false;
        return ConnectionMultiplexer.Connect(redisOptions);
    });
}

// Governance & policy providers
builder.Services.AddSingleton(new System.Collections.Concurrent.ConcurrentDictionary<string, ContextFlow.Core.Entities.ToolPolicy>(StringComparer.OrdinalIgnoreCase));
builder.Services.AddScoped<IToolPolicyProvider, RedisToolPolicyProvider>();
builder.Services.AddSingleton<IRateLimiter, RedisRateLimiter>();
builder.Services.AddSingleton<IResultCache, RedisResultCache>();
builder.Services.AddSingleton<IContextTrimmer, JsonContextTrimmer>();
builder.Services.AddScoped<IPatTokenService, PatTokenService>();
builder.Services.AddSingleton<IAuditQueue, ChannelAuditQueue>();
builder.Services.AddSingleton<IRedactor, RegexRedactor>();
builder.Services.AddHostedService<AuditWriterService>();
var auditRetentionDays = builder.Configuration.GetValue<int?>("Audit:RetentionDays") is > 0 and var days ? days : 30;
builder.Services.AddHostedService(sp => new AuditRetentionService(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<ILogger<AuditRetentionService>>(),
    sp.GetService<TimeProvider>(),
    auditRetentionDays));

// Authentication: HS256 JWT Bearer
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "contextflow";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "contextflow-gateway";
var jwtSigningKey = builder.Configuration["Jwt:SigningKey"]
    ?? throw new InvalidOperationException("Configuration 'Jwt:SigningKey' must be set (minimum 32 bytes).");

var keyBytes = Encoding.UTF8.GetBytes(jwtSigningKey);
if (keyBytes.Length < 32)
{
    throw new InvalidOperationException(
        $"Configuration 'Jwt:SigningKey' must be at least 32 bytes (256 bits). Current length is {keyBytes.Length} bytes.");
}

var securityKey = new SymmetricSecurityKey(keyBytes);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = securityKey,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            RoleClaimType = "roles",
            ClockSkew = TimeSpan.FromSeconds(5)
        };

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var jti = context.Principal?.FindFirst("jti")?.Value;
                if (string.IsNullOrEmpty(jti))
                {
                    context.Fail("Token is missing the required 'jti' claim.");
                    return;
                }

                var tokenService = context.HttpContext.RequestServices.GetRequiredService<IPatTokenService>();
                if (await tokenService.IsTokenRevokedAsync(jti, context.HttpContext.RequestAborted))
                {
                    context.Fail("Token has been revoked.");
                }
            }
        };
    });

builder.Services.AddAuthorization();

// MCP Server registration with request authorization and governance filters
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly()
    .AddAuthorizationFilters()
    .WithRequestFilters(filters =>
    {
        // 3. Tool Policy Filter (list & call)
        filters.AddListToolsFilter(ToolPolicyFilter.FilterListTools);
        filters.AddCallToolFilter(ToolPolicyFilter.FilterCallTool);

        // 4. Rate-Limit Filter (call)
        filters.AddCallToolFilter(RateLimitFilter.FilterCallTool);

        // 5. Cache Filter (call) - outermost to Trim
        filters.AddCallToolFilter(CacheFilter.FilterCallTool);

        // 6. Trim Filter (call) - innermost to Cache so cache stores trimmed result
        filters.AddCallToolFilter(TrimFilter.FilterCallTool);
    });

// 1. Audit Filter - Outermost filter in CallToolWithAlternateFilters (runs before AuthorizationFilters)
builder.Services.PostConfigure<ModelContextProtocol.Server.McpServerOptions>(options =>
{
#pragma warning disable MCPEXP002
    options.Filters.Request.CallToolWithAlternateFilters.Insert(0, AuditFilter.FilterCallToolWithAlternate);
#pragma warning restore MCPEXP002
});

var app = builder.Build();

// CLI bootstrapping command: dotnet run -- mint-admin-token
if (args.Length > 0 && args[0] == "mint-admin-token")
{
    using var scope = app.Services.CreateScope();
    var tokenService = scope.ServiceProvider.GetRequiredService<IPatTokenService>();
    var adminToken = await tokenService.MintTokenAsync(
        ownerUserId: "admin",
        name: "Initial Admin Token",
        roles: ["Admin"],
        lifetime: TimeSpan.FromDays(90));

    Console.WriteLine(adminToken);
    return;
}

app.UseAuthentication();
app.UseAuthorization();

app.MapMcp("/mcp").RequireAuthorization();

app.Run();

#pragma warning disable ASP0027 // Public Program class is required by public ContextFlowWebApplicationFactory : WebApplicationFactory<Program>
public partial class Program;
#pragma warning restore ASP0027

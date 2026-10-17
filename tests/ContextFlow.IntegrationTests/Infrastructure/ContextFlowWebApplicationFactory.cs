using ContextFlow.Core.Entities;
using ContextFlow.Core.Interfaces;
using ContextFlow.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace ContextFlow.IntegrationTests.Infrastructure;

public class ContextFlowWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string TestSigningKey = "test-jwt-secret-signing-key-for-integration-tests-min-32-bytes!!";
    public const string TestIssuer = "contextflow";
    public const string TestAudience = "contextflow-gateway";

    private readonly string _dbName = "TestDb_" + Guid.NewGuid().ToString("N");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:ContextFlowDb", "Host=localhost;Database=contextflow_test;Username=test;Password=test");
        builder.UseSetting("ConnectionStrings:ToolsReadOnlyDb", "Host=localhost;Database=contextflow_test;Username=test_ro;Password=test");
        builder.UseSetting("ConnectionStrings:Redis", string.Empty);
        builder.UseSetting("Jwt:SigningKey", TestSigningKey);
        builder.UseSetting("Jwt:Issuer", TestIssuer);
        builder.UseSetting("Jwt:Audience", TestAudience);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<ContextFlowDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ContextFlowDbContext>>();
            services.RemoveAll<ContextFlowDbContext>();

            services.RemoveAll<DbContextOptions<ToolsReadOnlyDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ToolsReadOnlyDbContext>>();
            services.RemoveAll<ToolsReadOnlyDbContext>();

            services.RemoveAll<IConnectionMultiplexer>();

            services.AddDbContext<ContextFlowDbContext>(options =>
                options.UseInMemoryDatabase(_dbName));

            services.AddDbContext<ToolsReadOnlyDbContext>(options =>
                options.UseInMemoryDatabase(_dbName));
        });
    }

    public async Task<string> MintTokenAsync(
        string userId,
        string name,
        IEnumerable<string> roles,
        TimeSpan? lifetime = null)
    {
        using var scope = Services.CreateScope();
        var patService = scope.ServiceProvider.GetRequiredService<IPatTokenService>();
        return await patService.MintTokenAsync(userId, name, roles, lifetime);
    }

    public void SeedEmployee(Employee employee)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContextFlowDbContext>();
        db.Employees.Add(employee);
        db.SaveChanges();
    }
}


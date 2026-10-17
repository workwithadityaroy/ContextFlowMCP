using ContextFlow.Core.Entities;
using ContextFlow.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ContextFlow.IntegrationTests;

public sealed class DbContextIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DbContextIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public void DbContextsAreResolvableFromServiceContainer()
    {
        using var scope = _factory.Services.CreateScope();

        var governanceDb = scope.ServiceProvider.GetService<ContextFlowDbContext>();
        var readOnlyDb = scope.ServiceProvider.GetService<ToolsReadOnlyDbContext>();

        Assert.NotNull(governanceDb);
        Assert.NotNull(readOnlyDb);
    }

    [Fact]
    public void ContextFlowDbContextModelContainsRequiredEntitiesAndIndexes()
    {
        using var scope = _factory.Services.CreateScope();
        var governanceDb = scope.ServiceProvider.GetRequiredService<ContextFlowDbContext>();
        var model = governanceDb.Model;

        // Verify entities
        Assert.NotNull(model.FindEntityType(typeof(ToolPolicy)));
        Assert.NotNull(model.FindEntityType(typeof(ToolExecutionLog)));
        Assert.NotNull(model.FindEntityType(typeof(PersonalAccessToken)));
        Assert.NotNull(model.FindEntityType(typeof(Employee)));

        // Verify ToolExecutionLog indexes per context.md §5
        var logEntity = model.FindEntityType(typeof(ToolExecutionLog));
        Assert.NotNull(logEntity);

        var indexes = logEntity.GetIndexes().ToList();
        Assert.Contains(indexes, i => i.Properties.Count == 1 && i.Properties[0].Name == "Timestamp");
        Assert.Contains(indexes, i => i.Properties.Count == 2 &&
                                      i.Properties.Any(p => p.Name == "ToolName") &&
                                      i.Properties.Any(p => p.Name == "Timestamp"));
    }

    [Fact]
    public void ToolsReadOnlyDbContextOnlyExposesBusinessEntities()
    {
        using var scope = _factory.Services.CreateScope();
        var readOnlyDb = scope.ServiceProvider.GetRequiredService<ToolsReadOnlyDbContext>();
        var model = readOnlyDb.Model;

        Assert.NotNull(model.FindEntityType(typeof(Employee)));
        Assert.Null(model.FindEntityType(typeof(ToolPolicy)));
        Assert.Null(model.FindEntityType(typeof(ToolExecutionLog)));
        Assert.Null(model.FindEntityType(typeof(PersonalAccessToken)));
    }
}


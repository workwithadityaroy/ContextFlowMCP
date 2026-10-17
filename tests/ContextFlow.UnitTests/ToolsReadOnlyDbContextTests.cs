using ContextFlow.Core.Entities;
using ContextFlow.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ContextFlow.UnitTests;

public sealed class ToolsReadOnlyDbContextTests
{
    private static DbContextOptions<ToolsReadOnlyDbContext> CreateInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ToolsReadOnlyDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
    }

    [Fact]
    public void ContextSetsNoTrackingQueryBehaviorByDefault()
    {
        // Arrange
        var options = CreateInMemoryOptions();

        // Act
        using var context = new ToolsReadOnlyDbContext(options);

        // Assert
        Assert.Equal(QueryTrackingBehavior.NoTracking, context.ChangeTracker.QueryTrackingBehavior);
    }

    [Fact]
    public void SaveChangesThrowsInvalidOperationException()
    {
        // Arrange
        var options = CreateInMemoryOptions();
        using var context = new ToolsReadOnlyDbContext(options);

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => context.SaveChanges());
        Assert.Contains("strictly read-only", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveChangesAsyncThrowsInvalidOperationException()
    {
        // Arrange
        var options = CreateInMemoryOptions();
        await using var context = new ToolsReadOnlyDbContext(options);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Contains("strictly read-only", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}


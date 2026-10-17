using ContextFlow.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContextFlow.Infrastructure.Data;

/// <summary>
/// Dedicated read-only database context used exclusively by tool implementations
/// to query business data under the SELECT-only PostgreSQL role.
/// </summary>
public sealed class ToolsReadOnlyDbContext : DbContext
{
    public ToolsReadOnlyDbContext(DbContextOptions<ToolsReadOnlyDbContext> options)
        : base(options)
    {
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

    public DbSet<Employee> Employees => Set<Employee>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Employee>(entity =>
        {
            entity.ToTable("Employees");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EmployeeCode).HasMaxLength(32);
            entity.Property(e => e.FullName).HasMaxLength(128);
            entity.Property(e => e.Email).HasMaxLength(128);
            entity.Property(e => e.Department).HasMaxLength(64);
            entity.Property(e => e.JobTitle).HasMaxLength(64);
            entity.Property(e => e.SyntheticSalary).HasPrecision(18, 2);
        });
    }

    public override int SaveChanges() =>
        throw new InvalidOperationException("ToolsReadOnlyDbContext is strictly read-only. Modifying business data via this context is prohibited.");

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new InvalidOperationException("ToolsReadOnlyDbContext is strictly read-only. Modifying business data via this context is prohibited.");

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("ToolsReadOnlyDbContext is strictly read-only. Modifying business data via this context is prohibited.");

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("ToolsReadOnlyDbContext is strictly read-only. Modifying business data via this context is prohibited.");
}


using ContextFlow.Core.Entities;
using ContextFlow.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace ContextFlow.Infrastructure.Data;

/// <summary>
/// Primary read/write database context for ContextFlow governance tables (policies, audit logs, PATs)
/// and schema migrations for demo business tables.
/// </summary>
public sealed class ContextFlowDbContext(DbContextOptions<ContextFlowDbContext> options) : DbContext(options)
{
    public DbSet<ToolPolicy> ToolPolicies => Set<ToolPolicy>();

    public DbSet<ToolExecutionLog> ToolExecutionLogs => Set<ToolExecutionLog>();

    public DbSet<PersonalAccessToken> PersonalAccessTokens => Set<PersonalAccessToken>();

    public DbSet<Employee> Employees => Set<Employee>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ToolPolicy configuration
        modelBuilder.Entity<ToolPolicy>(entity =>
        {
            entity.HasKey(e => e.ToolName);
            entity.Property(e => e.ToolName).HasMaxLength(128);
            entity.Property(e => e.AuditMode).HasConversion<string>().HasMaxLength(32);
        });

        // ToolExecutionLog configuration & indexes
        modelBuilder.Entity<ToolExecutionLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).HasMaxLength(128);
            entity.Property(e => e.TokenId).HasMaxLength(128);
            entity.Property(e => e.ToolName).HasMaxLength(128);
            entity.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.ErrorType).HasMaxLength(128);

            // Indexes required by context.md §5
            entity.HasIndex(e => e.Timestamp)
                  .HasDatabaseName("IX_ToolExecutionLogs_Timestamp");

            entity.HasIndex(e => new { e.ToolName, e.Timestamp })
                  .HasDatabaseName("IX_ToolExecutionLogs_ToolName_Timestamp");
        });

        // PersonalAccessToken configuration
        modelBuilder.Entity<PersonalAccessToken>(entity =>
        {
            entity.HasKey(e => e.Jti);
            entity.Property(e => e.Jti).HasMaxLength(128);
            entity.Property(e => e.Name).HasMaxLength(128);
            entity.Property(e => e.OwnerUserId).HasMaxLength(128);
            entity.Property(e => e.Roles).HasMaxLength(256);

            entity.HasIndex(e => e.OwnerUserId)
                  .HasDatabaseName("IX_PersonalAccessTokens_OwnerUserId");
        });

        // Employee configuration
        modelBuilder.Entity<Employee>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EmployeeCode).HasMaxLength(32);
            entity.Property(e => e.FullName).HasMaxLength(128);
            entity.Property(e => e.Email).HasMaxLength(128);
            entity.Property(e => e.Department).HasMaxLength(64);
            entity.Property(e => e.JobTitle).HasMaxLength(64);
            entity.Property(e => e.SyntheticSalary).HasPrecision(18, 2);

            entity.HasIndex(e => e.EmployeeCode).IsUnique()
                  .HasDatabaseName("IX_Employees_EmployeeCode");
        });

        // Seed default tool governance policies
        var seedTimestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        modelBuilder.Entity<ToolPolicy>().HasData(
            new ToolPolicy
            {
                ToolName = "echo",
                IsEnabled = true,
                MaxRequestsPerMinute = 60,
                CacheTtlSeconds = 0,
                AuditMode = AuditMode.Full,
                UpdatedAt = seedTimestamp
            },
            new ToolPolicy
            {
                ToolName = "get_employee_profile",
                IsEnabled = true,
                MaxRequestsPerMinute = 60,
                CacheTtlSeconds = 60,
                AuditMode = AuditMode.InputsOnly,
                UpdatedAt = seedTimestamp
            },
            new ToolPolicy
            {
                ToolName = "query_system_metrics",
                IsEnabled = true,
                MaxRequestsPerMinute = 30,
                CacheTtlSeconds = 0,
                AuditMode = AuditMode.Full,
                UpdatedAt = seedTimestamp
            },
            new ToolPolicy
            {
                ToolName = "fetch_external_exchange_rates",
                IsEnabled = true,
                MaxRequestsPerMinute = 120,
                CacheTtlSeconds = 300,
                AuditMode = AuditMode.Full,
                UpdatedAt = seedTimestamp
            }
        );

        // Seed synthetic employees (zero real PII per context.md §5)
        modelBuilder.Entity<Employee>().HasData(
            new Employee
            {
                Id = 1,
                EmployeeCode = "EMP-001",
                FullName = "Synthetic Alice Finance",
                Email = "alice.finance@example.internal",
                Department = "Finance",
                JobTitle = "Senior Financial Analyst",
                SyntheticSalary = 95000.00m,
                HireDate = new DateOnly(2022, 3, 15)
            },
            new Employee
            {
                Id = 2,
                EmployeeCode = "EMP-002",
                FullName = "Synthetic Bob Engineering",
                Email = "bob.dev@example.internal",
                Department = "Engineering",
                JobTitle = "Lead Cloud Architect",
                SyntheticSalary = 135000.00m,
                HireDate = new DateOnly(2021, 6, 1)
            },
            new Employee
            {
                Id = 3,
                EmployeeCode = "EMP-003",
                FullName = "Synthetic Charlie Ops",
                Email = "charlie.ops@example.internal",
                Department = "Operations",
                JobTitle = "Site Reliability Engineer",
                SyntheticSalary = 110000.00m,
                HireDate = new DateOnly(2023, 1, 10)
            },
            new Employee
            {
                Id = 4,
                EmployeeCode = "EMP-004",
                FullName = "Synthetic Diana Product",
                Email = "diana.product@example.internal",
                Department = "Product",
                JobTitle = "Director of AI Products",
                SyntheticSalary = 145000.00m,
                HireDate = new DateOnly(2020, 9, 20)
            },
            new Employee
            {
                Id = 5,
                EmployeeCode = "EMP-005",
                FullName = "Synthetic Evan Security",
                Email = "evan.security@example.internal",
                Department = "Security",
                JobTitle = "Security Governance Specialist",
                SyntheticSalary = 125000.00m,
                HireDate = new DateOnly(2022, 11, 5)
            }
        );
    }
}


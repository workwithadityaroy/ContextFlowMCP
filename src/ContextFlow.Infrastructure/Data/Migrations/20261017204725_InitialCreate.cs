using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace ContextFlow.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Employees",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EmployeeCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FullName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Email = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Department = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    JobTitle = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SyntheticSalary = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    HireDate = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Employees", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PersonalAccessTokens",
                columns: table => new
                {
                    Jti = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OwnerUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Roles = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonalAccessTokens", x => x.Jti);
                });

            migrationBuilder.CreateTable(
                name: "ToolExecutionLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TokenId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ToolName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    CacheHit = table.Column<bool>(type: "boolean", nullable: false),
                    InputJson = table.Column<string>(type: "text", nullable: true),
                    OutputJson = table.Column<string>(type: "text", nullable: true),
                    OutputBytes = table.Column<int>(type: "integer", nullable: false),
                    ErrorType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolExecutionLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ToolPolicies",
                columns: table => new
                {
                    ToolName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MaxRequestsPerMinute = table.Column<int>(type: "integer", nullable: false),
                    CacheTtlSeconds = table.Column<int>(type: "integer", nullable: false),
                    AuditMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolPolicies", x => x.ToolName);
                });

            migrationBuilder.InsertData(
                table: "Employees",
                columns: new[] { "Id", "Department", "Email", "EmployeeCode", "FullName", "HireDate", "JobTitle", "SyntheticSalary" },
                values: new object[,]
                {
                    { 1, "Finance", "alice.finance@example.internal", "EMP-001", "Synthetic Alice Finance", new DateOnly(2022, 3, 15), "Senior Financial Analyst", 95000.00m },
                    { 2, "Engineering", "bob.dev@example.internal", "EMP-002", "Synthetic Bob Engineering", new DateOnly(2021, 6, 1), "Lead Cloud Architect", 135000.00m },
                    { 3, "Operations", "charlie.ops@example.internal", "EMP-003", "Synthetic Charlie Ops", new DateOnly(2023, 1, 10), "Site Reliability Engineer", 110000.00m },
                    { 4, "Product", "diana.product@example.internal", "EMP-004", "Synthetic Diana Product", new DateOnly(2020, 9, 20), "Director of AI Products", 145000.00m },
                    { 5, "Security", "evan.security@example.internal", "EMP-005", "Synthetic Evan Security", new DateOnly(2022, 11, 5), "Security Governance Specialist", 125000.00m }
                });

            migrationBuilder.InsertData(
                table: "ToolPolicies",
                columns: new[] { "ToolName", "AuditMode", "CacheTtlSeconds", "IsEnabled", "MaxRequestsPerMinute", "UpdatedAt" },
                values: new object[,]
                {
                    { "echo", "Full", 0, true, 60, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { "fetch_external_exchange_rates", "Full", 300, true, 120, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { "get_employee_profile", "InputsOnly", 60, true, 60, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { "query_system_metrics", "Full", 0, true, 30, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Employees_EmployeeCode",
                table: "Employees",
                column: "EmployeeCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PersonalAccessTokens_OwnerUserId",
                table: "PersonalAccessTokens",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ToolExecutionLogs_Timestamp",
                table: "ToolExecutionLogs",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_ToolExecutionLogs_ToolName_Timestamp",
                table: "ToolExecutionLogs",
                columns: new[] { "ToolName", "Timestamp" });

            // Create contextflow_ro role and grant SELECT on business tables (context.md §2, §7)
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'contextflow_ro') THEN
        CREATE ROLE contextflow_ro WITH LOGIN;
    END IF;
END
$$;

GRANT USAGE ON SCHEMA public TO contextflow_ro;
GRANT SELECT ON TABLE ""Employees"" TO contextflow_ro;
REVOKE ALL ON TABLE ""ToolPolicies"", ""ToolExecutionLogs"", ""PersonalAccessTokens"" FROM contextflow_ro;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Employees");

            migrationBuilder.DropTable(
                name: "PersonalAccessTokens");

            migrationBuilder.DropTable(
                name: "ToolExecutionLogs");

            migrationBuilder.DropTable(
                name: "ToolPolicies");
        }
    }
}

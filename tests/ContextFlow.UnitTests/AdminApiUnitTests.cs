using System.Text.Json;
using ContextFlow.Core.Entities;
using ContextFlow.Core.Enums;
using ContextFlow.Gateway.Admin;
using ContextFlow.Infrastructure.Auditing;
using ContextFlow.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ContextFlow.UnitTests;

public sealed class AdminApiUnitTests
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ComputePercentileReturnsZeroForEmptyList()
    {
        Assert.Equal(0, AdminMetricsEndpoints.ComputePercentile([], 0.95));
    }

    [Fact]
    public void ComputePercentileReturnsSingleElementForSingleItemList()
    {
        Assert.Equal(42, AdminMetricsEndpoints.ComputePercentile([42], 0.95));
    }

    [Fact]
    public void ComputePercentileCalculatesNearestRank95thPercentile()
    {
        var sorted = Enumerable.Range(1, 100).Select(i => (long)i).ToArray();

        var p95 = AdminMetricsEndpoints.ComputePercentile(sorted, 0.95);
        var p50 = AdminMetricsEndpoints.ComputePercentile(sorted, 0.50);
        var p0 = AdminMetricsEndpoints.ComputePercentile(sorted, 0.0);
        var p100 = AdminMetricsEndpoints.ComputePercentile(sorted, 1.0);

        Assert.Equal(95, p95);
        Assert.Equal(50, p50);
        Assert.Equal(1, p0);
        Assert.Equal(100, p100);
    }

    [Theory]
    [InlineData(0, 50, null, null, null)]
    [InlineData(-1, 50, null, null, null)]
    [InlineData(1, 0, null, null, null)]
    [InlineData(1, 201, null, null, null)]
    [InlineData(1, 50, "InvalidOutcome", null, null)]
    public async Task GetLogsAsyncReturns400ProblemOnInvalidParameters(
        int page,
        int pageSize,
        string? outcome,
        DateTimeOffset? from,
        DateTimeOffset? to)
    {
        await using var db = CreateInMemoryDb();

        var result = await AdminLogsEndpoints.GetLogsAsync(
            db,
            page: page,
            pageSize: pageSize,
            outcome: outcome,
            from: from,
            to: to);

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    [Fact]
    public async Task GetLogsAsyncReturns400WhenFromIsAfterTo()
    {
        await using var db = CreateInMemoryDb();
        var now = DateTimeOffset.UtcNow;

        var result = await AdminLogsEndpoints.GetLogsAsync(
            db,
            from: now,
            to: now.AddMinutes(-5));

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    [Fact]
    public async Task GetLogsAsyncSupportsToolAndUserFallbackAliasesAndPagination()
    {
        await using var db = CreateInMemoryDb();
        var baseTime = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

        db.ToolExecutionLogs.AddRange(
            new ToolExecutionLog
            {
                Id = 1,
                Timestamp = baseTime.AddSeconds(1),
                UserId = "alice",
                TokenId = "jti-1",
                ToolName = "echo",
                Outcome = ExecutionOutcome.Success,
                DurationMs = 4
            },
            new ToolExecutionLog
            {
                Id = 2,
                Timestamp = baseTime.AddSeconds(2),
                UserId = "alice",
                TokenId = "jti-1",
                ToolName = "echo",
                Outcome = ExecutionOutcome.RateLimited,
                DurationMs = 1,
                ErrorType = "RateLimited"
            },
            new ToolExecutionLog
            {
                Id = 3,
                Timestamp = baseTime.AddSeconds(3),
                UserId = "bob",
                TokenId = "jti-2",
                ToolName = "get_employee_profile",
                Outcome = ExecutionOutcome.Success,
                DurationMs = 12
            });
        await db.SaveChangesAsync();

        var result = await AdminLogsEndpoints.GetLogsAsync(
            db,
            page: 1,
            pageSize: 10,
            outcome: "ratelimited",
            tool: "echo",
            user: "alice");

        var ok = Assert.IsType<Ok<PagedLogsResponse>>(result);
        Assert.NotNull(ok.Value);
        Assert.Equal(1, ok.Value.TotalCount);
        Assert.Equal(1, ok.Value.TotalPages);
        var item = Assert.Single(ok.Value.Items);
        Assert.Equal(2, item.Id);
        Assert.Equal(ExecutionOutcome.RateLimited, item.Outcome);
    }

    [Fact]
    public async Task GetLogByIdAsyncReturns404WhenNotFound()
    {
        await using var db = CreateInMemoryDb();

        var result = await AdminLogsEndpoints.GetLogByIdAsync(99999, db);

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
    }

    [Fact]
    public async Task GetMetricsAsyncComputesPerToolRatesP95AndDroppedRecords()
    {
        await using var db = CreateInMemoryDb();
        var queue = new ChannelAuditQueue(capacity: 2);
        // Overflow bounded queue of capacity 2 by writing 4 entries -> 2 dropped
        for (var i = 0; i < 4; i++)
        {
            queue.TryEnqueue(new ToolExecutionLog
            {
                UserId = "u",
                TokenId = "t",
                ToolName = "echo",
                Outcome = ExecutionOutcome.Success
            });
        }

        db.ToolExecutionLogs.AddRange(
            new ToolExecutionLog
            {
                UserId = "u1",
                TokenId = "t1",
                ToolName = "echo",
                Outcome = ExecutionOutcome.Success,
                DurationMs = 10,
                CacheHit = false,
                OutputBytes = 100
            },
            new ToolExecutionLog
            {
                UserId = "u1",
                TokenId = "t1",
                ToolName = "echo",
                Outcome = ExecutionOutcome.Success,
                DurationMs = 50,
                CacheHit = true,
                OutputBytes = 100
            },
            new ToolExecutionLog
            {
                UserId = "u2",
                TokenId = "t2",
                ToolName = "echo",
                Outcome = ExecutionOutcome.Denied,
                DurationMs = 2,
                CacheHit = false,
                OutputBytes = 0
            });
        await db.SaveChangesAsync();

        var services = new ServiceCollection().BuildServiceProvider();
        var result = await AdminMetricsEndpoints.GetMetricsAsync(db, queue, services);

        var ok = Assert.IsType<Ok<SystemMetricsResponse>>(result);
        var metrics = ok.Value;
        Assert.NotNull(metrics);
        Assert.Equal("healthy", metrics.Status);
        Assert.True(metrics.DatabaseHealthy);
        Assert.False(metrics.RedisConfigured);
        Assert.Equal(3, metrics.TotalCalls);
        Assert.Equal(2, metrics.SuccessCount);
        Assert.Equal(1, metrics.ErrorCount);
        Assert.Equal(1, metrics.DeniedCount);
        Assert.Equal(1, metrics.CacheHitCount);
        Assert.Equal(200, metrics.TotalOutputBytes);
        Assert.Equal(2, metrics.DroppedAuditRecords);

        var echoMetrics = Assert.Single(metrics.PerTool);
        Assert.Equal("echo", echoMetrics.ToolName);
        Assert.Equal(3, echoMetrics.TotalCalls);
        Assert.Equal(50, echoMetrics.P95DurationMs);
    }

    [Fact]
    public void ContractsSerializeEnumsAsStrings()
    {
        var dto = new ToolPolicyDto(
            ToolName: "get_employee_profile",
            Description: "Employee lookup",
            IsReadOnly: true,
            AllowedRoles: ["Admin", "Finance"],
            IsEnabled: true,
            MaxRequestsPerMinute: 60,
            CacheTtlSeconds: 60,
            AuditMode: AuditMode.InputsOnly,
            UpdatedAt: DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(dto, _jsonOptions);
        Assert.Contains("\"auditMode\":\"InputsOnly\"", json, StringComparison.Ordinal);

        var roundtrip = JsonSerializer.Deserialize<ToolPolicyDto>(json, _jsonOptions);
        Assert.NotNull(roundtrip);
        Assert.Equal(AuditMode.InputsOnly, roundtrip.AuditMode);
    }

    [Fact]
    public async Task UpdateToolPolicyAsyncRejectsNonReadOnlyToolWithPositiveCacheTtl()
    {
        await using var db = CreateInMemoryDb();
        var nonReadOnlyTool = ModelContextProtocol.Server.McpServerTool.Create(
            (string input) => input,
            new ModelContextProtocol.Server.McpServerToolCreateOptions
            {
                Name = "mutating_tool",
                ReadOnly = false
            });

        var services = new ServiceCollection()
            .AddSingleton(nonReadOnlyTool)
            .BuildServiceProvider();

        var policyProvider = new ContextFlow.Infrastructure.Redis.RedisToolPolicyProvider(
            db,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ContextFlow.Infrastructure.Redis.RedisToolPolicyProvider>.Instance);

        var result = await AdminToolsEndpoints.UpdateToolPolicyAsync(
            "mutating_tool",
            new UpdateToolPolicyRequest(true, 60, 120, AuditMode.Full),
            db,
            policyProvider,
            services);

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    private static ContextFlowDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<ContextFlowDbContext>()
            .UseInMemoryDatabase("AdminUnitTestDb_" + Guid.NewGuid().ToString("N"))
            .Options;

        return new ContextFlowDbContext(options);
    }
}

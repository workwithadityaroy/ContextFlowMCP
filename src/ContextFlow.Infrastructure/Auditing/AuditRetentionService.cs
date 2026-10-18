using ContextFlow.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContextFlow.Infrastructure.Auditing;

/// <summary>
/// Background worker service that periodically purges audit logs older than the configured retention period.
/// </summary>
public sealed partial class AuditRetentionService(
    IServiceScopeFactory scopeFactory,
    ILogger<AuditRetentionService> logger,
    TimeProvider? timeProvider = null,
    int retentionDays = 30,
    TimeSpan? checkInterval = null) : BackgroundService
{
    private readonly TimeSpan _checkInterval = checkInterval ?? TimeSpan.FromHours(24);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = timeProvider is not null
            ? new PeriodicTimer(_checkInterval, timeProvider)
            : new PeriodicTimer(_checkInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var cutoff = (timeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow).AddDays(-retentionDays);
                    using var scope = scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<ContextFlowDbContext>();

                    int deletedCount;
                    if (dbContext.Database.IsRelational())
                    {
                        deletedCount = await dbContext.ToolExecutionLogs
                            .Where(l => l.Timestamp < cutoff)
                            .ExecuteDeleteAsync(stoppingToken);
                    }
                    else
                    {
                        var expired = await dbContext.ToolExecutionLogs
                            .Where(l => l.Timestamp < cutoff)
                            .ToListAsync(stoppingToken);

                        dbContext.ToolExecutionLogs.RemoveRange(expired);
                        await dbContext.SaveChangesAsync(stoppingToken);
                        deletedCount = expired.Count;
                    }

                    LogRetentionPurgeCompleted(logger, deletedCount, cutoff);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogRetentionPurgeFailed(logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal graceful shutdown
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Audit retention purge completed: deleted {DeletedCount} records older than {Cutoff}")]
    private static partial void LogRetentionPurgeCompleted(ILogger logger, int deletedCount, DateTimeOffset cutoff);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Audit retention purge failed")]
    private static partial void LogRetentionPurgeFailed(ILogger logger, Exception ex);
}

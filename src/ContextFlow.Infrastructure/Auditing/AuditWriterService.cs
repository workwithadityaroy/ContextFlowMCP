using ContextFlow.Core.Entities;
using ContextFlow.Core.Interfaces;
using ContextFlow.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContextFlow.Infrastructure.Auditing;

/// <summary>
/// Background worker service that asynchronously drains audit log entries from <see cref="IAuditQueue"/>
/// and persists them in batches to the database.
/// </summary>
public sealed partial class AuditWriterService(
    IAuditQueue auditQueue,
    IServiceScopeFactory scopeFactory,
    ILogger<AuditWriterService> logger,
    TimeProvider? timeProvider = null,
    int batchSize = 100,
    TimeSpan? flushInterval = null) : BackgroundService
{
    private readonly TimeSpan _flushInterval = flushInterval ?? TimeSpan.FromMilliseconds(500);
    private readonly int _batchSize = batchSize > 0 ? batchSize : 100;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<ToolExecutionLog>(_batchSize);
        var enumerator = auditQueue.ReadAllAsync(stoppingToken).GetAsyncEnumerator(stoppingToken);
        Task<bool>? pendingMoveNext = null;
        CancellationTokenSource? flushDelayCts = null;
        Task? flushDelayTask = null;

        void ResetFlushTimer()
        {
            if (flushDelayCts is not null)
            {
                flushDelayCts.Cancel();
                flushDelayCts.Dispose();
                flushDelayCts = null;
            }

            flushDelayTask = null;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                pendingMoveNext ??= enumerator.MoveNextAsync().AsTask();

                if (batch.Count == 0)
                {
                    var hasItem = await pendingMoveNext;
                    pendingMoveNext = null;

                    if (!hasItem)
                    {
                        break;
                    }

                    batch.Add(enumerator.Current);
                    if (batch.Count >= _batchSize)
                    {
                        await WriteBatchAsync(batch, stoppingToken);
                    }
                }
                else
                {
                    if (flushDelayTask is null)
                    {
                        flushDelayCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        flushDelayTask = timeProvider is not null
                            ? Task.Delay(_flushInterval, timeProvider, flushDelayCts.Token)
                            : Task.Delay(_flushInterval, flushDelayCts.Token);
                    }

                    var completedTask = await Task.WhenAny(pendingMoveNext, flushDelayTask);

                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (completedTask == pendingMoveNext)
                    {
                        var hasItem = await pendingMoveNext;
                        pendingMoveNext = null;

                        if (!hasItem)
                        {
                            break;
                        }

                        batch.Add(enumerator.Current);
                        if (batch.Count >= _batchSize)
                        {
                            ResetFlushTimer();
                            await WriteBatchAsync(batch, stoppingToken);
                        }
                    }
                    else
                    {
                        // Flush interval elapsed before batch reached capacity
                        ResetFlushTimer();
                        await WriteBatchAsync(batch, stoppingToken);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal graceful shutdown triggered by cancellation
        }
        finally
        {
            ResetFlushTimer();

            if (pendingMoveNext is { IsCompletedSuccessfully: true } && pendingMoveNext.Result)
            {
                batch.Add(enumerator.Current);
            }

            if (batch.Count > 0)
            {
                try
                {
                    await WriteBatchAsync(batch, CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogBatchWriteFailed(logger, ex, batch.Count);
                }
            }

            try
            {
                await enumerator.DisposeAsync();
            }
            catch (Exception)
            {
                // Suppress disposal exceptions on cancellation
            }
        }
    }

    private async Task WriteBatchAsync(List<ToolExecutionLog> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ContextFlowDbContext>();
            await dbContext.ToolExecutionLogs.AddRangeAsync(batch, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            LogBatchWritten(logger, batch.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogBatchWriteFailed(logger, ex, batch.Count);
            try
            {
                if (timeProvider is not null)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), timeProvider, cancellationToken);
                }
                else
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Ignore cancellation during error delay
            }
        }
        finally
        {
            batch.Clear();
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Successfully wrote batch of {Count} audit log entries to database")]
    private static partial void LogBatchWritten(ILogger logger, int count);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Failed to write batch of {Count} audit log entries to database")]
    private static partial void LogBatchWriteFailed(ILogger logger, Exception ex, int count);
}

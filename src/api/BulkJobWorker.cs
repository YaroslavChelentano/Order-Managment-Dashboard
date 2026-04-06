using Dapper;
using Npgsql;

namespace OrderManagement;

public sealed class BulkJobWorker(
    NpgsqlDataSource dataSource,
    IJobStore jobStore,
    EventBroadcaster events,
    ILogger<BulkJobWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            string? jobId;
            try
            {
                jobId = await jobStore.BlockingDequeueAsync(2, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Redis dequeue failed");
                await Task.Delay(1000, stoppingToken);
                continue;
            }

            if (jobId is null) continue;

            try
            {
                await ProcessJobAsync(jobId, stoppingToken);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Job {JobId} failed", jobId);
                await jobStore.SetStatusAsync(jobId, "failed", stoppingToken);
            }
        }
    }

    private async Task ProcessJobAsync(string jobId, CancellationToken ct)
    {
        var payload = await jobStore.GetPayloadAsync(jobId, ct);
        if (payload is null)
        {
            await jobStore.SetStatusAsync(jobId, "failed", ct);
            return;
        }

        var action = payload.Action.ToLowerInvariant();
        foreach (var orderId in payload.OrderIds)
        {
            var (completed, failed) = await TryProcessOneOrderAsync(orderId, action, ct);
            if (completed) await jobStore.IncrementCompletedAsync(jobId, 1, ct);
            else if (failed) await jobStore.IncrementFailedAsync(jobId, 1, ct);
        }

        var progress = await jobStore.GetProgressAsync(jobId, ct);
        var finalStatus = progress.Failed == progress.Total && progress.Total > 0 ? "failed" : "completed";
        await jobStore.SetStatusAsync(jobId, finalStatus, ct);
        await events.BroadcastBulkCompletedAsync(jobId, ct);
    }

    private async Task<(bool completed, bool failed)> TryProcessOneOrderAsync(string orderId, string action, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(@id::text, 0));",
            new { id = orderId },
            tx);

        var row = await conn.QuerySingleOrDefaultAsync<OrderRowLock>(
            "SELECT id, status, version FROM orders WHERE id = @id FOR UPDATE",
            new { id = orderId },
            tx);

        if (row is null)
        {
            await tx.CommitAsync(ct);
            return (false, true);
        }

        var now = DateTime.UtcNow;
        if (action == BulkActions.Approve)
        {
            if (row.Status == OrderStatuses.Cancelled)
            {
                await tx.CommitAsync(ct);
                return (false, true);
            }

            if (row.Status == OrderStatuses.Approved)
            {
                await tx.CommitAsync(ct);
                return (true, false);
            }

            if (row.Status != OrderStatuses.Pending)
            {
                await tx.CommitAsync(ct);
                return (false, true);
            }

            var n = await conn.ExecuteAsync(
                """
                UPDATE orders
                SET status = @newStatus, updated_at = @now, version = version + 1
                WHERE id = @id AND version = @v
                """,
                new { id = orderId, now, v = row.Version, newStatus = OrderStatuses.Approved },
                tx);
            if (n == 0)
            {
                await tx.CommitAsync(ct);
                return (false, true);
            }

            await tx.CommitAsync(ct);
            return (true, false);
        }

        if (action == BulkActions.Reject)
        {
            if (row.Status == OrderStatuses.Cancelled)
            {
                await tx.CommitAsync(ct);
                return (false, true);
            }

            if (row.Status == OrderStatuses.Rejected)
            {
                await tx.CommitAsync(ct);
                return (true, false);
            }

            if (row.Status != OrderStatuses.Pending)
            {
                await tx.CommitAsync(ct);
                return (false, true);
            }

            var n = await conn.ExecuteAsync(
                """
                UPDATE orders
                SET status = @newStatus, updated_at = @now, version = version + 1
                WHERE id = @id AND version = @v
                """,
                new { id = orderId, now, v = row.Version, newStatus = OrderStatuses.Rejected },
                tx);
            if (n == 0)
            {
                await tx.CommitAsync(ct);
                return (false, true);
            }

            await tx.CommitAsync(ct);
            return (true, false);
        }

        if (action == BulkActions.Flag)
        {
            var n = await conn.ExecuteAsync(
                """
                UPDATE orders
                SET flagged = TRUE, updated_at = @now, version = version + 1
                WHERE id = @id AND version = @v
                """,
                new { id = orderId, now, v = row.Version },
                tx);
            if (n == 0)
            {
                await tx.CommitAsync(ct);
                return (false, true);
            }

            await tx.CommitAsync(ct);
            return (true, false);
        }

        await tx.CommitAsync(ct);
        return (false, true);
    }

    private sealed record OrderRowLock(string Id, string Status, int Version);
}

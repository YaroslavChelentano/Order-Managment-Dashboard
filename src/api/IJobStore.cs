namespace OrderManagement;

public interface IJobStore
{
    Task EnqueueAsync(string jobId, string action, IReadOnlyList<string> orderIds, string? reason, CancellationToken ct);
    Task<string?> BlockingDequeueAsync(int timeoutSeconds, CancellationToken ct);
    Task<BulkJobPayload?> GetPayloadAsync(string jobId, CancellationToken ct);
    Task<JobProgressDto> GetProgressAsync(string jobId, CancellationToken ct);
    Task IncrementCompletedAsync(string jobId, int delta, CancellationToken ct);
    Task IncrementFailedAsync(string jobId, int delta, CancellationToken ct);
    Task SetStatusAsync(string jobId, string status, CancellationToken ct);
}

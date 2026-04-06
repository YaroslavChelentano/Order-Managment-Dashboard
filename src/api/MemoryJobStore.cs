using System.Text.Json;
using System.Threading.Channels;

namespace OrderManagement;

/// <summary>In-process job queue when Redis is unavailable (local dev / CI without Docker).</summary>
public sealed class MemoryJobStore : IJobStore
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Dictionary<string, string> _payloads = new();
    private readonly Dictionary<string, JobMeta> _meta = new();
    private readonly Lock _lock = new();

    private sealed class JobMeta
    {
        public string Status { get; set; } = "processing";
        public int Total { get; set; }
        public int Completed { get; set; }
        public int Failed { get; set; }
    }

    public Task EnqueueAsync(string jobId, string action, IReadOnlyList<string> orderIds, string? reason, CancellationToken ct)
    {
        var payloadObj = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["order_ids"] = orderIds,
            ["reason"] = reason,
        };
        var json = JsonSerializer.Serialize(payloadObj, AppJson.Options);
        lock (_lock)
        {
            _payloads[jobId] = json;
            _meta[jobId] = new JobMeta { Total = orderIds.Count, Status = "processing" };
        }

        _queue.Writer.TryWrite(jobId);
        return Task.CompletedTask;
    }

    public async Task<string?> BlockingDequeueAsync(int timeoutSeconds, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            return await _queue.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public Task<BulkJobPayload?> GetPayloadAsync(string jobId, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_payloads.TryGetValue(jobId, out var json))
                return Task.FromResult<BulkJobPayload?>(null);
            return Task.FromResult(JsonSerializer.Deserialize<BulkJobPayload>(json, AppJson.Options));
        }
    }

    public Task<JobProgressDto> GetProgressAsync(string jobId, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_meta.TryGetValue(jobId, out var m))
                return Task.FromResult(new JobProgressDto("failed", 0, 0, 0));
            return Task.FromResult(new JobProgressDto(m.Status, m.Total, m.Completed, m.Failed));
        }
    }

    public Task IncrementCompletedAsync(string jobId, int delta, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_meta.TryGetValue(jobId, out var m))
                m.Completed += delta;
        }

        return Task.CompletedTask;
    }

    public Task IncrementFailedAsync(string jobId, int delta, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_meta.TryGetValue(jobId, out var m))
                m.Failed += delta;
        }

        return Task.CompletedTask;
    }

    public Task SetStatusAsync(string jobId, string status, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_meta.TryGetValue(jobId, out var m))
                m.Status = status;
        }

        return Task.CompletedTask;
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;

namespace OrderManagement;

public sealed class RedisJobStore(IConnectionMultiplexer redis) : IJobStore
{
    private readonly IDatabase _db = redis.GetDatabase();

    public const string QueueKey = "bulk:queue";

    public static string PayloadKey(string jobId) => $"job:{jobId}:payload";
    public static string MetaHashKey(string jobId) => $"job:{jobId}:meta";

    public async Task EnqueueAsync(string jobId, string action, IReadOnlyList<string> orderIds, string? reason, CancellationToken ct)
    {
        var payloadObj = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["order_ids"] = orderIds,
            ["reason"] = reason,
        };
        var payload = JsonSerializer.Serialize(payloadObj, AppJson.Options);
        await _db.StringSetAsync(PayloadKey(jobId), payload).WaitAsync(ct);
        var meta = new HashEntry[]
        {
            new("status", "processing"),
            new("total", orderIds.Count),
            new("completed", 0),
            new("failed", 0),
        };
        await _db.HashSetAsync(MetaHashKey(jobId), meta).WaitAsync(ct);
        await _db.ListLeftPushAsync(QueueKey, jobId).WaitAsync(ct);
    }

    /// <summary>Blocking pop; returns job id or null on timeout.</summary>
    public async Task<string?> BlockingDequeueAsync(int timeoutSeconds, CancellationToken ct)
    {
        var result = await _db.ExecuteAsync("BRPOP", QueueKey, timeoutSeconds.ToString()).WaitAsync(ct);
        if (result.IsNull) return null;
#pragma warning disable CS0618
        if (result.Type != ResultType.MultiBulk) return null;
#pragma warning restore CS0618
        var arr = (RedisResult[])result!;
        if (arr.Length < 2) return null;
        return arr[1].ToString();
    }

    public async Task<BulkJobPayload?> GetPayloadAsync(string jobId, CancellationToken ct)
    {
        var s = await _db.StringGetAsync(PayloadKey(jobId)).WaitAsync(ct);
        if (s.IsNullOrEmpty) return null;
        return JsonSerializer.Deserialize<BulkJobPayload>(s.ToString()!, AppJson.Options);
    }

    public async Task<JobProgressDto> GetProgressAsync(string jobId, CancellationToken ct)
    {
        var hash = await _db.HashGetAllAsync(MetaHashKey(jobId)).WaitAsync(ct);
        if (hash.Length == 0)
            return new JobProgressDto("failed", 0, 0, 0);

        var status = "processing";
        long total = 0, completed = 0, failed = 0;
        foreach (var e in hash)
        {
            var name = e.Name.ToString();
            if (name == "status") status = e.Value.ToString()!;
            else if (name == "total") total = (long)e.Value;
            else if (name == "completed") completed = (long)e.Value;
            else if (name == "failed") failed = (long)e.Value;
        }

        return new JobProgressDto(status, (int)total, (int)completed, (int)failed);
    }

    public async Task IncrementCompletedAsync(string jobId, int delta, CancellationToken ct) =>
        await _db.HashIncrementAsync(MetaHashKey(jobId), "completed", delta).WaitAsync(ct);

    public async Task IncrementFailedAsync(string jobId, int delta, CancellationToken ct) =>
        await _db.HashIncrementAsync(MetaHashKey(jobId), "failed", delta).WaitAsync(ct);

    public async Task SetStatusAsync(string jobId, string status, CancellationToken ct) =>
        await _db.HashSetAsync(MetaHashKey(jobId), "status", status).WaitAsync(ct);
}

public sealed class BulkJobPayload
{
    public string Action { get; set; } = "";
    [JsonPropertyName("order_ids")]
    public List<string> OrderIds { get; set; } = [];
    public string? Reason { get; set; }
}

public sealed record JobProgressDto(string Status, int Total, int Completed, int Failed);

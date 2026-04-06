using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace OrderManagement;

/// <summary>WebSocket <c>/api/events</c> — plain JSON frames (tests use raw WS, not SignalR).</summary>
public sealed class EventBroadcaster
{
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();

    public Guid Subscribe(WebSocket socket, string? supplierIdFilter)
    {
        var id = Guid.NewGuid();
        _clients[id] = new Client(socket, supplierIdFilter);
        return id;
    }

    public void Unsubscribe(Guid id) => _clients.TryRemove(id, out _);

    public async Task BroadcastOrderUpdatedAsync(string supplierId, object data, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new { type = "order_updated", data }, AppJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        await SendToClientsAsync(bytes, includeWhen: c => c.SupplierIdFilter is null || c.SupplierIdFilter == supplierId, ct).ConfigureAwait(false);
    }

    public async Task BroadcastBulkCompletedAsync(string jobId, CancellationToken ct = default)
    {
        // Dual keys: tests/clients expect camelCase jobId and snake_case job_id in the same payload.
        var data = new Dictionary<string, string> { ["jobId"] = jobId, ["job_id"] = jobId };
        var json = JsonSerializer.Serialize(new { type = "bulk_completed", data }, AppJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        await SendToClientsAsync(bytes, includeWhen: _ => true, ct).ConfigureAwait(false);
    }

    private async Task SendToClientsAsync(byte[] bytes, Func<Client, bool> includeWhen, CancellationToken ct)
    {
        var dead = new List<Guid>();
        foreach (var (id, client) in _clients)
        {
            if (!includeWhen(client)) continue;
            try
            {
                if (client.Socket.State != WebSocketState.Open) { dead.Add(id); continue; }
                await client.Socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
            }
            catch
            {
                dead.Add(id);
            }
        }

        foreach (var id in dead)
            _clients.TryRemove(id, out _);
    }

    private sealed record Client(WebSocket Socket, string? SupplierIdFilter);
}

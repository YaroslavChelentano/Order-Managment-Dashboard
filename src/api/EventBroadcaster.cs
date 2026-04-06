using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace OrderManagement;

/// <summary>
/// Plain JSON text frames for <c>/api/events</c> (tests use raw WebSocket, not SignalR).
/// </summary>
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

    /// <summary>Clients without filter get all events; filtered clients only see matching <paramref name="supplierId"/>.</summary>
    public async Task BroadcastOrderUpdatedAsync(string supplierId, object data, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new { type = "order_updated", data }, AppJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        await SendToClientsAsync(bytes, includeWhen: c => c.SupplierIdFilter is null || c.SupplierIdFilter == supplierId, ct).ConfigureAwait(false);
    }

    public async Task BroadcastBulkCompletedAsync(string jobId, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new { type = "bulk_completed", data = new { jobId } }, AppJson.Options);
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

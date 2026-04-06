using System.Text.Json;

namespace OrderManagement;

public static class BulkJson
{
    /// <summary>Reads <c>order_ids</c> (snake) or <c>orderIds</c> (camel) — both are part of the public bulk contract.</summary>
    public static List<string> ReadOrderIds(JsonElement root)
    {
        List<string> ids = [];
        if (root.TryGetProperty("order_ids", out var snake) && snake.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in snake.EnumerateArray())
            {
                var s = e.GetString();
                if (s is not null) ids.Add(s);
            }
        }
        else if (root.TryGetProperty("orderIds", out var camel) && camel.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in camel.EnumerateArray())
            {
                var s = e.GetString();
                if (s is not null) ids.Add(s);
            }
        }

        return ids;
    }
}

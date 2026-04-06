using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrderManagement;

/// <summary>API JSON uses snake_case (test contract); dual-alias payloads use dictionaries where camelCase is required.</summary>
public static class AppJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}

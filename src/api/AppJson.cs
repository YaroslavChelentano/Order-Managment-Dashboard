using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrderManagement;

/// <summary>
/// JSON for API: snake_case property names (test contract). Dual-alias DTOs add camelCase via extra properties where needed.
/// </summary>
public static class AppJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}

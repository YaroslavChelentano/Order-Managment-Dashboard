namespace OrderManagement;

public static class PaginationQuery
{
    public static (int Limit, int Offset) ParseStandard(HttpRequest request, int defaultLimit = ApiLimits.DefaultPageSize)
    {
        var limit = int.TryParse(request.Query["limit"].FirstOrDefault(), out var l)
            ? Math.Clamp(l, 1, ApiLimits.MaxPageSize)
            : defaultLimit;
        var offset = int.TryParse(request.Query["offset"].FirstOrDefault(), out var o) && o >= 0 ? o : 0;
        return (limit, offset);
    }
}

using System.Globalization;

namespace OrderManagement;

public static class OrderListQueryParser
{
    private const string DefaultSortField = "created_at";
    private const string DefaultSortOrder = "desc";

    public static OrderListQueryArgs Parse(HttpRequest request)
    {
        var limitRaw = request.Query["limit"].FirstOrDefault();
        var offsetRaw = request.Query["offset"].FirstOrDefault();
        var limit = ApiLimits.DefaultPageSize;
        var offset = 0;
        if (int.TryParse(limitRaw, out var l))
        {
            if (l < 0) limit = ApiLimits.OrdersNegativeLimitSubstitute;
            else limit = Math.Clamp(l, 1, ApiLimits.MaxPageSize);
        }

        if (int.TryParse(offsetRaw, out var o) && o >= 0) offset = o;

        var statusQ = request.Query["status"].FirstOrDefault();
        string[]? statuses = null;
        if (!string.IsNullOrEmpty(statusQ))
            statuses = statusQ.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var priority = request.Query["priority"].FirstOrDefault();
        var supplierId = request.Query["supplier_id"].FirstOrDefault();
        var warehouse = request.Query["warehouse"].FirstOrDefault();
        DateTime? dateFrom = null;
        DateTime? dateTo = null;
        if (DateTime.TryParse(request.Query["date_from"].FirstOrDefault(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var df))
            dateFrom = df;
        if (DateTime.TryParse(request.Query["date_to"].FirstOrDefault(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            dateTo = dt.Date.AddDays(1).AddTicks(-1);

        decimal? minTotal = null;
        if (decimal.TryParse(request.Query["min_total"].FirstOrDefault(), CultureInfo.InvariantCulture, out var mt))
            minTotal = mt;

        var search = request.Query["search"].FirstOrDefault();
        var sort = request.Query["sort"].FirstOrDefault() ?? DefaultSortField;
        var order = request.Query["order"].FirstOrDefault() ?? DefaultSortOrder;

        return new OrderListQueryArgs(limit, offset, statuses, priority, supplierId, warehouse, dateFrom, dateTo,
            minTotal, search, sort, order);
    }
}

public readonly record struct OrderListQueryArgs(
    int Limit,
    int Offset,
    string[]? Statuses,
    string? Priority,
    string? SupplierId,
    string? Warehouse,
    DateTime? DateFrom,
    DateTime? DateTo,
    decimal? MinTotal,
    string? Search,
    string Sort,
    string Order);

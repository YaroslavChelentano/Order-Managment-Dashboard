namespace OrderManagement;

/// <summary>Shared pagination and bulk limits (API + tests rely on these bounds).</summary>
public static class ApiLimits
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 10_000;
    /// <summary>When <c>limit</c> is negative on <c>GET /api/orders</c>, the handler substitutes this value (test contract).</summary>
    public const int OrdersNegativeLimitSubstitute = 100;
    public const int BulkMaxOrderIds = 10_000;
}

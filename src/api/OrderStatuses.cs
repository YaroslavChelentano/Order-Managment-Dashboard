namespace OrderManagement;

/// <summary>Order <c>status</c> values stored in PostgreSQL and exposed by the API.</summary>
public static class OrderStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Shipped = "shipped";
    public const string Delivered = "delivered";
    public const string Cancelled = "cancelled";
}

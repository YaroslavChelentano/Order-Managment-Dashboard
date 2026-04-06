using Dapper;
using Npgsql;

namespace OrderManagement;

public partial class OrderDb
{
    public async Task<Dictionary<string, object?>> GetDashboardStatsAsync(CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var totalOrders = await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*)::int FROM orders", cancellationToken: ct));
        var totalRevenue = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition("SELECT COALESCE(SUM(total_price), 0) FROM orders", cancellationToken: ct));
        var avgOrderValue = totalOrders > 0 ? totalRevenue / totalOrders : 0m;

        var statusRows = await conn.QueryAsync<(string Status, long Count, decimal TotalValue)>(
            new CommandDefinition(
                "SELECT status, COUNT(*)::bigint, COALESCE(SUM(total_price), 0) FROM orders GROUP BY status",
                cancellationToken: ct));

        var byStatus = new Dictionary<string, object>();
        foreach (var (status, count, totalValue) in statusRows)
        {
            byStatus[status] = new Dictionary<string, object> { ["count"] = count, ["total_value"] = totalValue };
        }

        var monthRows = await conn.QueryAsync<(string Month, long OrderCount, decimal Revenue)>(
            new CommandDefinition(
                """
                SELECT to_char(created_at AT TIME ZONE 'UTC', 'YYYY-MM') AS month,
                       COUNT(*)::bigint,
                       COALESCE(SUM(total_price), 0)
                FROM orders
                GROUP BY 1
                ORDER BY 1
                """,
                cancellationToken: ct));

        var monthMap = monthRows.ToDictionary(r => r.Month, r => (r.OrderCount, r.Revenue));
        var byMonth = new List<Dictionary<string, object>>();
        for (var d = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc); d < new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc); d = d.AddMonths(1))
        {
            var key = d.ToString("yyyy-MM");
            monthMap.TryGetValue(key, out var v);
            byMonth.Add(new Dictionary<string, object>
            {
                ["month"] = key,
                ["order_count"] = v == default ? 0L : v.OrderCount,
                ["revenue"] = v == default ? 0m : v.Revenue,
            });
        }

        var topSuppliers = await conn.QueryAsync<(string SupplierId, string SupplierName, decimal TotalRevenue)>(
            new CommandDefinition(
                """
                SELECT o.supplier_id, s.name, COALESCE(SUM(o.total_price), 0)::numeric AS tr
                FROM orders o
                INNER JOIN suppliers s ON s.id = o.supplier_id
                GROUP BY o.supplier_id, s.name
                ORDER BY tr DESC
                LIMIT 10
                """,
                cancellationToken: ct));

        var topList = topSuppliers.Select(r => new Dictionary<string, object?>
        {
            ["supplier_id"] = r.SupplierId,
            ["supplier_name"] = r.SupplierName,
            ["total_revenue"] = r.TotalRevenue,
        }).ToList();

        var whRows = await conn.QueryAsync<(string Warehouse, long Count, decimal TotalValue)>(
            new CommandDefinition(
                """
                SELECT COALESCE(NULLIF(trim(warehouse), ''), 'unassigned') AS w,
                       COUNT(*)::bigint,
                       COALESCE(SUM(total_price), 0)
                FROM orders
                GROUP BY 1
                ORDER BY 1
                """,
                cancellationToken: ct));

        var byWarehouse = whRows.Select(r => new Dictionary<string, object?>
        {
            ["warehouse"] = r.Warehouse,
            ["count"] = r.Count,
            ["total_value"] = r.TotalValue,
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["total_orders"] = totalOrders,
            ["total_revenue"] = totalRevenue,
            ["avg_order_value"] = avgOrderValue,
            ["by_status"] = byStatus,
            ["by_month"] = byMonth,
            ["top_suppliers"] = topList,
            ["by_warehouse"] = byWarehouse,
        };
    }

    public async Task<Dictionary<string, object?>> GetSupplierPerformanceAsync(string supplierId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var avgDelivery = await conn.ExecuteScalarAsync<double?>(
            new CommandDefinition(
                """
                SELECT AVG(EXTRACT(EPOCH FROM (updated_at - created_at)) / 86400.0)
                FROM orders
                WHERE supplier_id = @sid AND status = 'delivered'
                """,
                new { sid = supplierId },
                cancellationToken: ct));

        var rejectionRate = await conn.ExecuteScalarAsync<double>(
            new CommandDefinition(
                """
                SELECT CASE WHEN COUNT(*) = 0 THEN 0
                       ELSE COUNT(*) FILTER (WHERE status = 'rejected')::float / COUNT(*)::float END
                FROM orders WHERE supplier_id = @sid
                """,
                new { sid = supplierId },
                cancellationToken: ct));

        var avgOrderValue = await conn.ExecuteScalarAsync<decimal>(
            new CommandDefinition(
                "SELECT COALESCE(AVG(total_price), 0) FROM orders WHERE supplier_id = @sid",
                new { sid = supplierId },
                cancellationToken: ct));

        var monthRows = await conn.QueryAsync<(string Month, long Cnt)>(
            new CommandDefinition(
                """
                SELECT to_char(created_at AT TIME ZONE 'UTC', 'YYYY-MM') AS m, COUNT(*)::bigint
                FROM orders WHERE supplier_id = @sid
                GROUP BY 1
                """,
                new { sid = supplierId },
                cancellationToken: ct));

        var monthMap = monthRows.ToDictionary(r => r.Month, r => r.Cnt);
        var monthlyTrend = new List<Dictionary<string, object>>();
        for (var d = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc); d < new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc); d = d.AddMonths(1))
        {
            var key = d.ToString("yyyy-MM");
            monthlyTrend.Add(new Dictionary<string, object>
            {
                ["month"] = key,
                ["order_count"] = monthMap.TryGetValue(key, out var c) ? c : 0L,
            });
        }

        var priceConsistency = await conn.ExecuteScalarAsync<double>(
            new CommandDefinition(
                """
                SELECT CASE WHEN COUNT(*) = 0 THEN 0
                       ELSE COUNT(*) FILTER (
                         WHERE o.unit_price BETWEEN p.price * 0.8 AND p.price * 1.2
                       )::float / COUNT(*)::float END
                FROM orders o
                INNER JOIN products p ON p.id = o.product_id
                WHERE o.supplier_id = @sid
                """,
                new { sid = supplierId },
                cancellationToken: ct));

        return new Dictionary<string, object?>
        {
            ["avg_delivery_days"] = avgDelivery ?? 0d,
            ["rejection_rate"] = rejectionRate,
            ["avg_order_value"] = avgOrderValue,
            ["monthly_trend"] = monthlyTrend,
            ["price_consistency"] = priceConsistency,
        };
    }

    public async Task<List<Dictionary<string, object?>>> GetAnomaliesAsync(CancellationToken ct)
    {
        const string sql = """
            WITH supplier_bad_ratio AS (
                SELECT o2.supplier_id,
                       COUNT(*) FILTER (
                           WHERE ABS(o2.total_price - (o2.quantity::numeric * o2.unit_price)) > 0.01
                              OR NOT s2.active
                              OR o2.quantity < 0
                              OR o2.updated_at < o2.created_at
                       )::float / NULLIF(COUNT(*)::float, 0) AS ratio
                FROM orders o2
                INNER JOIN suppliers s2 ON s2.id = o2.supplier_id
                GROUP BY o2.supplier_id
            )
            SELECT o.id,
                   ARRAY_REMOVE(ARRAY[
                       CASE WHEN ABS(o.total_price - (o.quantity::numeric * o.unit_price)) > 0.01 THEN 'price_mismatch'::text END,
                       CASE WHEN NOT s.active THEN 'inactive_supplier' END,
                       CASE WHEN o.quantity < 0 THEN 'negative_quantity' END,
                       CASE WHEN o.updated_at < o.created_at THEN 'timestamp_anomaly' END,
                       CASE WHEN o.unit_price > (p.price * 3) THEN 'price_spike' END,
                       CASE WHEN EXTRACT(HOUR FROM (o.created_at AT TIME ZONE 'UTC')) >= 22
                                 OR EXTRACT(HOUR FROM (o.created_at AT TIME ZONE 'UTC')) < 6
                            THEN 'after_hours' END,
                       CASE WHEN br.ratio > 0.5 THEN 'risky_supplier' END
                   ], NULL) AS types
            FROM orders o
            INNER JOIN suppliers s ON s.id = o.supplier_id
            INNER JOIN products p ON p.id = o.product_id
            LEFT JOIN supplier_bad_ratio br ON br.supplier_id = o.supplier_id
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(string Id, string[]? Types)>(new CommandDefinition(sql, cancellationToken: ct));

        var list = new List<Dictionary<string, object?>>();
        foreach (var (id, types) in rows)
        {
            if (types is null || types.Length == 0) continue;
            var clean = types.Where(t => t is not null).Select(t => t!).ToArray();
            if (clean.Length == 0) continue;
            var severity = ComputeSeverity(clean);
            list.Add(new Dictionary<string, object?>
            {
                ["order_id"] = id,
                ["anomaly_types"] = clean.ToList(),
                ["severity"] = severity,
            });
        }

        return list;
    }

    private static string ComputeSeverity(string[] types)
    {
        var set = types.Select(t => t.ToLowerInvariant()).ToHashSet();
        if (set.Contains("negative_quantity") || set.Contains("price_mismatch") || set.Contains("timestamp_anomaly"))
            return "high";
        if (set.Contains("inactive_supplier") || set.Contains("price_spike"))
            return "medium";
        return "low";
    }
}

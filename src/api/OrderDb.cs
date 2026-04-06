using System.Text;
using Dapper;
using Npgsql;

namespace OrderManagement;

public sealed partial class OrderDb(NpgsqlDataSource dataSource)
{
    private static readonly HashSet<string> SortColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "created_at", "updated_at", "total_price", "quantity", "unit_price",
        "status", "priority", "supplier_id", "warehouse",
    };

    private static readonly HashSet<string> ValidStatuses =
    [
        "pending", "approved", "rejected", "shipped", "delivered", "cancelled",
    ];

    public async Task<(List<Dictionary<string, object?>> rows, int total)> ListOrdersAsync(
        string[]? statuses,
        string? priority,
        string? supplierId,
        string? warehouse,
        DateTime? dateFrom,
        DateTime? dateTo,
        decimal? minTotal,
        string? search,
        string sort,
        string order,
        int limit,
        int offset,
        CancellationToken ct)
    {
        sort = SortColumns.Contains(sort) ? sort : "created_at";
        order = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";

        var sql = new StringBuilder("""
            SELECT o.id, o.supplier_id, o.product_id, p.name AS product_name, o.quantity,
                   o.unit_price, o.total_price, o.status, o.priority,
                   o.created_at, o.updated_at,
                   COALESCE(o.warehouse, '') AS warehouse,
                   o.notes
            FROM orders o
            INNER JOIN products p ON p.id = o.product_id
            WHERE 1=1
            """);

        var dp = new DynamicParameters();
        if (statuses is { Length: > 0 })
        {
            sql.Append(" AND o.status = ANY(@statuses)");
            dp.Add("statuses", statuses);
        }

        if (!string.IsNullOrEmpty(priority))
        {
            sql.Append(" AND o.priority = @priority");
            dp.Add("priority", priority);
        }

        if (!string.IsNullOrEmpty(supplierId))
        {
            sql.Append(" AND o.supplier_id = @supplierId");
            dp.Add("supplierId", supplierId);
        }

        if (!string.IsNullOrEmpty(warehouse))
        {
            sql.Append(" AND o.warehouse = @warehouse");
            dp.Add("warehouse", warehouse);
        }

        if (dateFrom is not null)
        {
            sql.Append(" AND o.created_at >= @dateFrom");
            dp.Add("dateFrom", dateFrom.Value);
        }

        if (dateTo is not null)
        {
            sql.Append(" AND o.created_at <= @dateTo");
            dp.Add("dateTo", dateTo.Value);
        }

        if (minTotal is not null)
        {
            sql.Append(" AND o.total_price >= @minTotal");
            dp.Add("minTotal", minTotal.Value);
        }

        if (!string.IsNullOrEmpty(search))
        {
            sql.Append(" AND p.name ILIKE @search");
            dp.Add("search", "%" + search + "%");
        }

        var listSqlBuilder = sql.ToString();
        var whereStart = listSqlBuilder.IndexOf("WHERE 1=1", StringComparison.Ordinal);
        var countSql = "SELECT COUNT(*) FROM orders o INNER JOIN products p ON p.id = o.product_id " + listSqlBuilder[whereStart..];

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(countSql, dp, cancellationToken: ct));

        sql.Append($" ORDER BY o.{sort} {order} LIMIT @limit OFFSET @offset");
        dp.Add("limit", limit);
        dp.Add("offset", offset);

        var rows = await conn.QueryAsync(new CommandDefinition(sql.ToString(), dp, cancellationToken: ct));
        var list = new List<Dictionary<string, object?>>();
        foreach (var row in rows)
        {
            var d = (IDictionary<string, object>)row;
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in d)
                dict[kv.Key] = kv.Value;
            list.Add(dict);
        }

        return (list, total);
    }

    public async Task<Dictionary<string, object?>?> GetOrderByIdAsync(string id, CancellationToken ct)
    {
        const string sql = """
            SELECT o.id, o.supplier_id, o.product_id, p.name AS product_name, s.name AS supplier_name,
                   o.quantity, o.unit_price, o.total_price, o.status, o.priority,
                   o.created_at, o.updated_at, COALESCE(o.warehouse, '') AS warehouse, o.notes
            FROM orders o
            INNER JOIN products p ON p.id = o.product_id
            INNER JOIN suppliers s ON s.id = o.supplier_id
            WHERE o.id = @id
            """;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        if (row is null) return null;
        var d = (IDictionary<string, object>)row;
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in d) dict[kv.Key] = kv.Value;
        return dict;
    }

    public async Task<(bool ok, bool conflict, bool notFound, bool badRequest, Dictionary<string, object?>? body, string? oldStatus, string? supplierId)> PatchOrderAsync(
        string id, string? status, string? notes, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var current = await conn.QuerySingleOrDefaultAsync<(string Status, int Version)?>(
            new CommandDefinition("SELECT status, version FROM orders WHERE id = @id", new { id }, cancellationToken: ct));
        if (current is null) return (false, false, true, false, null, null, null);

        if (current.Value.Status == "cancelled")
            return (false, true, false, false, null, null, null);

        if (status is not null && !ValidStatuses.Contains(status))
            return (false, false, false, true, null, null, null);

        var oldStatus = current.Value.Status;
        var supplierId = await conn.ExecuteScalarAsync<string>(
            new CommandDefinition("SELECT supplier_id FROM orders WHERE id = @id", new { id }, cancellationToken: ct));

        var now = DateTime.UtcNow;
        if (status is not null && notes is not null)
        {
            var n = await conn.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE orders SET status = @status, notes = @notes, updated_at = @now, version = version + 1
                    WHERE id = @id AND version = @v
                    """,
                    new { status, notes, now, id, v = current.Value.Version },
                    cancellationToken: ct));
            if (n == 0) return (false, true, false, false, null, oldStatus, supplierId);
        }
        else if (status is not null)
        {
            var n = await conn.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE orders SET status = @status, updated_at = @now, version = version + 1
                    WHERE id = @id AND version = @v
                    """,
                    new { status, now, id, v = current.Value.Version },
                    cancellationToken: ct));
            if (n == 0) return (false, true, false, false, null, oldStatus, supplierId);
        }
        else if (notes is not null)
        {
            var n = await conn.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE orders SET notes = @notes, updated_at = @now, version = version + 1
                    WHERE id = @id AND version = @v
                    """,
                    new { notes, now, id, v = current.Value.Version },
                    cancellationToken: ct));
            if (n == 0) return (false, true, false, false, null, oldStatus, supplierId);
        }
        else
        {
            return (true, false, false, false, await GetOrderByIdAsync(id, ct), oldStatus, supplierId);
        }

        var body = await GetOrderByIdAsync(id, ct);
        return (true, false, false, false, body, oldStatus, supplierId);
    }

    public async Task<(List<Dictionary<string, object?>> rows, int total)> ListSuppliersAsync(int limit, int offset, CancellationToken ct)
    {
        const string sql = """
            SELECT s.id, s.name, s.email, s.rating, s.country, s.active, s.created_at
            FROM suppliers s
            ORDER BY s.id
            LIMIT @limit OFFSET @offset
            """;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM suppliers", cancellationToken: ct));
        var rows = await conn.QueryAsync(new CommandDefinition(sql, new { limit, offset }, cancellationToken: ct));
        return (ToDictList(rows), total);
    }

    public async Task<Dictionary<string, object?>?> GetSupplierByIdAsync(string id, CancellationToken ct)
    {
        const string sql = """
            SELECT s.id, s.name, s.email, s.rating, s.country, s.active, s.created_at,
                   COUNT(o.id)::int AS order_count,
                   COALESCE(SUM(o.total_price), 0)::numeric AS total_revenue
            FROM suppliers s
            LEFT JOIN orders o ON o.supplier_id = s.id
            WHERE s.id = @id
            GROUP BY s.id, s.name, s.email, s.rating, s.country, s.active, s.created_at
            """;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        if (row is null) return null;
        return ToDict((IDictionary<string, object>)row);
    }

    public async Task<(List<Dictionary<string, object?>> rows, int total)> ListProductsAsync(int limit, int offset, string? categoryRoot, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        string sql;
        object param;

        if (string.IsNullOrEmpty(categoryRoot))
        {
            sql = """
                SELECT id, name, category_id, sku, price
                FROM products
                ORDER BY id
                LIMIT @limit OFFSET @offset
                """;
            param = new { limit, offset };
        }
        else
        {
            sql = """
                WITH RECURSIVE tree AS (
                    SELECT id FROM categories WHERE id = @root
                    UNION ALL
                    SELECT c.id FROM categories c INNER JOIN tree t ON c.parent_id = t.id
                )
                SELECT p.id, p.name, p.category_id, p.sku, p.price
                FROM products p
                WHERE p.category_id IN (SELECT id FROM tree)
                ORDER BY p.id
                LIMIT @limit OFFSET @offset
                """;
            param = new { root = categoryRoot, limit, offset };
        }

        var totalSql = string.IsNullOrEmpty(categoryRoot)
            ? "SELECT COUNT(*) FROM products"
            : """
              WITH RECURSIVE tree AS (
                  SELECT id FROM categories WHERE id = @root
                  UNION ALL
                  SELECT c.id FROM categories c INNER JOIN tree t ON c.parent_id = t.id
              )
              SELECT COUNT(*) FROM products p WHERE p.category_id IN (SELECT id FROM tree)
              """;

        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(totalSql, string.IsNullOrEmpty(categoryRoot) ? new { } : new { root = categoryRoot }, cancellationToken: ct));
        var rows = await conn.QueryAsync(new CommandDefinition(sql, param, cancellationToken: ct));
        return (ToDictList(rows), total);
    }

    private static List<Dictionary<string, object?>> ToDictList(IEnumerable<dynamic> rows)
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (var row in rows)
        {
            var d = (IDictionary<string, object>)row;
            list.Add(ToDict(d));
        }

        return list;
    }

    private static Dictionary<string, object?> ToDict(IDictionary<string, object> d)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in d) dict[kv.Key] = kv.Value;
        return dict;
    }
}

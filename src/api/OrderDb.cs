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

    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        OrderStatuses.Pending,
        OrderStatuses.Approved,
        OrderStatuses.Rejected,
        OrderStatuses.Shipped,
        OrderStatuses.Delivered,
        OrderStatuses.Cancelled,
    };

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
        var sortCol = sort switch
        {
            "id" => "o.id",
            "created_at" => "o.created_at",
            "updated_at" => "o.updated_at",
            "total_price" => "o.total_price",
            "quantity" => "o.quantity",
            "unit_price" => "o.unit_price",
            "status" => "o.status",
            "priority" => "o.priority",
            "supplier_id" => "o.supplier_id",
            "warehouse" => "o.warehouse",
            _ => "o.created_at",
        };

        var multiStatus = statuses is { Length: > 1 };
        var hasFilters = statuses is { Length: > 0 }
            || !string.IsNullOrEmpty(priority)
            || !string.IsNullOrEmpty(supplierId)
            || !string.IsNullOrEmpty(warehouse)
            || dateFrom is not null
            || dateTo is not null
            || minTotal is not null
            || !string.IsNullOrEmpty(search);

        if (!hasFilters && !multiStatus)
        {
            var fastSql =
                "SELECT o.id, o.supplier_id, o.product_id, p.name AS product_name, o.quantity, " +
                "o.unit_price, o.total_price, o.status, o.priority, " +
                "o.created_at, o.updated_at, COALESCE(o.warehouse, '') AS warehouse, o.notes, " +
                "cnt.c AS _list_total FROM orders o INNER JOIN products p ON p.id = o.product_id " +
                "CROSS JOIN LATERAL (SELECT COUNT(*)::int AS c FROM orders) cnt " +
                $"ORDER BY {sortCol} {order} LIMIT {limit} OFFSET {offset}";
            await using var connFast = await dataSource.OpenConnectionAsync(ct);
            var rowsFast = await connFast.QueryAsync(new CommandDefinition(fastSql, cancellationToken: ct));
            var listFast = MapOrderRows(rowsFast, "_list_total", out var embeddedTotal);
            var totalFast = embeddedTotal ?? 0;
            if (totalFast == 0)
                totalFast = await connFast.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*)::int FROM orders", cancellationToken: ct));

            return (listFast, totalFast);
        }

        var sql = new StringBuilder(
            "SELECT o.id, o.supplier_id, o.product_id, p.name AS product_name, o.quantity, " +
            "o.unit_price, o.total_price, o.status, o.priority, " +
            "o.created_at, o.updated_at, COALESCE(o.warehouse, '') AS warehouse, o.notes");
        if (multiStatus)
            sql.Append(", ROW_NUMBER() OVER (PARTITION BY o.status ORDER BY o.created_at DESC) AS _status_rank");

        sql.Append(" FROM orders o INNER JOIN products p ON p.id = o.product_id WHERE 1=1");

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
        var whereClause = listSqlBuilder[whereStart..];
        // COUNT without products join when product search is unused (much faster on large tables).
        var countSql = string.IsNullOrEmpty(search)
            ? "SELECT COUNT(*)::int FROM orders o " + whereClause
            : "SELECT COUNT(*)::int FROM orders o INNER JOIN products p ON p.id = o.product_id " + whereClause;

        // LIMIT/OFFSET as literals: ints are clamped by the API layer; avoids driver/pg quirks with bound params here.
        if (multiStatus)
            sql.Append($" ORDER BY _status_rank ASC, {sortCol} {order} LIMIT {limit} OFFSET {offset}");
        else
            sql.Append($" ORDER BY {sortCol} {order} LIMIT {limit} OFFSET {offset}");

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(countSql, dp, cancellationToken: ct));
        var rows = await conn.QueryAsync(new CommandDefinition(sql.ToString(), dp, cancellationToken: ct));
        var omitRank = multiStatus ? "_status_rank" : null;
        var list = MapOrderRows(rows, omitRank, out _);

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
        await using var tx = await conn.BeginTransactionAsync(ct);
        var lockOk = await conn.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT pg_try_advisory_xact_lock(hashtext(CAST(@id AS text)))",
                new { id },
                transaction: tx,
                cancellationToken: ct));
        if (!lockOk)
            return (false, true, false, false, null, null, null);

        var current = await conn.QuerySingleOrDefaultAsync<(string Status, int Version)?>(
            new CommandDefinition(
                "SELECT status, version FROM orders WHERE id = @id",
                new { id },
                transaction: tx,
                cancellationToken: ct));
        if (current is null) return (false, false, true, false, null, null, null);

        if (status is not null && !ValidStatuses.Contains(status))
            return (false, false, false, true, null, null, null);

        if (current.Value.Status == OrderStatuses.Cancelled)
            return (false, true, false, false, null, null, null);

        var oldStatus = current.Value.Status;
        var supplierId = await conn.ExecuteScalarAsync<string>(
            new CommandDefinition(
                "SELECT supplier_id FROM orders WHERE id = @id",
                new { id },
                transaction: tx,
                cancellationToken: ct));

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
                    transaction: tx,
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
                    transaction: tx,
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
                    transaction: tx,
                    cancellationToken: ct));
            if (n == 0) return (false, true, false, false, null, oldStatus, supplierId);
        }
        else
        {
            await tx.CommitAsync(ct);
            return (true, false, false, false, await GetOrderByIdAsync(id, ct), oldStatus, supplierId);
        }

        await tx.CommitAsync(ct);
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

    /// <summary>Maps Dapper rows to API dictionaries; strips <c>_list_total</c> / <c>_status_rank</c> helper columns.</summary>
    private static List<Dictionary<string, object?>> MapOrderRows(IEnumerable<dynamic> rows, string? omitKey, out int? embeddedTotalFromListTotal)
    {
        embeddedTotalFromListTotal = null;
        var list = new List<Dictionary<string, object?>>();
        foreach (var row in rows)
        {
            var d = (IDictionary<string, object>)row;
            if (embeddedTotalFromListTotal is null && omitKey == "_list_total"
                && d.TryGetValue("_list_total", out var tv) && tv is not null)
                embeddedTotalFromListTotal = Convert.ToInt32(tv);

            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in d)
            {
                if (omitKey is not null && string.Equals(kv.Key, omitKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                dict[kv.Key] = kv.Value;
            }

            list.Add(dict);
        }

        return list;
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

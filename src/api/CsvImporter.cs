using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace OrderManagement;

public sealed class CsvImporter
{
    public static async Task ImportAllAsync(NpgsqlConnection conn, string dataDirectory, CancellationToken ct = default)
    {
        // COPY binary import cannot share an ambient transaction with batched Dapper inserts in all Npgsql versions; run in order.
        await ImportCategoriesAsync(conn, null, Path.Combine(dataDirectory, "categories.csv"), ct);
        await ImportSuppliersAsync(conn, null, Path.Combine(dataDirectory, "suppliers.csv"), ct);
        await ImportProductsAsync(conn, null, Path.Combine(dataDirectory, "products.csv"), ct);
        await ImportOrdersBinaryAsync(conn, Path.Combine(dataDirectory, "orders.csv"), ct);
    }

    private static async Task ImportCategoriesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string path, CancellationToken ct)
    {
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
        });
        await csv.ReadAsync();
        csv.ReadHeader();

        // CSV repeats some ids (e.g. cat_150–152); last row wins.
        const string sql = """
            INSERT INTO categories (id, name, parent_id)
            VALUES (@id, @name, @parent_id)
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                parent_id = EXCLUDED.parent_id;
            """;

        while (await csv.ReadAsync())
        {
            var id = csv.GetField(0)?.Trim();
            if (string.IsNullOrEmpty(id)) continue;
            var name = csv.GetField(1) ?? "";
            var parentRaw = csv.GetField(2);
            string? parentId = string.IsNullOrWhiteSpace(parentRaw) ? null : parentRaw.Trim();
            await conn.ExecuteAsync(new CommandDefinition(sql, new { id, name, parent_id = parentId }, transaction: tx, cancellationToken: ct));
        }
    }

    private static async Task ImportSuppliersAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string path, CancellationToken ct)
    {
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
        });
        await csv.ReadAsync();
        csv.ReadHeader();

        const string sql = """
            INSERT INTO suppliers (id, name, email, rating, country, active, created_at)
            VALUES (@id, @name, @email, @rating, @country, @active, @created_at);
            """;

        while (await csv.ReadAsync())
        {
            var id = csv.GetField(0)?.Trim();
            if (string.IsNullOrEmpty(id)) continue;
            var name = csv.GetField(1) ?? "";
            var email = csv.GetField(2);
            var rating = decimal.TryParse(csv.GetField(3), CultureInfo.InvariantCulture, out var r) ? r : (decimal?)null;
            var country = csv.GetField(4);
            var active = string.Equals(csv.GetField(5)?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            var createdAt = DateTime.TryParse(csv.GetField(6), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ca)
                ? ca
                : DateTime.UtcNow;
            await conn.ExecuteAsync(new CommandDefinition(sql, new
            {
                id,
                name,
                email,
                rating,
                country,
                active,
                created_at = createdAt,
            }, transaction: tx, cancellationToken: ct));
        }
    }

    private static async Task ImportProductsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string path, CancellationToken ct)
    {
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
        });
        await csv.ReadAsync();
        csv.ReadHeader();

        const string sql = """
            INSERT INTO products (id, name, category_id, sku, price)
            VALUES (@id, @name, @category_id, @sku, @price);
            """;

        while (await csv.ReadAsync())
        {
            var id = csv.GetField(0)?.Trim();
            if (string.IsNullOrEmpty(id)) continue;
            var name = csv.GetField(1) ?? "";
            var categoryId = csv.GetField(2);
            var sku = csv.GetField(3);
            var price = decimal.Parse(csv.GetField(4) ?? "0", CultureInfo.InvariantCulture);
            await conn.ExecuteAsync(new CommandDefinition(sql, new { id, name, category_id = categoryId, sku, price }, transaction: tx, cancellationToken: ct));
        }
    }

    private static async Task ImportOrdersBinaryAsync(NpgsqlConnection conn, string path, CancellationToken ct)
    {
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
        });
        await csv.ReadAsync();
        csv.ReadHeader();

        await using var importer = await conn.BeginBinaryImportAsync("""
            COPY orders (id, supplier_id, product_id, quantity, unit_price, total_price, status, priority, created_at, updated_at, warehouse, notes, version, flagged)
            FROM STDIN (FORMAT BINARY)
            """, ct);

        while (await csv.ReadAsync())
        {
            var id = csv.GetField(0)?.Trim();
            if (string.IsNullOrEmpty(id)) continue;
            var supplierId = csv.GetField(1)!;
            var productId = csv.GetField(2)!;
            var quantity = int.Parse(csv.GetField(3)!, CultureInfo.InvariantCulture);
            var unitPrice = decimal.Parse(csv.GetField(4)!, CultureInfo.InvariantCulture);
            var totalPrice = decimal.Parse(csv.GetField(5)!, CultureInfo.InvariantCulture);
            var status = csv.GetField(6)!;
            var priority = csv.GetField(7)!;
            var createdAt = DateTime.SpecifyKind(
                DateTime.Parse(csv.GetField(8)!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                DateTimeKind.Utc);
            var updatedAt = DateTime.SpecifyKind(
                DateTime.Parse(csv.GetField(9)!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                DateTimeKind.Utc);
            var warehouseRaw = csv.GetField(10);
            string? warehouse = string.IsNullOrWhiteSpace(warehouseRaw) ? null : warehouseRaw.Trim();
            var notes = csv.GetField(11);

            importer.StartRow();
            await importer.WriteAsync(id, NpgsqlDbType.Text, ct);
            await importer.WriteAsync(supplierId, NpgsqlDbType.Text, ct);
            await importer.WriteAsync(productId, NpgsqlDbType.Text, ct);
            await importer.WriteAsync(quantity, NpgsqlDbType.Integer, ct);
            await importer.WriteAsync(unitPrice, NpgsqlDbType.Numeric, ct);
            await importer.WriteAsync(totalPrice, NpgsqlDbType.Numeric, ct);
            await importer.WriteAsync(status, NpgsqlDbType.Text, ct);
            await importer.WriteAsync(priority, NpgsqlDbType.Text, ct);
            await importer.WriteAsync(createdAt, NpgsqlDbType.TimestampTz, ct);
            await importer.WriteAsync(updatedAt, NpgsqlDbType.TimestampTz, ct);
            if (warehouse is null)
                await importer.WriteNullAsync(ct);
            else
                await importer.WriteAsync(warehouse, NpgsqlDbType.Text, ct);
            if (notes is null)
                await importer.WriteNullAsync(ct);
            else
                await importer.WriteAsync(notes, NpgsqlDbType.Text, ct);
            await importer.WriteAsync(0, NpgsqlDbType.Integer, ct);
            await importer.WriteAsync(false, NpgsqlDbType.Boolean, ct);
        }

        await importer.CompleteAsync(ct);
    }
}

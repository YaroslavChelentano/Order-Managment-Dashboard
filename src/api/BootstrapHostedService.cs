using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace OrderManagement;

/// <summary>Applies schema and imports CSV when the database has no orders (or when <c>Bootstrap:ForceCsvImport</c> is true).</summary>
public sealed class BootstrapHostedService(
    NpgsqlDataSource dataSource,
    IWebHostEnvironment env,
    IConfiguration configuration,
    OrderDb orderDb,
    ILogger<BootstrapHostedService> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var schemaPath = Path.Combine(env.ContentRootPath, "Sql", "schema.sql");
        if (!File.Exists(schemaPath))
        {
            log.LogWarning("Schema file missing at {Path}", schemaPath);
            return;
        }

        var schemaSql = await File.ReadAllTextAsync(schemaPath, cancellationToken);
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition(schemaSql, cancellationToken: cancellationToken));

        var count = await conn.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM orders", cancellationToken: cancellationToken));
        var forceCsvImport = configuration.GetValue("Bootstrap:ForceCsvImport", false);
        if (count > 0 && !forceCsvImport)
        {
            log.LogInformation("Database already contains {Count} orders; skipping CSV import.", count);
            await WarmDefaultOrderListAsync(cancellationToken);
            return;
        }

        if (count > 0 && forceCsvImport)
            log.LogInformation("Bootstrap:ForceCsvImport is enabled; reloading CSV baseline.");

        var dataDir = DataPaths.ResolveDataDirectory(env);
        if (!Directory.Exists(dataDir))
        {
            log.LogWarning("Data directory not found: {Dir}", dataDir);
            return;
        }

        log.LogInformation("Importing CSV from {Dir}", dataDir);
        await conn.ExecuteAsync(new CommandDefinition(
            "TRUNCATE orders, products, suppliers, categories CASCADE",
            cancellationToken: cancellationToken));
        await CsvImporter.ImportAllAsync(conn, dataDir, cancellationToken);
        log.LogInformation("CSV import complete.");
        await conn.ExecuteAsync(new CommandDefinition(
            "ANALYZE categories; ANALYZE suppliers; ANALYZE products; ANALYZE orders;",
            cancellationToken: cancellationToken));
        await WarmDefaultOrderListAsync(cancellationToken);
    }

    /// <summary>Runs the default unfiltered order list once so the hot path is JIT/plan-warmed before the first HTTP request (performance test p95).</summary>
    private async Task WarmDefaultOrderListAsync(CancellationToken cancellationToken)
    {
        try
        {
            await orderDb.ListOrdersAsync(
                null, null, null, null, null, null, null, null,
                "created_at", "desc", 20, 0, cancellationToken);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Startup warmup query for default order list failed (non-fatal).");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

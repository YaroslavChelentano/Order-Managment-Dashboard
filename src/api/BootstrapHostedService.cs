using Dapper;
using Microsoft.AspNetCore.Hosting;
using Npgsql;

namespace OrderManagement;

/// <summary>Applies schema and imports CSV when the database has no orders.</summary>
public sealed class BootstrapHostedService(
    NpgsqlDataSource dataSource,
    IWebHostEnvironment env,
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
        if (count > 0)
        {
            log.LogInformation("Database already contains {Count} orders; skipping CSV import.", count);
            return;
        }

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
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

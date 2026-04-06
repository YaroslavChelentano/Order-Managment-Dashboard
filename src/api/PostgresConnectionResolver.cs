using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace OrderManagement;

/// <summary>
/// Resolves a working PostgreSQL connection string using configuration priority and safe fallbacks
/// (alternate hosts, usernames, SSL mode). Does not brute-force passwords.
/// </summary>
public static class PostgresConnectionResolver
{
    private const string DefaultFallback =
        "Host=localhost;Port=5433;Database=order_ops;Username=postgres;Password=postgres";

    public static async Task<string> ResolveAsync(
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var (primaryCs, source) = ResolvePrimaryConnectionString(configuration);
        NpgsqlConnectionStringBuilder primary;
        try
        {
            primary = new NpgsqlConnectionStringBuilder(primaryCs);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Invalid PostgreSQL connection string (check DATABASE_URL or ConnectionStrings:DefaultConnection).", ex);
        }

        if (string.IsNullOrWhiteSpace(primary.Database))
            primary.Database = "order_ops";

        LogStartupDiagnostics(logger, primary, source);

        var strategies = BuildPasswordStrategies(primary).ToList();
        var failures = new List<string>();
        var variantCount = 0;

        foreach (var (strategyLabel, strategyBuilder) in strategies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogInformation(
                "PostgreSQL: trying password strategy \"{Strategy}\" (password value not logged).",
                strategyLabel);

            var variants = BuildConnectionVariants(strategyBuilder);
            variantCount += variants.Count;

            foreach (var (label, connectionString) in variants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = await TryConnectAsync(connectionString, cancellationToken);
                if (outcome.Success)
                {
                    logger.LogInformation(
                        "PostgreSQL: connected using strategy \"{Strategy}\", variant \"{Variant}\" (config source: {ConfigSource}).",
                        strategyLabel,
                        label,
                        source);
                    return connectionString;
                }

                if (outcome is { Exception: PostgresException { SqlState: PostgresErrorCodes.InvalidCatalogName } })
                {
                    var targetDb = new NpgsqlConnectionStringBuilder(connectionString).Database ?? "order_ops";
                    var created = await TryCreateDatabaseIfMissingAsync(connectionString, targetDb, logger, cancellationToken);
                    if (created)
                    {
                        var retry = await TryConnectAsync(connectionString, cancellationToken);
                        if (retry.Success)
                        {
                            logger.LogInformation(
                                "PostgreSQL: connected after CREATE DATABASE (strategy \"{Strategy}\", variant \"{Variant}\").",
                                strategyLabel,
                                label);
                            return connectionString;
                        }
                    }
                }

                failures.Add($"[{strategyLabel}] {label}: {outcome.Message}");
                logger.LogDebug(
                    "PostgreSQL variant failed: [{Strategy}] {Variant} — {Reason}",
                    strategyLabel,
                    label,
                    outcome.Message);
            }
        }

        logger.LogError(
            "PostgreSQL: all connection attempts failed. Config source was {Source}. Tried {StrategyCount} password strategies, {VariantCount} connection variants.",
            source,
            strategies.Count,
            variantCount);

        foreach (var f in failures)
            logger.LogWarning("{Failure}", f);

        throw new InvalidOperationException(
            "Could not connect to PostgreSQL after exhausting configuration sources and safe fallbacks. " +
            "See logs above for each attempt (passwords are not logged).");
    }

    public static (string ConnectionString, string Source) ResolvePrimaryConnectionString(IConfiguration configuration)
    {
        var dbUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (!string.IsNullOrWhiteSpace(dbUrl))
            return (dbUrl.Trim(), "DATABASE_URL");

        var merged = configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrWhiteSpace(merged))
        {
            var hasEnvOverride =
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection"));
            var src = hasEnvOverride
                ? "ConnectionStrings__DefaultConnection (environment variable)"
                : "appsettings.json: ConnectionStrings:DefaultConnection";
            return (merged.Trim(), src);
        }

        return (DefaultFallback, "built-in fallback (no DATABASE_URL and no ConnectionStrings:DefaultConnection)");
    }

    /// <summary>
    /// Ordered password sources: configuration first, then common env vars, then empty password on local hosts only (trust).
    /// Does not iterate arbitrary password guesses.
    /// </summary>
    private static IEnumerable<(string Label, NpgsqlConnectionStringBuilder Builder)> BuildPasswordStrategies(
        NpgsqlConnectionStringBuilder primary)
    {
        yield return ("from configuration", new NpgsqlConnectionStringBuilder(primary.ConnectionString));

        var pgUser = Environment.GetEnvironmentVariable("PGUSER");
        if (!string.IsNullOrEmpty(pgUser)
            && !pgUser.Equals(primary.Username, StringComparison.Ordinal))
        {
            yield return (
                "PGUSER environment variable (username only, password from configuration)",
                new NpgsqlConnectionStringBuilder(primary.ConnectionString) { Username = pgUser });
        }

        var pgPass = Environment.GetEnvironmentVariable("PGPASSWORD");
        if (!string.IsNullOrEmpty(pgPass))
            yield return ("PGPASSWORD environment variable", new NpgsqlConnectionStringBuilder(primary.ConnectionString) { Password = pgPass });

        if (!string.IsNullOrEmpty(pgUser) && !string.IsNullOrEmpty(pgPass))
        {
            yield return (
                "PGUSER and PGPASSWORD environment variables",
                new NpgsqlConnectionStringBuilder(primary.ConnectionString) { Username = pgUser, Password = pgPass });
        }

        var postPass = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD");
        if (!string.IsNullOrEmpty(postPass) && postPass != pgPass)
            yield return ("POSTGRES_PASSWORD environment variable", new NpgsqlConnectionStringBuilder(primary.ConnectionString) { Password = postPass });

        var host = string.IsNullOrWhiteSpace(primary.Host) ? "localhost" : primary.Host!;
        if (IsLocalHost(host))
            yield return ("empty password (local hosts only)", new NpgsqlConnectionStringBuilder(primary.ConnectionString) { Password = "" });
    }

    private static void LogStartupDiagnostics(ILogger logger, NpgsqlConnectionStringBuilder b, string configSource)
    {
        logger.LogInformation(
            "PostgreSQL startup diagnostics: host={Host}, port={Port}, database={Database}, username={Username}, " +
            "configSource={ConfigSource} (password not logged).",
            b.Host ?? "(default)",
            b.Port == 0 ? 5432 : b.Port,
            b.Database ?? "(unspecified)",
            b.Username ?? "(unspecified)",
            configSource);
    }

    private static List<(string Label, string ConnectionString)> BuildConnectionVariants(NpgsqlConnectionStringBuilder template)
    {
        var results = new List<(string Label, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string label, NpgsqlConnectionStringBuilder b)
        {
            var s = b.ConnectionString;
            if (seen.Add(s))
                results.Add((label, s));
        }

        foreach (var host in ExpandHosts(template.Host))
        foreach (var port in ExpandPorts(template.Port))
        foreach (var user in ExpandUsernames(template.Username))
        foreach (var ssl in ExpandSslModes(host, template.SslMode))
        {
            var b = new NpgsqlConnectionStringBuilder(template.ConnectionString)
            {
                Host = host,
                Port = port,
                Username = user,
                Database = template.Database,
                SslMode = ssl,
            };
            Add($"host={host};port={port};user={user};ssl={ssl}", b);
        }

        return results;
    }

    private static IEnumerable<string> ExpandHosts(string? host)
    {
        var h = string.IsNullOrWhiteSpace(host) ? "localhost" : host.Trim();
        var list = new List<string>();
        void Add(string x)
        {
            if (!list.Contains(x, StringComparer.OrdinalIgnoreCase))
                list.Add(x);
        }

        Add(h);

        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            Add("127.0.0.1");
        else if (h == "127.0.0.1")
            Add("localhost");
        else if (h.Equals("postgres", StringComparison.OrdinalIgnoreCase)
                 || h.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase))
        {
            Add("localhost");
            Add("127.0.0.1");
        }

        return list;
    }

    /// <summary>Try default port and 5433 (common alternate when 5432 is already taken by another Postgres).</summary>
    private static IEnumerable<int> ExpandPorts(int port)
    {
        if (port is 0 or 5432)
        {
            yield return 5432;
            yield return 5433;
        }
        else
        {
            yield return port;
            if (port != 5432)
                yield return 5432;
        }
    }

    private static IEnumerable<string> ExpandUsernames(string? username)
    {
        var u = string.IsNullOrWhiteSpace(username) ? "postgres" : username.Trim();
        if (!u.Equals("postgres", StringComparison.Ordinal))
        {
            yield return u;
            yield return "postgres";
        }
        else
        {
            yield return "postgres";
        }
    }

    private static IEnumerable<SslMode> ExpandSslModes(string host, SslMode configured)
    {
        yield return configured;
        if (IsLocalHost(host) && configured != SslMode.Disable)
            yield return SslMode.Disable;
    }

    private static bool IsLocalHost(string host) =>
        host is "localhost" or "127.0.0.1" or "::1"
        || host.Equals("postgres", StringComparison.OrdinalIgnoreCase)
        || host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase);

    private static async Task<ConnectOutcome> TryConnectAsync(string connectionString, CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            return new ConnectOutcome(true, null, "");
        }
        catch (Exception ex)
        {
            return new ConnectOutcome(false, ex, DescribeException(ex));
        }
    }

    private static string DescribeException(Exception ex)
    {
        return ex switch
        {
            PostgresException pe => $"{pe.SqlState}: {pe.MessageText}",
            SocketException se => $"socket {se.SocketErrorCode}: {se.Message}",
            NpgsqlException npe => npe.Message,
            _ => ex.Message,
        };
    }

    private static async Task<bool> TryCreateDatabaseIfMissingAsync(
        string targetConnectionString,
        string databaseName,
        ILogger logger,
        CancellationToken ct)
    {
        if (!IsSafeDbName(databaseName))
        {
            logger.LogWarning("PostgreSQL: refusing to CREATE DATABASE with unsafe name.");
            return false;
        }

        var admin = new NpgsqlConnectionStringBuilder(targetConnectionString) { Database = "postgres" };
        try
        {
            await using var conn = new NpgsqlConnection(admin.ConnectionString);
            await conn.OpenAsync(ct);
            await using var check = new NpgsqlCommand(
                "SELECT 1 FROM pg_database WHERE datname = @n",
                conn);
            check.Parameters.AddWithValue("n", databaseName);
            var exists = await check.ExecuteScalarAsync(ct) is not null;
            if (exists)
                return false;

            await using var create =
                new NpgsqlCommand($"CREATE DATABASE \"{databaseName.Replace("\"", "\"\"")}\"", conn);
            await create.ExecuteNonQueryAsync(ct);
            logger.LogInformation("PostgreSQL: created missing database \"{Database}\".", databaseName);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PostgreSQL: CREATE DATABASE \"{Database}\" failed.", databaseName);
            return false;
        }
    }

    private static bool IsSafeDbName(string name) =>
        name.Length is > 0 and <= 63 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private sealed record ConnectOutcome(bool Success, Exception? Exception, string Message);

    private static class PostgresErrorCodes
    {
        public const string InvalidCatalogName = "3D000";
    }
}

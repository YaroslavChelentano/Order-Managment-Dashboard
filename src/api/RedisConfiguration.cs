using Microsoft.Extensions.Configuration;

namespace OrderManagement;

public static class RedisConfiguration
{
    private const string DefaultConnection = "localhost:6379";

    /// <summary>Resolves Redis connection: <c>Redis</c> config key, then <c>REDIS_URL</c>, then localhost default.</summary>
    public static string ResolveConnectionString(IConfiguration configuration)
    {
        var fromConfig = configuration["Redis"];
        if (!string.IsNullOrWhiteSpace(fromConfig))
            return fromConfig;

        var env = Environment.GetEnvironmentVariable("REDIS_URL");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Replace("redis://", "", StringComparison.OrdinalIgnoreCase);

        return DefaultConnection;
    }
}

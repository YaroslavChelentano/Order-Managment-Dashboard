namespace OrderManagement;

public static class DataPaths
{
    /// <summary>Resolves repo <c>data/</c> from API project directory (<c>src/api</c> → repo root).</summary>
    public static string ResolveDataDirectory(IWebHostEnvironment env)
    {
        var dir = Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "..", "data"));
        return dir;
    }
}

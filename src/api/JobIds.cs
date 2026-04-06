namespace OrderManagement;

public static class JobIds
{
    public static string CreateNew() => "job_" + Guid.NewGuid().ToString("N")[..12];
}

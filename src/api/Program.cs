using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Npgsql;
using OrderManagement;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
              ?? Environment.GetEnvironmentVariable("DATABASE_URL");
if (string.IsNullOrEmpty(connStr))
    connStr = "Host=localhost;Port=5432;Database=order_ops;Username=postgres;Password=postgres";

builder.Services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connStr).Build());
builder.Services.AddSingleton<OrderDb>();
builder.Services.AddSingleton<EventBroadcaster>();
builder.Services.AddSingleton<RedisJobStore>();

var redisConn = builder.Configuration["Redis"]
                ?? Environment.GetEnvironmentVariable("REDIS_URL")?.Replace("redis://", "", StringComparison.OrdinalIgnoreCase)
                ?? "localhost:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConn));

builder.Services.AddHostedService<BootstrapHostedService>();
builder.Services.AddHostedService<BulkJobWorker>();

var app = builder.Build();

app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();

var orderDb = app.Services.GetRequiredService<OrderDb>();
var events = app.Services.GetRequiredService<EventBroadcaster>();
var jobStore = app.Services.GetRequiredService<RedisJobStore>();

app.Map("/api/events", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var supplierFilter = ctx.Request.Query["supplier_id"].FirstOrDefault();
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var id = events.Subscribe(ws, supplierFilter);
    try
    {
        var buf = new byte[256];
        while (ws.State == WebSocketState.Open)
        {
            var r = await ws.ReceiveAsync(buf, ctx.RequestAborted);
            if (r.MessageType == WebSocketMessageType.Close) break;
        }
    }
    finally
    {
        events.Unsubscribe(id);
    }
});

app.MapGet("/api/orders/stats", async (CancellationToken ct) =>
    Results.Json(await orderDb.GetDashboardStatsAsync(ct), AppJson.Options));

app.MapGet("/api/orders/anomalies", async (CancellationToken ct) =>
    Results.Json(new { data = await orderDb.GetAnomaliesAsync(ct) }, AppJson.Options));

app.MapGet("/api/orders", async (HttpRequest req, CancellationToken ct) =>
{
    var limitRaw = req.Query["limit"].FirstOrDefault();
    var offsetRaw = req.Query["offset"].FirstOrDefault();
    var limit = 20;
    var offset = 0;
    if (int.TryParse(limitRaw, out var l))
    {
        if (l < 0) limit = 100;
        else limit = Math.Clamp(l, 1, 10_000);
    }

    if (int.TryParse(offsetRaw, out var o) && o >= 0) offset = o;

    var statusQ = req.Query["status"].FirstOrDefault();
    string[]? statuses = null;
    if (!string.IsNullOrEmpty(statusQ))
        statuses = statusQ.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    var priority = req.Query["priority"].FirstOrDefault();
    var supplierId = req.Query["supplier_id"].FirstOrDefault();
    var warehouse = req.Query["warehouse"].FirstOrDefault();
    DateTime? dateFrom = null;
    DateTime? dateTo = null;
    if (DateTime.TryParse(req.Query["date_from"].FirstOrDefault(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var df))
        dateFrom = df;
    if (DateTime.TryParse(req.Query["date_to"].FirstOrDefault(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
        dateTo = dt.Date.AddDays(1).AddTicks(-1);

    decimal? minTotal = null;
    if (decimal.TryParse(req.Query["min_total"].FirstOrDefault(), CultureInfo.InvariantCulture, out var mt))
        minTotal = mt;

    var search = req.Query["search"].FirstOrDefault();
    var sort = req.Query["sort"].FirstOrDefault() ?? "created_at";
    var order = req.Query["order"].FirstOrDefault() ?? "desc";

    (List<Dictionary<string, object?>> rows, int total) = await orderDb.ListOrdersAsync(
        statuses, priority, supplierId, warehouse, dateFrom, dateTo, minTotal, search,
        sort, order, limit, offset, ct);

    return Results.Json(new { data = rows, total, limit, offset }, AppJson.Options);
});

app.MapGet("/api/orders/{id}", async (string id, CancellationToken ct) =>
{
    var row = await orderDb.GetOrderByIdAsync(id, ct);
    return row is null
        ? ApiErrors.JsonError(404, "Order not found", "NOT_FOUND")
        : Results.Json(row, AppJson.Options);
});

app.MapPatch("/api/orders/{id}", async (string id, HttpRequest req, CancellationToken ct) =>
{
    var body = await JsonSerializer.DeserializeAsync<PatchOrderBody>(req.Body, AppJson.Options, ct);
    (bool ok, bool conflict, bool notFound, bool badRequest, Dictionary<string, object?>? dict, string? oldStatus, string? supplierId) =
        await orderDb.PatchOrderAsync(id, body?.Status, body?.Notes, ct);

    if (notFound) return ApiErrors.JsonError(404, "Order not found", "NOT_FOUND");
    if (badRequest) return ApiErrors.JsonError(400, "Invalid status", "VALIDATION_ERROR");
    if (conflict) return ApiErrors.JsonError(409, "Order cannot be modified", "CONFLICT");
    if (!ok || dict is null) return ApiErrors.JsonError(500, "Update failed", "ERROR");

    if (body?.Status is not null && oldStatus is not null && supplierId is not null)
    {
        dict.TryGetValue("updated_at", out var updatedAt);
        await events.BroadcastOrderUpdatedAsync(supplierId, new
        {
            id,
            old_status = oldStatus,
            new_status = body.Status,
            updated_at = updatedAt,
        }, ct);
    }

    return Results.Json(dict, AppJson.Options);
});

async Task<IResult> EnqueueBulkAsync(HttpRequest req, RedisJobStore store, CancellationToken ct)
{
    using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
    var root = doc.RootElement;
    List<string> ids = [];
    if (root.TryGetProperty("order_ids", out var snake) && snake.ValueKind == JsonValueKind.Array)
        foreach (var e in snake.EnumerateArray())
        {
            var s = e.GetString();
            if (s is not null) ids.Add(s);
        }
    else if (root.TryGetProperty("orderIds", out var camel) && camel.ValueKind == JsonValueKind.Array)
        foreach (var e in camel.EnumerateArray())
        {
            var s = e.GetString();
            if (s is not null) ids.Add(s);
        }

    if (ids.Count > 10_000)
        return ApiErrors.JsonError(400, "Too many order ids", "VALIDATION_ERROR");
    if (ids.Count == 0)
        return ApiErrors.JsonError(400, "orderIds required", "VALIDATION_ERROR");

    var action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
    var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;

    if (action is null || action is not ("approve" or "reject" or "flag"))
        return ApiErrors.JsonError(400, "Invalid action", "VALIDATION_ERROR");

    var jobId = "job_" + Guid.NewGuid().ToString("N")[..12];
    await store.EnqueueAsync(jobId, action, ids, reason, ct);
    return Results.Json(new Dictionary<string, string> { ["jobId"] = jobId, ["job_id"] = jobId }, AppJson.Options, statusCode: 202);
}

app.MapPost("/api/orders/bulk-action", EnqueueBulkAsync);
app.MapPost("/api/orders/bulk-actions", EnqueueBulkAsync);
app.MapPost("/api/orders/bulk", EnqueueBulkAsync);

app.MapGet("/api/jobs/{id}", async (string id, CancellationToken ct) =>
{
    var p = await jobStore.GetProgressAsync(id, ct);
    if (p.Total == 0 && p.Status == "failed")
        return ApiErrors.JsonError(404, "Job not found", "NOT_FOUND");

    return Results.Json(new
    {
        status = p.Status,
        progress = new { total = p.Total, completed = p.Completed, failed = p.Failed },
    }, AppJson.Options);
});

app.MapGet("/api/suppliers", async (HttpRequest req, CancellationToken ct) =>
{
    var limit = int.TryParse(req.Query["limit"].FirstOrDefault(), out var l) ? Math.Clamp(l, 1, 10_000) : 20;
    var offset = int.TryParse(req.Query["offset"].FirstOrDefault(), out var o) && o >= 0 ? o : 0;
    (List<Dictionary<string, object?>> rows, int total) = await orderDb.ListSuppliersAsync(limit, offset, ct);
    return Results.Json(new { data = rows, total, limit, offset }, AppJson.Options);
});

app.MapGet("/api/suppliers/{id}/performance", async (string id, CancellationToken ct) =>
{
    if (await orderDb.GetSupplierByIdAsync(id, ct) is null)
        return ApiErrors.JsonError(404, "Supplier not found", "NOT_FOUND");
    var perf = await orderDb.GetSupplierPerformanceAsync(id, ct);
    return Results.Json(perf, AppJson.Options);
});

app.MapGet("/api/suppliers/{id}", async (string id, CancellationToken ct) =>
{
    var row = await orderDb.GetSupplierByIdAsync(id, ct);
    return row is null
        ? ApiErrors.JsonError(404, "Supplier not found", "NOT_FOUND")
        : Results.Json(row, AppJson.Options);
});

app.MapGet("/api/products", async (HttpRequest req, CancellationToken ct) =>
{
    var limit = int.TryParse(req.Query["limit"].FirstOrDefault(), out var l) ? Math.Clamp(l, 1, 10_000) : 20;
    var offset = int.TryParse(req.Query["offset"].FirstOrDefault(), out var o) && o >= 0 ? o : 0;
    var category = req.Query["category"].FirstOrDefault();
    (List<Dictionary<string, object?>> rows, int total) = await orderDb.ListProductsAsync(limit, offset, category, ct);
    return Results.Json(new { data = rows, total, limit, offset }, AppJson.Options);
});

app.MapFallback(async (HttpContext ctx) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.WriteAsJsonAsync(new { error = "Not found", code = "NOT_FOUND" }, AppJson.Options);
        return;
    }

    var env = ctx.RequestServices.GetRequiredService<IWebHostEnvironment>();
    var path = Path.Combine(env.WebRootPath ?? "", "index.html");
    if (!File.Exists(path))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    ctx.Response.ContentType = "text/html";
    await ctx.Response.SendFileAsync(path);
});

app.Run();

internal sealed record PatchOrderBody(string? Status, string? Notes);

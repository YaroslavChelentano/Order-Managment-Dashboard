using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using OrderManagement;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

using var dbLogFactory = LoggerFactory.Create(lb =>
{
    lb.AddConsole();
    lb.SetMinimumLevel(LogLevel.Information);
});
var pgLogger = dbLogFactory.CreateLogger("PostgresConnection");

var connStr = await PostgresConnectionResolver.ResolveAsync(builder.Configuration, pgLogger, CancellationToken.None);

builder.Services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connStr).Build());
builder.Services.AddSingleton<OrderDb>();
builder.Services.AddSingleton<EventBroadcaster>();

var redisConn = RedisConfiguration.ResolveConnectionString(builder.Configuration);
builder.Services.AddSingleton<IJobStore>(_ =>
{
    var options = ConfigurationOptions.Parse(redisConn);
    options.AbortOnConnectFail = true;
    options.ConnectTimeout = 2000;
    try
    {
        var mux = ConnectionMultiplexer.Connect(options);
        mux.GetDatabase().Ping();
        return new RedisJobStore(mux);
    }
    catch
    {
        return new MemoryJobStore();
    }
});

builder.Services.AddHostedService<BootstrapHostedService>();
builder.Services.AddHostedService<BulkJobWorker>();

var app = builder.Build();

app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();

var orderDb = app.Services.GetRequiredService<OrderDb>();
var events = app.Services.GetRequiredService<EventBroadcaster>();
var jobStore = app.Services.GetRequiredService<IJobStore>();

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
    var q = OrderListQueryParser.Parse(req);
    (List<Dictionary<string, object?>> rows, int total) = await orderDb.ListOrdersAsync(
        q.Statuses, q.Priority, q.SupplierId, q.Warehouse, q.DateFrom, q.DateTo, q.MinTotal, q.Search,
        q.Sort, q.Order, q.Limit, q.Offset, ct);

    return Results.Json(new { data = rows, total, limit = q.Limit, offset = q.Offset }, AppJson.Options);
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

async Task<IResult> EnqueueBulkAsync(HttpRequest req, IJobStore store, CancellationToken ct)
{
    using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
    var root = doc.RootElement;
    var ids = BulkJson.ReadOrderIds(root);

    if (ids.Count > ApiLimits.BulkMaxOrderIds)
        return ApiErrors.JsonError(400, "Too many order ids", "VALIDATION_ERROR");
    if (ids.Count == 0)
        return ApiErrors.JsonError(400, "orderIds required", "VALIDATION_ERROR");

    var action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
    var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;

    if (action is null || action is not (BulkActions.Approve or BulkActions.Reject or BulkActions.Flag))
        return ApiErrors.JsonError(400, "Invalid action", "VALIDATION_ERROR");

    var jobId = JobIds.CreateNew();
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
    var (limit, offset) = PaginationQuery.ParseStandard(req);
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
    var (limit, offset) = PaginationQuery.ParseStandard(req);
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

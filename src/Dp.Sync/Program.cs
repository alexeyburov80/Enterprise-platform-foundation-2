// ============================================================================
// dp-sync — интеграционный воркер Data Platform (composition root).
//
// Отдельный сервис, который содержит ВЕСЬ интеграционный контур:
//   * AdapterSwitcher — коннекторы 1С/Битрикс24 (real) и переключение на наши
//     сервисы-заместители (stub/fallback), режимы auto | real | stub;
//   * IngestionService — pull-синхронизация в logical store'ы (ручной запуск
//     из dp-api POST /admin/sync/{code} и/или расписание Sync:AutoIntervalSeconds);
//   * OutboxPublisher — доставка dp.data.changed.v1 из dp.outbox в RabbitMQ
//     (topic dp.data); брокер недоступен — события копятся, данные не теряются.
//
// Внутреннее API (сеть стенда, управляется dp-api по X-Api-Key):
//   GET  /health                 — состояние воркера (без ключа, для probe)
//   GET  /adapters               — режимы/пробы/fallback по каждой системе
//   POST /adapters/{code}/mode   — горячее переключение auto|real|stub
//   POST /run/{code}             — запуск синхронизации «сейчас»
//   POST /sd/reload              — перечитать SD-документы
// Схему БД воркер не мигрирует: ждёт dp.migration_journal (её применяет dp-api).
// ============================================================================

using System.Text.Json.Nodes;
using Dp.Core;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connString = builder.Configuration.GetConnectionString("Db")
    ?? throw new InvalidOperationException("ConnectionStrings:Db не задана");
builder.Services.AddSingleton(sp => NpgsqlDataSource.Create(connString));
builder.Services.AddSingleton(sp =>
    new SdRegistry(sp.GetRequiredService<ILogger<SdRegistry>>(),
        builder.Configuration["Sd:Path"] ?? "deploy/sd"));
builder.Services.AddSingleton<OutboxWriter>();
builder.Services.AddSingleton<StoreService>();
builder.Services.AddSingleton<AdapterSwitcher>();
builder.Services.AddSingleton<IngestionService>();
builder.Services.AddSingleton<OutboxPublisher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboxPublisher>());
builder.Services.AddHostedService<SyncScheduler>();

var app = builder.Build();

// --- мидлварь ----------------------------------------------------------------
// X-Api-Key для управляющих вызовов (dp-api ходит с тем же ключом).
var apiKey = app.Configuration["Auth:ApiKey"] ?? "dev-key";
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (path.StartsWith("/health") || path == "/")
    {
        await next();
        return;
    }
    var provided = ctx.Request.Headers["X-Api-Key"].FirstOrDefault();
    if (provided != apiKey)
    {
        await Problems.WriteAsync(ctx, 401, DpCodes.AuthTokenInvalid,
            "Заголовок X-Api-Key отсутствует или неверен");
        return;
    }
    await next();
});

app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (DpException dp)
    {
        await Problems.WriteAsync(ctx, dp.Status, dp.Code, dp.Detail);
    }
    catch (PostgresException pg)
    {
        var mapped = Problems.FromPostgres(pg);
        await Problems.WriteAsync(ctx, mapped.Status, mapped.Code, mapped.Detail);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Необработанная ошибка на {Path}", ctx.Request.Path);
        await Problems.WriteAsync(ctx, 500, DpCodes.Internal, ex.Message);
    }
});

var startedAt = DateTimeOffset.UtcNow;
var dbSource = app.Services.GetRequiredService<NpgsqlDataSource>();
var sd = app.Services.GetRequiredService<SdRegistry>();
var switcher = app.Services.GetRequiredService<AdapterSwitcher>();
var ingestion = app.Services.GetRequiredService<IngestionService>();
var publisher = app.Services.GetRequiredService<OutboxPublisher>();

// --- готовность схемы ----------------------------------------------------------
// Миграции применяет dp-api; воркер ждёт dp.migration_journal перед записью.
var schemaReady = false;
async Task<bool> EnsureSchemaAsync(CancellationToken ct)
{
    if (schemaReady) return true;
    await using var conn = await dbSource.OpenConnectionAsync(ct);
    await using var cmd = new NpgsqlCommand(
        "SELECT to_regclass('dp.migration_journal') IS NOT NULL", conn);
    var ready = (bool)(await cmd.ExecuteScalarAsync(ct))!;
    schemaReady = ready;
    return ready;
}

app.MapGet("/health", () => Results.Json(new JsonObject
{
    ["service"] = "dp-sync",
    ["status"] = "ok",
    ["uptimeSec"] = (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds,
    ["schemaReady"] = schemaReady,
    ["rabbitmq"] = new JsonObject { ["connected"] = publisher.IsConnected, ["exchange"] = builder.Configuration["Rabbit:Exchange"] ?? "dp.data" },
    ["sdStores"] = sd.Stores.Count,
    ["adapters"] = new JsonArray(switcher.ListAll().Select(a => (JsonNode?)new JsonObject
    {
        ["code"] = a.Code,
        ["mode"] = a.Mode,
        ["modeSource"] = a.ModeSource
    }).ToArray())
}));

app.MapGet("/adapters", async () =>
{
    // Свежие probe реальных систем — админ видит актуальное состояние,
    // а не результат последней синхронизации.
    await switcher.ProbeAllAsync();
    return Results.Json(new JsonObject
    {
        ["adapters"] = new JsonArray(switcher.ListAll().Select(a => (JsonNode?)new JsonObject
        {
            ["code"] = a.Code,
            ["displayName"] = a.DisplayName,
            ["mode"] = a.Mode,
            ["modeSource"] = a.ModeSource,
            ["realBaseUrl"] = a.RealBaseUrl,
            ["stubBaseUrl"] = a.StubBaseUrl,
            ["lastRealProbe"] = a.LastRealProbe is null ? null : new JsonObject
            {
                ["ok"] = a.LastRealProbe.Ok,
                ["detail"] = a.LastRealProbe.Detail,
                ["latencyMs"] = a.LastRealProbe.LatencyMs,
                ["checkedAt"] = Rows.Iso(a.LastRealProbe.CheckedAt)
            },
            ["lastFallbackAt"] = a.LastFallbackAt?.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            ["lastFallbackReason"] = a.LastFallbackReason,
            ["endpoint"] = a.LastEndpoint,
            ["lastSyncAt"] = a.LastSyncAt?.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            ["lastSyncTotal"] = a.LastSyncTotal
        }).ToArray())
    });
});

// ГЛАВНАЯ ДЕМО: горячее переключение auto ↔ real ↔ stub без рестарта и изменения кода
app.MapPost("/adapters/{code}/mode", async (string code, HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body) as JsonObject
        ?? throw new DpException(DpCodes.TypeMismatch, 400, "Тело: {\"mode\": \"auto\" | \"stub\" | \"real\"}");
    var mode = (string?)body["mode"] ?? throw new DpException(DpCodes.TypeMismatch, 400, "Отсутствует поле mode");
    var info = switcher.Switch(code, mode);
    return Results.Json(new JsonObject
    {
        ["code"] = info.Code,
        ["displayName"] = info.DisplayName,
        ["mode"] = info.Mode,
        ["modeSource"] = info.ModeSource,
        ["realBaseUrl"] = info.RealBaseUrl,
        ["stubBaseUrl"] = info.StubBaseUrl
    });
});

app.MapPost("/run/{code}", async (string code, CancellationToken ct) =>
{
    if (!await EnsureSchemaAsync(ct))
        throw new DpException(DpCodes.ProviderUnavailable, 503,
            "Схема Data Platform ещё не создана: dp-api не применил миграции (dp.migration_journal отсутствует)");
    return Results.Json(await ingestion.RunAsync(code, ct));
});

app.MapPost("/sd/reload", () =>
{
    try
    {
        sd.Load();
        return Results.Json(new JsonObject { ["reloaded"] = true, ["stores"] = sd.Stores.Count });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new JsonObject { ["reloaded"] = false, ["errors"] = ex.Message }, statusCode: 400);
    }
});

app.MapGet("/", () => Results.Text(
    "Data Platform MVP (dp-sync) — интеграционный воркер.\n" +
    "GET /health, GET /adapters, POST /adapters/{code}/mode, POST /run/{code}, POST /sd/reload\n" +
    "Аутентификация: заголовок X-Api-Key (кроме /health)\n"));

app.Run();

// ============================================================================
// SyncScheduler — опциональный автосинк по расписанию (Sync:AutoIntervalSeconds).
// По умолчанию выключен (0): синхронизация запускается вручную из dp-api
// POST /admin/sync/{code} — детерминированно для демонстраций.
// ============================================================================

public sealed class SyncScheduler(IngestionService ingestion, AdapterSwitcher switcher, IConfiguration cfg,
    ILogger<SyncScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = cfg.GetValue("Sync:AutoIntervalSeconds", 0);
        if (interval <= 0)
        {
            logger.LogInformation("SyncScheduler: автосинк отключён (Sync:AutoIntervalSeconds=0) — запуск вручную через dp-api /admin/sync/{{code}}");
            return;
        }

        logger.LogInformation("SyncScheduler: автосинк каждые {Interval} с по системам: {Systems}",
            interval, string.Join(", ", switcher.ListAll().Select(a => a.Code)));
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(interval), ct); }
            catch (TaskCanceledException) { break; }

            foreach (var a in switcher.ListAll())
            {
                try
                {
                    var result = await ingestion.RunAsync(a.Code, ct);
                    logger.LogInformation("SyncScheduler: {Code} — created={Created}, updated={Updated}",
                        a.Code, result["created"], result["updated"]);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "SyncScheduler: автосинк {Code} не удался (повтор в следующем цикле)", a.Code);
                }
            }
        }
    }
}

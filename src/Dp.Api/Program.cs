// ============================================================================
// dp-api — публичный синхронный контур Data Platform (composition root).
//
// Data Access API v1: 9 операций (табл. 13), базовый префикс /dp/v1;
// admin-контур /admin/*: health, outbox, миграции, SD-reload, а управление
// интеграциями (адаптеры, sync) — прокси к сервису dp-sync (границы сервисов:
// dp-api вообще не знает про 1С/Битрикс, коннекторы живут в dp-sync).
// RFC 9457; static API key (упрощение MVP против OIDC+mTLS+capability token —
// решение A-18, см. README «Отклонения MVP»).
// ============================================================================

using System.Text.Json.Nodes;
using Dp.Core;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// --- инфраструктура ----------------------------------------------------------
var connString = builder.Configuration.GetConnectionString("Db")
    ?? throw new InvalidOperationException("ConnectionStrings:Db не задана");
builder.Services.AddSingleton(sp => NpgsqlDataSource.Create(connString));
builder.Services.AddSingleton(sp =>
    new SdRegistry(sp.GetRequiredService<ILogger<SdRegistry>>(),
        builder.Configuration["Sd:Path"] ?? "deploy/sd"));
builder.Services.AddSingleton<Migrator>();
builder.Services.AddSingleton<OutboxWriter>();
builder.Services.AddSingleton<StoreService>();

// HTTP-клиент к dp-sync (управление адаптерами и синхронизацией — в воркере)
builder.Services.AddSingleton(sp =>
{
    var client = new HttpClient
    {
        BaseAddress = new Uri(builder.Configuration["Worker:BaseUrl"] ?? "http://localhost:5090"),
        Timeout = TimeSpan.FromSeconds(180) // sync может тянуть большие выборки
    };
    client.DefaultRequestHeaders.Add("X-Api-Key", builder.Configuration["Worker:ApiKey"] ?? "dev-key");
    return client;
});

var app = builder.Build();

// --- стартовая миграция (idempotent) ----------------------------------------
var migrator = app.Services.GetRequiredService<Migrator>();
await migrator.EnsureSystemAsync();
if (builder.Configuration.GetValue("Migrations:AutoApply", true))
{
    var results = await migrator.ApplyAllAsync(app.Services.GetRequiredService<SdRegistry>().Stores);
    foreach (var m in results)
        Console.WriteLine($"[migrator] {m.StoreId}: {m.FromVersion} → {m.ToVersion} — {m.Status} ({m.DurationMs} мс)");
}

// --- мидлварь ----------------------------------------------------------------
// Static API key (MVP-упрощение). Отклонение от A-18 задокументировано в README.
var apiKey = app.Configuration["Auth:ApiKey"] ?? "dev-key";
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (path.StartsWith("/dp") || path.StartsWith("/admin"))
    {
        var provided = ctx.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (provided != apiKey)
        {
            await Problems.WriteAsync(ctx, 401, DpCodes.AuthTokenInvalid,
                "Заголовок X-Api-Key отсутствует или неверен (MVP: static API key вместо OIDC + capability token)");
            return;
        }
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

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Data Platform MVP v1"));

var svc = app.Services.GetRequiredService<StoreService>();
var sd = app.Services.GetRequiredService<SdRegistry>();
var worker = app.Services.GetRequiredService<HttpClient>();

string Actor(HttpContext ctx) => "api";
string? IdemKey(HttpContext ctx) => ctx.Request.Headers["Idempotency-Key"].FirstOrDefault();
static string HashOf(JsonNode? node) => DpUtil.Sha256Hex(node?.ToJsonString() ?? "");

long? IfMatch(HttpContext ctx)
{
    var raw = ctx.Request.Headers["If-Match"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(raw)) return null;
    raw = raw.Trim().TrimStart('W', '/').Trim('"');
    return long.TryParse(raw, out var v) ? v : null;
}

// --- прокси к dp-sync ----------------------------------------------------------
// dp-api не содержит ни адаптеров, ни логики синхронизации: интеграционный
// контур целиком живёт в dp-sync (разделение сервисов). Недоступность воркера
// — DP-4002 SYNC_WORKER_UNAVAILABLE (RFC 9457).

async Task ProxyAsync(HttpContext ctx, string path, string? method = null)
{
    try
    {
        using var req = new HttpRequestMessage(new HttpMethod(method ?? ctx.Request.Method), path);
        if (ctx.Request.ContentLength > 0)
        {
            req.Content = new StreamContent(ctx.Request.Body);
            req.Content.Headers.ContentType = new("application/json");
        }
        using var resp = await worker.SendAsync(req, ctx.RequestAborted);
        var body = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
        ctx.Response.StatusCode = (int)resp.StatusCode;
        ctx.Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
        await ctx.Response.WriteAsync(body);
    }
    catch (HttpRequestException ex)
    {
        throw new DpException(DpCodes.SyncWorkerUnavailable, 503,
            $"Сервис dp-sync недоступен ({ex.Message}): управление адаптерами и синхронизация выполняются в dp-sync");
    }
    catch (TaskCanceledException)
    {
        throw new DpException(DpCodes.SyncWorkerUnavailable, 504,
            "Сервис dp-sync не ответил вовремя (тайм-аут прокси dp-api → dp-sync)");
    }
}

// ============================================================================
// Data Access API v1 (9 операций — таблица 13 спецификации)
// ============================================================================

var dp = app.MapGroup("/dp/v1");

dp.MapGet("/stores", () => Results.Json(new
{
    stores = sd.Stores.Select(s => new
    {
        storeId = s.StoreId,
        version = s.Metadata.Version,
        displayName = s.Metadata.DisplayName,
        owner = s.Spec.Owner,
        entities = s.Spec.Entities.Select(e => e.Name).ToList()
    }).ToList()
}));

// GetSchema (канон store'а)
dp.MapGet("/stores/{storeId}/schema", (string storeId, string? version) =>
{
    var store = sd.FindStore(storeId);
    if (!string.IsNullOrEmpty(version) && version != store.Metadata.Version)
        return Results.Json(new JsonObject
        {
            ["code"] = DpCodes.StoreVersionDeprecated,
            ["detail"] = $"Пин версии {version}: live-версия — {store.Metadata.Version}"
        }, statusCode: 410);
    return Results.Json(new JsonObject
    {
        ["storeId"] = store.StoreId,
        ["version"] = store.Metadata.Version,
        ["status"] = "deployed",
        ["displayName"] = store.Metadata.DisplayName,
        ["owner"] = store.Spec.Owner,
        ["tenancy"] = new JsonObject { ["mode"] = store.Spec.Tenancy.Mode },
        ["entities"] = new JsonArray(store.Spec.Entities.Select(e => (JsonNode?)new JsonObject
        {
            ["name"] = e.Name,
            ["table"] = DdlGenerator.EntityTable(store, e),
            ["primaryKey"] = new JsonArray(e.PrimaryKey.Select(x => (JsonNode?)x).ToArray()),
            ["softDelete"] = e.Lifecycle.SoftDelete,
            ["audited"] = e.Lifecycle.Audited,
            ["fields"] = new JsonArray(e.Fields.Select(f => (JsonNode?)new JsonObject
            {
                ["name"] = f.Name,
                ["type"] = f.Type,
                ["required"] = f.Required,
                ["default"] = f.Default,
                ["length"] = f.Length,
                ["precision"] = f.Precision,
                ["scale"] = f.Scale,
                ["values"] = f.Values.Count > 0 ? new JsonArray(f.Values.Select(v => (JsonNode?)v).ToArray()) : null,
                ["refEntity"] = f.RefEntity
            }).ToArray()),
            ["extensions"] = new JsonObject(sd.ExtensionsFor(storeId, e.Name)
                .Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key,
                    new JsonArray(kv.Value.Select(f => (JsonNode?)f.Name).ToArray()))))
        }).ToArray())
    });
});

// GetCapabilities
dp.MapGet("/stores/{storeId}/capabilities", (string storeId) =>
{
    var store = sd.FindStore(storeId);
    return Results.Json(new JsonObject
    {
        ["storeId"] = store.StoreId,
        ["version"] = store.Metadata.Version,
        ["provider"] = "postgresql",
        ["operators"] = new JsonArray(QueryEngine.Operators.Select(o => (JsonNode?)o).ToArray()),
        ["limits"] = new JsonObject
        {
            ["batchMaxOperations"] = 500,
            ["queryLimitMax"] = 1000,
            ["queryLimitDefault"] = 100,
            ["whereDepthMax"] = 3,
            ["inValuesMax"] = 1000
        },
        ["features"] = new JsonObject
        {
            ["idempotency"] = true,
            ["optimisticLock"] = true,
            ["softDelete"] = store.Spec.Entities.Any(e => e.Lifecycle.SoftDelete),
            ["versionPin"] = "current-only",
            ["joinSpec"] = false,
            ["extFields"] = true,
            ["aggregates"] = false,
            ["count"] = false
        }
    });
});

// Get
dp.MapGet("/stores/{storeId}/entities/{entity}/{id}", async (HttpContext ctx, string storeId, string entity, string id,
    string? version, bool? includeDeleted) =>
{
    if (!Guid.TryParse(id, out var key))
        throw new DpException(DpCodes.TypeMismatch, 400, $"Параметр id='{id}' не является uuid");
    var re = svc.Resolve(storeId, entity, version);
    var row = await svc.GetAsync(re, key, includeDeleted ?? false);
    ctx.Response.Headers.ETag = $"\"{row["rowVersion"]?.ToString()}\"";
    return Results.Json(row);
});

// Query
dp.MapPost("/stores/{storeId}/entities/{entity}/query", async (string storeId, string entity, HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body) as JsonObject
        ?? throw new DpException(DpCodes.DslSyntaxInvalid, 400, "Тело запроса должно быть JSON-объектом Query DSL");
    var version = (string?)body["version"];
    var re = svc.Resolve(storeId, entity, version);
    var db = app.Services.GetRequiredService<NpgsqlDataSource>();
    return Results.Json(await QueryEngine.QueryAsync(db, re, body));
});

// Insert
dp.MapPost("/stores/{storeId}/entities/{entity}", async (HttpContext ctx, string storeId, string entity) =>
{
    var payload = await JsonNode.ParseAsync(ctx.Request.Body) as JsonObject
        ?? throw new DpException(DpCodes.TypeMismatch, 400, "Тело запроса должно быть JSON-объектом (полезная нагрузка сущности)");
    var re = svc.Resolve(storeId, entity, (string?)null);
    var outcome = await svc.InsertAsync(re, payload, Actor(ctx), IdemKey(ctx), HashOf(payload));
    ctx.Response.Headers.ETag = $"\"{outcome.Row["rowVersion"]?.ToString()}\"";
    ctx.Response.Headers.Location = $"/dp/v1/stores/{storeId}/entities/{entity}/{(string?)outcome.Row["id"]}";
    if (outcome.Replay) ctx.Response.Headers["X-DP-Idempotent-Replay"] = "true";
    return Results.Json(outcome.Row, statusCode: 201);
});

// Update (PATCH — patch-режим, PUT — replace-режим, табл. 6)
async Task<IResult> UpdateHandler(HttpContext ctx, string storeId, string entity, string id, string mode)
{
    var payload = await JsonNode.ParseAsync(ctx.Request.Body) as JsonObject
        ?? throw new DpException(DpCodes.TypeMismatch, 400, "Тело запроса должно быть JSON-объектом");
    if (!Guid.TryParse(id, out var key))
        throw new DpException(DpCodes.TypeMismatch, 400, $"Параметр id='{id}' не является uuid");
    var re = svc.Resolve(storeId, entity, null);
    var outcome = await svc.UpdateAsync(re, key, payload, mode, Actor(ctx), IfMatch(ctx), IdemKey(ctx), HashOf(payload));
    ctx.Response.Headers.ETag = $"\"{outcome.Row["rowVersion"]?.ToString()}\"";
    if (outcome.Replay) ctx.Response.Headers["X-DP-Idempotent-Replay"] = "true";
    return Results.Json(outcome.Row);
}
dp.MapPatch("/stores/{storeId}/entities/{entity}/{id}", (HttpContext ctx, string storeId, string entity, string id)
    => UpdateHandler(ctx, storeId, entity, id, "patch"));
dp.MapPut("/stores/{storeId}/entities/{entity}/{id}", (HttpContext ctx, string storeId, string entity, string id)
    => UpdateHandler(ctx, storeId, entity, id, "replace"));

// Delete
dp.MapDelete("/stores/{storeId}/entities/{entity}/{id}", async (HttpContext ctx, string storeId, string entity, string id,
    bool? hard, string? version) =>
{
    if (!Guid.TryParse(id, out var key))
        throw new DpException(DpCodes.TypeMismatch, 400, $"Параметр id='{id}' не является uuid");
    var re = svc.Resolve(storeId, entity, version);
    var outcome = await svc.DeleteAsync(re, key, hard ?? false, Actor(ctx), IdemKey(ctx), HashOf(null));
    if (outcome.Replay) ctx.Response.Headers["X-DP-Idempotent-Replay"] = "true";
    return Results.Json(outcome.Row);
});

// Batch / ExecuteTransaction (атомарно в одной транзакции одной физической БД)
async Task<IResult> BatchHandler(HttpContext ctx, string storeId)
{
    var body = await JsonNode.ParseAsync(ctx.Request.Body) as JsonObject
        ?? throw new DpException(DpCodes.DslSyntaxInvalid, 400, "Тело запроса должно быть JSON-объектом {operations: [...]}");
    var operations = body["operations"] as JsonArray
        ?? throw new DpException(DpCodes.DslSyntaxInvalid, 400, "Отсутствует массив operations");
    var result = await svc.BatchAsync(storeId, operations, Actor(ctx), IdemKey(ctx), HashOf(body));
    return Results.Json(result);
}
dp.MapPost("/stores/{storeId}/batch", (HttpContext ctx, string storeId) => BatchHandler(ctx, storeId));
dp.MapPost("/stores/{storeId}/transaction", (HttpContext ctx, string storeId) => BatchHandler(ctx, storeId));

// ============================================================================
// Admin-контур: локальные операции (БД/SD) + прокси интеграций к dp-sync
// ============================================================================

var admin = app.MapGroup("/admin");

admin.MapGet("/health", async () =>
{
    var dbOk = false;
    long outboxPending = -1;
    var dbMs = 0L;
    try
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var db = app.Services.GetRequiredService<NpgsqlDataSource>();
        await using var conn = await db.OpenConnectionAsync();
        await using (var cmd = new NpgsqlCommand("SELECT 1", conn))
            await cmd.ExecuteScalarAsync();
        await using (var cmd = new NpgsqlCommand("SELECT count(*) FROM dp.outbox WHERE published_at IS NULL", conn))
            outboxPending = (long)(await cmd.ExecuteScalarAsync())!;
        sw.Stop();
        dbMs = sw.ElapsedMilliseconds;
        dbOk = true;
    }
    catch { /* отчёт о здоровье не должен падать */ }

    // Состояние dp-sync (rabbit, схема, адаптеры) — агрегируется из воркера.
    JsonObject? workerState = null;
    var workerOk = false;
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var resp = await worker.GetAsync("/health", cts.Token);
        workerOk = resp.IsSuccessStatusCode;
        workerState = await JsonNode.ParseAsync(await resp.Content.ReadAsStreamAsync(cts.Token)) as JsonObject;
    }
    catch { /* воркер может быть поднят позже */ }

    return Results.Json(new JsonObject
    {
        ["status"] = dbOk && workerOk ? "ok" : "degraded",
        ["db"] = new JsonObject { ["ok"] = dbOk, ["latencyMs"] = dbMs },
        ["worker"] = new JsonObject { ["ok"] = workerOk, ["baseUrl"] = worker.BaseAddress?.ToString() },
        ["rabbitmq"] = workerState?["rabbitmq"]?.DeepClone() ?? new JsonObject { ["connected"] = false },
        ["outboxPending"] = outboxPending,
        ["sdStores"] = sd.Stores.Count,
        ["adapters"] = workerState?["adapters"]?.DeepClone() ?? new JsonArray()
    });
});

admin.MapGet("/adapters", (HttpContext ctx) => ProxyAsync(ctx, "/adapters"));

// ГЛАВНАЯ ДЕМО: горячее переключение auto ↔ real ↔ stub без рестарта
admin.MapPost("/adapters/{code}/mode", (HttpContext ctx, string code) => ProxyAsync(ctx, $"/adapters/{code}/mode"));

admin.MapPost("/sync/{code}", (HttpContext ctx, string code) => ProxyAsync(ctx, $"/run/{code}"));

// Просмотр непубликованных/опубликованных событий outbox (dp.data.changed.v1)
admin.MapGet("/outbox", async (HttpContext ctx) =>
{
    var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var l) ? Math.Clamp(l, 1, 1000) : 50;
    var db = app.Services.GetRequiredService<NpgsqlDataSource>();
    await using var conn = await db.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand("""
            SELECT id, occurred_at, store_id, store_version, entity, op, key, data, previous, tx_id, source, published_at
            FROM dp.outbox ORDER BY occurred_at DESC LIMIT @limit
            """, conn);
    cmd.Parameters.AddWithValue("@limit", limit);
    var arr = new JsonArray();
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
    {
        arr.Add(new JsonObject
        {
            ["eventId"] = r.GetGuid(0).ToString(),
            ["occurredAt"] = Rows.Iso(Rows.AsDto(r.GetValue(1))),
            ["storeId"] = r.GetString(2),
            ["storeVersion"] = r.GetString(3),
            ["entity"] = r.GetString(4),
            ["op"] = r.GetString(5),
            ["key"] = JsonNode.Parse(r.GetString(6)),
            ["data"] = r.IsDBNull(7) ? null : JsonNode.Parse(r.GetString(7)),
            ["previous"] = r.IsDBNull(8) ? null : JsonNode.Parse(r.GetString(8)),
            ["txId"] = r.GetString(9),
            ["source"] = r.GetString(10),
            ["publishedAt"] = r.IsDBNull(11) ? null : Rows.Iso(Rows.AsDto(r.GetValue(11)))
        });
    }
    return Results.Json(new JsonObject { ["events"] = arr, ["count"] = arr.Count });
});

admin.MapGet("/migrations", async () =>
{
    var journal = await migrator.JournalAsync();
    return Results.Json(new JsonObject
    {
        ["stores"] = new JsonArray(sd.Stores.Select(s => (JsonNode?)new JsonObject
        {
            ["storeId"] = s.StoreId,
            ["sdVersion"] = s.Metadata.Version,
            ["schema"] = DdlGenerator.StoreSchema(s)
        }).ToArray()),
        ["journal"] = journal
    });
});

admin.MapPost("/migrations/apply", async () =>
{
    var results = await migrator.ApplyAllAsync(sd.Stores);
    return Results.Json(new JsonObject
    {
        ["results"] = new JsonArray(results.Select(m => (JsonNode?)new JsonObject
        {
            ["storeId"] = m.StoreId,
            ["fromVersion"] = m.FromVersion,
            ["toVersion"] = m.ToVersion,
            ["status"] = m.Status,
            ["packageHash"] = m.PackageHash,
            ["durationMs"] = m.DurationMs
        }).ToArray())
    });
});

// SD перезагружается в обоих сервисах: dp-api (чтение по API) и dp-sync (запись при sync)
admin.MapPost("/sd/reload", async (HttpContext ctx) =>
{
    JsonObject apiResult;
    try
    {
        sd.Load();
        apiResult = new JsonObject { ["reloaded"] = true, ["stores"] = sd.Stores.Count };
    }
    catch (InvalidOperationException ex)
    {
        apiResult = new JsonObject { ["reloaded"] = false, ["errors"] = ex.Message };
    }

    JsonObject? workerResult = null;
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var resp = await worker.PostAsync("/sd/reload", content: null, cts.Token);
        workerResult = await JsonNode.ParseAsync(await resp.Content.ReadAsStreamAsync(cts.Token)) as JsonObject;
    }
    catch (Exception ex)
    {
        workerResult = new JsonObject { ["reloaded"] = false, ["errors"] = "dp-sync недоступен: " + ex.Message };
    }

    return Results.Json(new JsonObject
    {
        ["api"] = apiResult,
        ["worker"] = workerResult
    }, statusCode: apiResult["reloaded"]!.GetValue<bool>() ? 200 : 400);
});

app.MapGet("/", () => Results.Text(
    "Data Platform MVP (dp-api).\n" +
    "Data Access API: /dp/v1 (9 операций, см. /swagger)\n" +
    "Админ: /admin/health, /admin/adapters, POST /admin/adapters/{code}/mode, POST /admin/sync/{code}\n" +
    "Интеграционный контур (адаптеры 1С/Битрикс, outbox publisher) — в сервисе dp-sync.\n" +
    "Аутентификация: заголовок X-Api-Key\n"));

app.Run();

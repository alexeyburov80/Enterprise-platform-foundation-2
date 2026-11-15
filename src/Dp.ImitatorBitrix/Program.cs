// ============================================================================
// Имитатор Битрикс24 (REST webhook)
// ----------------------------------------------------------------------------
// Воспроизводит профиль Bitrix24 REST, используемый коннекторами Data Platform:
//   POST /rest/{user}/{key}/crm.deal.list.json   — выборка сделок (select/filter/start)
//   POST /rest/{user}/{key}/crm.deal.get.json    — сделка по ID
//   POST /rest/{user}/{key}/crm.deal.add.json    — создание сделки
//   POST /rest/{user}/{key}/crm.product.list.json — товары
//   POST /rest/{user}/{key}/batch.json           — батч-команды
// Ответы в формате Б24: { "result": ..., "total": ..., "time": {...} }.
// Поддерживается и упрощённый маршрут /rest/{method}.json?auth=<key>.
//
// Назначение MVP: локальный офлайн-стенд для опробования переключения
// адаптера Битрикс24 между режимами stub (in-process) и real (HTTP).
//
// Управление: POST /admin/rotate (сдвиг ревизии сумм), GET /admin/state.
// ============================================================================

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton(sp => new DatasetBx(builder.Configuration));
builder.Services.AddSingleton(new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
});
var app = builder.Build();

// --- Мидлварь: ключ вебхука, задержка, имитация сбоев -------------------------
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (path.StartsWith("/rest", StringComparison.OrdinalIgnoreCase))
    {
        var ds = ctx.RequestServices.GetRequiredService<DatasetBx>();
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // /rest/{user}/{key}/{method}.json  или  /rest/{method}.json
        string? provided = segments.Length >= 3 ? segments[2] : ctx.Request.Query["auth"].FirstOrDefault();
        if (provided != ds.WebhookKey)
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "INVALID_AUTH",
                error_description = "Неверный ключ входящего вебхука Битрикс24"
            });
            return;
        }

        var delayMs = ds.DelayMs;
        if (delayMs > 0) await Task.Delay(delayMs);

        if (ds.FailRate > 0 && Random.Shared.Next(100) < ds.FailRate)
        {
            ctx.Response.StatusCode = 503;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "QUERY_LIMIT_EXCEEDED",
                error_description = "Имитатор Б24: временная недоступность (сработал Data:FailRate)"
            });
            return;
        }
    }
    await next();
});

var methods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "crm.deal.list", "crm.deal.get", "crm.deal.add", "crm.product.list", "batch"
};

async Task Handle(HttpContext ctx, string method)
{
    var ds = ctx.RequestServices.GetRequiredService<DatasetBx>();
    var opts = ctx.RequestServices.GetRequiredService<JsonSerializerOptions>();

    // нормализуем маршрут: /rest/{user}/{key}/{method}.json -> {method}
    method = StripJson(method);
    if (ctx.Request.Path.Value is { } p)
    {
        var seg = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (seg.Length >= 3 && seg[0] == "rest") method = StripJson(seg[^1]);
    }

    JsonObject? body = null;
    try
    {
        if (ctx.Request.ContentLength > 0)
        {
            var doc = await JsonNode.ParseAsync(ctx.Request.Body);
            body = doc as JsonObject;
        }
    }
    catch { /* пустое или не-JSON тело — трактуем как отсутствующее */ }

    JsonObject result = method.ToLowerInvariant() switch
    {
        "crm.deal.list"   => ds.DealList(body, ctx.Request.Query),
        "crm.deal.get"    => ds.DealGet(body, ctx.Request.Query),
        "crm.deal.add"    => ds.DealAdd(body),
        "crm.product.list" => ds.ProductList(body),
        "batch"           => ds.Batch(body),
        _ => new JsonObject
        {
            ["error"] = "METHOD_NOT_FOUND",
            ["error_description"] = $"Метод '{method}' не поддерживается имитатором. Доступно: {string.Join(", ", methods)}"
        }
    };

    await ctx.Response.WriteAsJsonAsync(result, opts);
}

static string StripJson(string s) =>
    s.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? s[..^5] : s;

app.MapPost("/rest/{user}/{key}/{method}", (HttpContext ctx, string method) => Handle(ctx, method));
app.MapGet("/rest/{user}/{key}/{method}", (HttpContext ctx, string method) => Handle(ctx, method));
app.MapPost("/rest/{method}", (HttpContext ctx, string method) => Handle(ctx, method));
app.MapGet("/rest/{method}", (HttpContext ctx, string method) => Handle(ctx, method));

app.MapPost("/admin/rotate", (DatasetBx ds) => Results.Json(new { revision = ds.Rotate() }));

// Health-probe без аутентификации: им пользуется dp-sync при проверке
// доступности системы (режимы auto/real) и healthcheck в docker-compose.
app.MapGet("/health", (DatasetBx ds) => Results.Json(new
{
    status = "ok",
    system = "bitrix24",
    role = "imitator",
    revision = ds.Revision,
    deals = ds.Deals.Count,
    products = ds.Products.Count
}));

app.MapGet("/admin/state", (DatasetBx ds) => Results.Json(new
{
    revision = ds.Revision,
    deals = ds.Deals.Count,
    products = ds.Products.Count
}));
app.MapGet("/", () => Results.Text(
    "Имитатор Битрикс24 (Data Platform MVP).\n" +
    "REST: POST /rest/crm.deal.list.json?auth=<key> и /rest/{user}/{key}/crm.*.json\n" +
    "Управление: POST /admin/rotate, GET /admin/state\n"));

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "ImitatorBitrix v1"));

app.Run();

// ============================================================================

/// <summary>Детерминированный набор «Битрикс24». Суммы сделок зависят от ревизии.</summary>
public sealed class DatasetBx
{
    private readonly object _lock = new();
    private int _revision;
    private int _nextDealId = 1007;

    public string WebhookKey { get; }
    public int DelayMs { get; }
    public int FailRate { get; }

    private static readonly (string Id, string Title, decimal BaseAmount, string Stage, string ContactName, string Created, string Comments)[] BaseDeals =
    {
        ("1001", "Поставка фанеры — ООО СтройДом",           84000.00m,  "NEW",                 "Смирнов А.В.",  "2026-11-01T09:15:00", "Запрос по email, ждут счёт"),
        ("1002", "Ремонт офиса — ИП Смирнов",                 156000.00m, "PREPARATION",         "Кузнецова М.И.", "2026-11-03T11:40:00", "Согласована смета"),
        ("1003", "Оснащение склада — ООО ДельтаПлюс",         312500.00m, "EXECUTING",           "Гаврилов П.П.", "2026-11-06T14:05:00", "Договор подписан"),
        ("1004", "Модернизация цеха — АО Металлсервис",       76000.00m,  "PREPAYMENT_INVOICE",  "Орлова Е.С.",   "2026-11-08T10:20:00", "Выставлен счёт на предоплату"),
        ("1005", "Поставка утеплителя — ООО ТеплоСтрой",      129800.00m, "WON",                 "Белов К.Н.",    "2026-11-10T16:45:00", "Отгружено полностью"),
        ("1006", "Конкурсный заказ — ТД Вектор",              44500.00m,  "LOSE",                "Ильина Д.А.",   "2026-11-11T12:30:00", "Выбран другой поставщик")
    };

    private static readonly (string Id, string Name, decimal BasePrice)[] BaseProducts =
    {
        ("P-001", "Фанера ФК 18 мм",            1250.00m),
        ("P-002", "Брус 100x100x6000",           640.00m),
        ("P-003", "Саморез 3.5x35 (уп. 1000)",   190.00m),
        ("P-004", "Краска ВОЛОШКА белая 2.7 л",  480.00m),
        ("P-005", "Профиль ПН-75 (3 м)",         145.00m),
        ("P-006", "Шпаклёвка финишная 20 кг",    520.00m),
        ("P-007", "Утеплитель РОКЛАЙТ 50 мм",    890.00m),
        ("P-008", "Гвозди 100 мм (насыпью)",     210.00m)
    };

    public DatasetBx(IConfiguration cfg)
    {
        WebhookKey = cfg["Auth:Key"] ?? "webhook-dev-key";
        DelayMs = cfg.GetValue("Data:DelayMs", 120);
        FailRate = cfg.GetValue("Data:FailRate", 0);
        _revision = cfg.GetValue("Data:Revision", 0);
    }

    public int Revision { get { lock (_lock) return _revision; } }
    public int Rotate() { lock (_lock) return ++_revision; }

    public List<JsonObject> Deals
    {
        get
        {
            var rev = Revision;
            return BaseDeals.Select(d => new JsonObject
            {
                ["ID"] = d.Id,
                ["TITLE"] = d.Title,
                ["OPPORTUNITY"] = Math.Round(d.BaseAmount * (decimal)Math.Pow(1.05, rev), 2),
                ["CURRENCY_ID"] = "RUB",
                ["STAGE_ID"] = d.Stage,
                ["CONTACT_ID"] = "C-" + d.Id,
                ["UF_CONTACT_NAME"] = d.ContactName,
                ["DATE_CREATE"] = d.Created,
                ["COMMENTS"] = d.Comments
            }).ToList();
        }
    }

    public List<JsonObject> Products
    {
        get
        {
            var rev = Revision;
            return BaseProducts.Select(p => new JsonObject
            {
                ["ID"] = p.Id,
                ["NAME"] = p.Name,
                ["PRICE"] = Math.Round(p.BasePrice * (decimal)Math.Pow(1.05, rev), 2),
                ["CURRENCY_ID"] = "RUB",
                ["ACTIVE"] = "Y"
            }).ToList();
        }
    }

    public JsonObject DealList(JsonObject? body, IQueryCollection? query = null)
    {
        var select = body?["select"] as JsonArray;
        var filter = body?["filter"] as JsonObject;

        int start = 0;
        if (body?["start"] is JsonValue svStart && svStart.TryGetValue<int>(out var s1)) start = s1;
        else if (query is not null && int.TryParse(query["start"].FirstOrDefault(), out var s2)) start = s2;

        var all = Deals;
        if (filter is not null)
        {
            if (filter.TryGetPropertyValue("STAGE_ID", out var st) && st is JsonValue sv) all = all.Where(d => (string?)d["STAGE_ID"] == (string?)sv).ToList();
            if (filter.TryGetPropertyValue("ID", out var id) && id is JsonValue iv) all = all.Where(d => (string?)d["ID"] == (string?)iv).ToList();
        }

        var page = all.Skip(start).Take(50).ToList();
        var res = new JsonObject { ["result"] = Project(page, select), ["total"] = all.Count };
        if (start + page.Count < all.Count) res["next"] = start + page.Count;
        res["time"] = new JsonObject { ["start"] = 0.0d, ["duration"] = 0.01d };
        return res;
    }

    public JsonObject DealGet(JsonObject? body, IQueryCollection query)
    {
        var id = (string?)body?["ID"] ?? query["ID"].FirstOrDefault();
        var deal = Deals.FirstOrDefault(d => (string?)d["ID"] == id);
        if (deal is null)
            return new JsonObject { ["error"] = "NOT_FOUND", ["error_description"] = $"Сделка с ID={id} не найдена" };
        return new JsonObject { ["result"] = deal.DeepClone(), ["time"] = new JsonObject { ["duration"] = 0.01d } };
    }

    public JsonObject DealAdd(JsonObject? body)
    {
        var fields = body?["fields"] as JsonObject;
        if (fields is null)
            return new JsonObject { ["error"] = "PARAMS_MISSING", ["error_description"] = "Ожидается {\"fields\": {...}}" };

        string id;
        lock (_lock) id = (_nextDealId++).ToString();
        var deal = new JsonObject
        {
            ["ID"] = id,
            ["TITLE"] = (string?)fields["TITLE"] ?? "(без названия)",
            ["OPPORTUNITY"] = fields["OPPORTUNITY"]?.DeepClone() ?? 0,
            ["CURRENCY_ID"] = (string?)fields["CURRENCY_ID"] ?? "RUB",
            ["STAGE_ID"] = (string?)fields["STAGE_ID"] ?? "NEW",
            ["CONTACT_ID"] = (string?)fields["CONTACT_ID"] ?? "",
            ["UF_CONTACT_NAME"] = (string?)fields["UF_CONTACT_NAME"] ?? "",
            ["DATE_CREATE"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss"),
            ["COMMENTS"] = (string?)fields["COMMENTS"] ?? ""
        };
        lock (_lock) DealsAdd(deal);
        return new JsonObject { ["result"] = id, ["time"] = new JsonObject { ["duration"] = 0.01d } };
    }

    private readonly List<JsonObject> _added = new();
    private void DealsAdd(JsonObject deal) => _added.Add(deal);

    public JsonObject ProductList(JsonObject? body)
    {
        var all = Products;
        return new JsonObject
        {
            ["result"] = new JsonArray(all.Select(p => (JsonNode?)p.DeepClone()).ToArray()),
            ["total"] = all.Count,
            ["time"] = new JsonObject { ["duration"] = 0.01d }
        };
    }

    public JsonObject Batch(JsonObject? body)
    {
        var cmd = body?["cmd"] as JsonObject;
        if (cmd is null)
            return new JsonObject { ["error"] = "PARAMS_MISSING", ["error_description"] = "Ожидается {\"cmd\": {\"k0\": \"method?...\"}}" };

        var results = new JsonObject();
        var errors = new JsonObject();
        foreach (var (key, raw) in cmd)
        {
            var spec = (string?)raw ?? "";
            var qIdx = spec.IndexOf('?');
            var method = qIdx < 0 ? spec : spec[..qIdx];
            JsonObject? inner = method.ToLowerInvariant() switch
            {
                "crm.deal.list" => DealList(new JsonObject { ["select"] = new JsonArray("ID", "TITLE", "STAGE_ID", "OPPORTUNITY") }),
                "crm.product.list" => ProductList(null),
                _ => null
            };
            if (inner is null)
                errors[key] = $"Метод '{method}' недоступен в batch имитатора";
            else
                results[key] = inner["result"]?.DeepClone() ?? new JsonObject();
        }

        var res = new JsonObject { ["result"] = results };
        if (errors.Count > 0) res["result_error"] = errors;
        res["time"] = new JsonObject { ["duration"] = 0.01d };
        return res;
    }

    private static JsonArray Project(List<JsonObject> rows, JsonArray? select)
    {
        var arr = new JsonArray();
        foreach (var row in rows)
        {
            if (select is null || select.Count == 0)
            {
                arr.Add(row.DeepClone());
                continue;
            }
            var proj = new JsonObject();
            foreach (var s in select)
            {
                var key = (string?)s;
                if (key is not null && row.TryGetPropertyValue(key, out var v)) proj[key] = v?.DeepClone();
            }
            arr.Add(proj);
        }
        return arr;
    }
}

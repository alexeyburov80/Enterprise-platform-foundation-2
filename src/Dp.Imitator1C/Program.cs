// ============================================================================
// Имитатор 1С:Предприятие (REST/OData-подобный интерфейс обмена)
// ----------------------------------------------------------------------------
// Воспроизводит профиль интеграционного API 1С, используемый коннекторами
// Data Platform: Basic-аутентификация, OData-стиль ответов
// ({ "odata.metadata", "odata.count", "value": [...] }), $top/$skip/$count,
// фильтр "Ref_Key eq '<guid>'".
//
// Назначение MVP: локальный офлайн-стенд для опробования переключения
// адаптера 1С между режимами stub (in-process) и real (HTTP к этой системе).
//
// Управление (без изменения кода):
//   POST /admin/rotate   — сдвиг ревизии цен (демонстрация дельты при синке)
//   GET  /admin/state    — текущее состояние имитатора
//   Data:DelayMs / Data:FailRate — имитация задержки и сбоев (churn-тесты)
// ============================================================================

using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton(sp => new Dataset1C(builder.Configuration));
builder.Services.AddSingleton(new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
});
var app = builder.Build();

// --- Мидлварь: Basic-аутентификация для /odata, задержка, имитация сбоев -----
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";

    if (path.StartsWith("/odata", StringComparison.OrdinalIgnoreCase))
    {
        var cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();
        var user = cfg["Integration:User"] ?? "svc_dp";
        var pass = cfg["Integration:Password"] ?? "dp-secret";

        if (!CheckBasicAuth(ctx.Request.Headers["Authorization"].FirstOrDefault(), user, pass))
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"1C OData\"";
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "1C_AUTH_FAILED",
                error_description = "Требуется Basic-аутентификация (пользователь обмена)"
            });
            return;
        }

        var delayMs = cfg.GetValue("Data:DelayMs", 120);
        if (delayMs > 0) await Task.Delay(delayMs);

        var failRate = cfg.GetValue("Data:FailRate", 0);
        if (failRate > 0 && Random.Shared.Next(100) < failRate)
        {
            ctx.Response.StatusCode = 503;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "1C_BUSY",
                error_description = "Имитатор 1С: временная недоступность (сработал Data:FailRate)"
            });
            return;
        }
    }

    await next();
});

// --- Сервисный документ OData ------------------------------------------------
app.MapGet("/odata", (JsonSerializerOptions opts) => Results.Json(new
{
    odata = "1.0",
    value = new object[]
    {
        new { name = "Catalog_Номенклатура",        kind = "EntitySet", url = "Catalog_Номенклатура" },
        new { name = "Document_ЗаказКлиента",       kind = "EntitySet", url = "Document_ЗаказКлиента" },
        new { name = "Document_ЗаказКлиента_Товары", kind = "EntitySet", url = "Document_ЗаказКлиента_Товары" }
    }
}, opts));

// --- Наборы сущностей ---------------------------------------------------------
// Поддерживаются и кириллические имена 1С, и ASCII-алиасы (для клиентов,
// которые не любят percent-encoding в URL).
var asciiAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["Catalog_Nomenclature"]           = "Catalog_Номенклатура",
    ["Document_CustomerOrder"]         = "Document_ЗаказКлиента",
    ["Document_CustomerOrder_Lines"]   = "Document_ЗаказКлиента_Товары"
};

app.MapGet("/odata/{entity}", (string entity, Dataset1C ds, HttpRequest req, JsonSerializerOptions opts) =>
{
    if (asciiAliases.TryGetValue(entity, out var canon)) entity = canon;

    object? rows = entity switch
    {
        "Catalog_Номенклатура"         => ds.GetNomenclature(),
        "Document_ЗаказКлиента"        => ds.GetOrders(),
        "Document_ЗаказКлиента_Товары" => ds.GetOrderLines(),
        _ => null
    };
    if (rows is null)
    {
        return Results.Json(new
        {
            error = "1C_ENTITY_NOT_FOUND",
            error_description = $"Набор сущностей '{entity}' не опубликован в имитаторе"
        }, opts, statusCode: 404);
    }

    var list = (List<Dictionary<string, object?>>)rows;

    // $filter: поддерживается только "Ref_Key eq '<guid>'" (типовой случай коннектора)
    var filter = req.Query["$filter"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(filter))
    {
        var m = Regex.Match(filter, @"^\s*Ref_Key\s+eq\s+'([0-9a-fA-F\-]{36})'\s*$");
        if (m.Success && Guid.TryParse(m.Groups[1].Value, out var want))
        {
            list = list.Where(r => r["Ref_Key"] is Guid g && g == want).ToList();
        }
    }

    var skip = ParseInt(req.Query["$skip"].FirstOrDefault(), 0);
    var top = ParseInt(req.Query["$top"].FirstOrDefault(), 1000);
    var wantCount = string.Equals(req.Query["$count"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);

    var page = list.Skip(skip).Take(Math.Clamp(top, 0, 10000)).ToList();

    var envelope = new Dictionary<string, object?>
    {
        ["odata.metadata"] = "odata.service",
        ["value"] = page
    };
    if (wantCount) envelope["odata.count"] = list.Count;

    return Results.Json(envelope, opts);
});

// --- Управление имитатором ----------------------------------------------------
app.MapPost("/admin/rotate", (Dataset1C ds, JsonSerializerOptions opts) =>
    Results.Json(new { revision = ds.Rotate() }, opts));

// Health-probe без аутентификации: им пользуется dp-sync при проверке
// доступности системы (режимы auto/real) и healthcheck в docker-compose.
app.MapGet("/health", (Dataset1C ds, JsonSerializerOptions opts) => Results.Json(new
{
    status = "ok",
    system = "1c",
    role = "imitator",
    revision = ds.Revision,
    nomenclature = ds.GetNomenclature().Count,
    orders = ds.GetOrders().Count,
    orderLines = ds.GetOrderLines().Count
}, opts));

app.MapGet("/admin/state", (Dataset1C ds, JsonSerializerOptions opts) => Results.Json(new
{
    revision = ds.Revision,
    nomenclature = ds.GetNomenclature().Count,
    orders = ds.GetOrders().Count,
    orderLines = ds.GetOrderLines().Count
}, opts));

app.MapGet("/", () => Results.Text(
    "Имитатор 1С:Предприятие (Data Platform MVP).\n" +
    "OData: GET /odata, /odata/Catalog_Номенклатура, /odata/Document_ЗаказКлиента, /odata/Document_ЗаказКлиента_Товары\n" +
    "Управление: POST /admin/rotate, GET /admin/state, GET /health (probe)\n" +
    "Аутентификация /odata: Basic (svc_dp / dp-secret)\n"));

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Imitator1C v1"));

app.Run();

// ============================================================================

static bool CheckBasicAuth(string? header, string user, string pass)
{
    if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        return false;
    try
    {
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
        var idx = decoded.IndexOf(':');
        return idx >= 0 && decoded[..idx] == user && decoded[(idx + 1)..] == pass;
    }
    catch { return false; }
}

static int ParseInt(string? s, int def) => int.TryParse(s, out var v) ? v : def;

/// <summary>
/// Детерминированный набор данных «1С». Цены зависят от ревизии:
/// цена_i = base_i * 1.07^revision (округление до копеек). POST /admin/rotate
/// сдвигает ревизию — при следующем синке коннектор увидит дельту.
/// Ref_Key всех объектов детерминированы (uuid5), поэтому при переключении
/// stub/real записи ложатся в те же ключи Data Platform.
/// </summary>
public sealed class Dataset1C
{
    private readonly object _lock = new();
    private int _revision;

    private static readonly (string Code, string Name, string Article, string Unit, decimal BasePrice, double WeightKg)[] Nom =
    {
        ("ФНК-001", "Фанера ФК 18 мм",                 "ФНК-001", "лист",      1250.00m, 18.5),
        ("БРС-100", "Брус 100x100x6000",               "БРС-100", "шт",         640.00m,  10.0),
        ("СМЗ-035", "Саморез 3.5x35 (уп. 1000)",       "СМЗ-035", "упак",       190.00m,  1.2),
        ("КРС-027", "Краска ВОЛОШКА белая 2.7 л",      "КРС-027", "банка",      480.00m,  3.1),
        ("ПРФ-075", "Профиль ПН-75 (3 м)",             "ПРФ-075", "шт",         145.00m,  2.4),
        ("ШПК-020", "Шпаклёвка финишная 20 кг",        "ШПК-020", "мешок",      520.00m,  20.5),
        ("УТЛ-050", "Утеплитель РОКЛАЙТ 50 мм",        "УТЛ-050", "упаковка",   890.00m,  12.8),
        ("ГВЛ-100", "Гвозди 100 мм (насыпью)",         "ГВЛ-100", "кг",         210.00m,  5.0)
    };

    private static readonly (string Number, string Customer, string Date, string Status1C, (string Code, decimal Qty)[] Lines)[] Orders =
    {
        ("ЗК-2026-000101", "ООО СтройДом",     "2026-11-02", "К выполнению",
            new[] { ("ФНК-001", 20m), ("СМЗ-035", 5m) }),
        ("ЗК-2026-000102", "ООО ДельтаПлюс",   "2026-11-05", "Отгружен",
            new[] { ("КРС-027", 12m), ("ШПК-020", 3m) }),
        ("ЗК-2026-000103", "ИП Смирнов А.В.",  "2026-11-08", "К выполнению",
            new[] { ("БРС-100", 40m), ("ГВЛ-100", 8m) }),
        ("ЗК-2026-000104", "ООО СтройДом",     "2026-11-12", "Отгружен",
            new[] { ("УТЛ-050", 6m), ("ПРФ-075", 30m) })
    };

    public Dataset1C(IConfiguration cfg) => _revision = cfg.GetValue("Data:Revision", 0);

    public int Revision { get { lock (_lock) return _revision; } }

    public int Rotate() { lock (_lock) return ++_revision; }

    private static decimal Price(decimal basePrice, int rev) =>
        Math.Round(basePrice * (decimal)Math.Pow(1.07, rev), 2);

    public List<Dictionary<string, object?>> GetNomenclature()
    {
        var rev = Revision;
        return Nom.Select(n => new Dictionary<string, object?>
        {
            ["Ref_Key"] = Uuid5("1c:nom:" + n.Code),
            ["Code"] = n.Code,
            ["Description"] = n.Name,
            ["Артикул"] = n.Article,
            ["ЕдиницаИзмерения"] = n.Unit,
            ["Цена"] = Price(n.BasePrice, rev),
            ["ВесКг"] = n.WeightKg
        }).ToList();
    }

    public List<Dictionary<string, object?>> GetOrderLines()
    {
        var rev = Revision;
        var lines = new List<Dictionary<string, object?>>();
        foreach (var o in Orders)
        {
            var docRef = Uuid5("1c:ord:" + o.Number);
            for (var i = 0; i < o.Lines.Length; i++)
            {
                var (code, qty) = o.Lines[i];
                var nom = Nom.First(n => n.Code == code);
                var price = Price(nom.BasePrice, rev);
                lines.Add(new Dictionary<string, object?>
                {
                    ["Ref_Key"] = docRef,
                    ["LineNumber"] = i + 1,
                    ["Номенклатура_Key"] = Uuid5("1c:nom:" + code),
                    ["Номенклатура"] = nom.Name,
                    ["Количество"] = qty,
                    ["Цена"] = price,
                    ["Сумма"] = Math.Round(qty * price, 2)
                });
            }
        }
        return lines;
    }

    public List<Dictionary<string, object?>> GetOrders()
    {
        var rev = Revision;
        var lines = GetOrderLines();
        return Orders.Select(o =>
        {
            var docRef = Uuid5("1c:ord:" + o.Number);
            var sum = lines
                .Where(l => l["Ref_Key"] is Guid g && g == docRef)
                .Sum(l => (decimal)l["Сумма"]!);
            return new Dictionary<string, object?>
            {
                ["Ref_Key"] = docRef,
                ["Number"] = o.Number,
                ["Date"] = o.Date + "T00:00:00",
                ["Статус"] = o.Status1C,
                ["Контрагент_Key"] = Uuid5("1c:contr:" + o.Customer),
                ["Контрагент"] = o.Customer,
                ["Валюта"] = "RUB",
                ["СуммаДокумента"] = sum
            };
        }).ToList();
    }

    /// <summary>RFC 4122 uuid5 (SHA-1) от текстового ключа — стабильные Ref_Key.</summary>
    public static Guid Uuid5(string name)
    {
        Span<byte> ns =
        [
            0x6b, 0xa7, 0xb8, 0x10, 0x9d, 0xad, 0x11, 0xd1, 0x80, 0xb4, 0x00, 0xc0, 0x4f, 0xd4, 0x30, 0xc8
        ];
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var hash = SHA1.HashData(ns.ToArray().Concat(nameBytes).ToArray());
        var b = new byte[16];
        Array.Copy(hash, b, 16);
        b[6] = (byte)((b[6] & 0x0f) | 0x50); // version 5
        b[8] = (byte)((b[8] & 0x3f) | 0x80); // RFC variant
        return new Guid(b);
    }
}

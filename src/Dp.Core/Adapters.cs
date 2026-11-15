// ============================================================================
// Интеграционные адаптеры и переключение auto / real / stub
// (Strangler Fig / Adoption v1).
//
// Архитектура переключения (без изменения кода):
//   1) Конфигурация при старте:  Integration:Systems:<code>:Mode = auto | real | stub
//      (env: Integration__Systems__1c__Mode=auto) — путь docker-compose/.env.
//   2) Горячее переключение в рантайме: POST /admin/adapters/<code>/mode
//      {"mode":"real"|"stub"|"auto"} — демонстрация Strangler Fig на живом стенде.
//
// Режимы:
//   stub  — HTTP-коннектор к НАШЕМУ сервису-заместителю (имитатор 1С/Битрикс,
//           отдельный контейнер). Встроенных заглушек в платформе больше нет:
//           каждая внешняя система — отдельный сервис, даже в тестовом контуре.
//   real  — HTTP-коннектор к реальному контуру (RealBaseUrl). Явный режим:
//           недоступность реальной системы = ошибка DP-4001 (без подмены).
//   auto  — производственный режим по умолчанию: перед каждой синхронизацией
//           платформа пробует health-probe реальной системы; если она
//           недоступна/не настроена — transparentно замещает её собственным
//           сервисом-имитатором (fallback), фиксируя причину в /admin/adapters.
//
// Результат адаптера — нормализованные записи (канонические поля + ext-namespace),
// одинаковые для real и stub, поэтому downstream-код (ingestion) не знает режим.
// Метка источника пишется в ext.<ns>.source: "real" | "stub".
// ============================================================================

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Dp.Core;

public sealed record NormalizedRecord(
    string ExternalId,
    string? ParentExternalId,
    Dictionary<string, object?> Fields,
    Dictionary<string, object?> ExtFields);

public sealed record NormalizedBatch(
    string StoreId, string Entity, string ExtNamespace, IReadOnlyList<NormalizedRecord> Records);

public interface ISystemAdapter
{
    string Code { get; }
    string DisplayName { get; }
    string Endpoint { get; }
    /// <summary>Метка источника для ext.&lt;ns&gt;.source: "real" | "stub".</summary>
    string SourceTag { get; }
    Task<List<NormalizedBatch>> FetchAsync(CancellationToken ct = default);
}

// --------------------------------------------------------------------------
// 1С: HTTP-коннектор (OData-подобный REST) — и к реальной 1С, и к имитатору.
// Профиль обмена один и тот же, отличие только в BaseUrl — это и есть
// точка Strangler Fig.
// --------------------------------------------------------------------------

public sealed class OneCHttpAdapter : ISystemAdapter
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public string Code => "1c";
    public string DisplayName => "1С:Предприятие (HTTP-коннектор)";
    public string Endpoint { get; }
    public string SourceTag { get; }

    private static readonly Dictionary<string, string> StatusMap = new(StringComparer.Ordinal)
    {
        ["К выполнению"] = "submitted",
        ["Отгружен"] = "approved",
        ["Отменена"] = "cancelled"
    };

    public OneCHttpAdapter(string baseUrl, string user, string password, string sourceTag,
        ILogger logger)
    {
        Endpoint = baseUrl;
        SourceTag = sourceTag;
        _logger = logger;
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    public async Task<List<NormalizedBatch>> FetchAsync(CancellationToken ct = default)
    {
        var nom = await FetchListAsync("/odata/Catalog_Nomenclature?$top=1000&$format=json", ct);
        var orders = await FetchListAsync("/odata/Document_CustomerOrder?$top=1000&$format=json", ct);
        var lines = await FetchListAsync("/odata/Document_CustomerOrder_Lines?$top=10000&$format=json", ct);

        var codeByRef = nom.ToDictionary(
            r => (string?)r["Ref_Key"] ?? "",
            r => (string?)r["Code"] ?? "");

        var nomRecords = nom.Select(r =>
        {
            var code = (string?)r["Code"] ?? "";
            return new NormalizedRecord(code, null,
                new Dictionary<string, object?>
                {
                    ["code"] = code,
                    ["name"] = (string?)r["Description"] ?? "",
                    ["article"] = (string?)r["Артикул"] ?? "",
                    ["unit"] = (string?)r["ЕдиницаИзмерения"] ?? "",
                    ["price"] = ToDecimal(r["Цена"]),
                    ["weightKg"] = ToDecimal(r["ВесКг"]),
                    ["active"] = true
                },
                new Dictionary<string, object?>
                {
                    ["refKey"] = (string?)r["Ref_Key"] ?? "",
                    ["source"] = SourceTag
                });
        }).ToList();

        var orderRecords = orders.Select(r =>
        {
            var number = (string?)r["Number"] ?? "";
            var customer = (string?)r["Контрагент"] ?? "";
            return new NormalizedRecord(number, null,
                new Dictionary<string, object?>
                {
                    ["number"] = number,
                    ["orderDate"] = DateOnly.Parse(((string?)r["Date"] ?? "2026-01-01")[..10]),
                    ["status"] = StatusMap.TryGetValue((string?)r["Статус"] ?? "", out var st) ? st : "draft",
                    ["customerRef"] = Guid.TryParse((string?)r["Контрагент_Key"], out var cr)
                        ? cr
                        : DpUtil.Uuid5("1c:contr:" + customer),
                    ["customerName"] = customer,
                    ["currencyCode"] = (string?)r["Валюта"] ?? "RUB",
                    ["totalAmount"] = ToDecimal(r["СуммаДокумента"])
                },
                new Dictionary<string, object?>
                {
                    ["refKey"] = (string?)r["Ref_Key"] ?? "",
                    ["source"] = SourceTag
                });
        }).ToList();

        var lineRecords = lines
            .Select(r =>
            {
                var docRef = (string?)r["Ref_Key"] ?? "";
                var parent = orders.FirstOrDefault(o => (string?)o["Ref_Key"] == docRef);
                var parentNumber = (string?)parent?["Number"];
                if (parentNumber is null) return null;
                var sku = codeByRef.TryGetValue((string?)r["Номенклатура_Key"] ?? "", out var c) ? c : "";
                return new NormalizedRecord(
                    parentNumber + "/" + ((int?)r["LineNumber"] ?? 0),
                    parentNumber,
                    new Dictionary<string, object?>
                    {
                        ["sku"] = sku,
                        ["qty"] = ToDecimal(r["Количество"]),
                        ["price"] = ToDecimal(r["Цена"])
                    },
                    new Dictionary<string, object?> { ["source"] = SourceTag });
            })
            .Where(x => x is not null)
            .Cast<NormalizedRecord>()
            .ToList();

        return
        [
            new("mdm.nomenclature", "Nomenclature", "onec", nomRecords),
            new("sales.orders", "Order", "onec", orderRecords),
            new("sales.orders", "OrderLine", "onec", lineRecords)
        ];
    }

    private async Task<List<JsonObject>> FetchListAsync(string url, CancellationToken ct)
    {
        _logger.LogDebug("1С-коннектор: GET {Base}{Url}", _http.BaseAddress, url);
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var doc = await JsonNode.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (doc?["value"] is not JsonArray arr)
            throw new DpException(DpCodes.ProviderUnavailable, 503, "1С-коннектор: ответ без массива value");
        return arr.OfType<JsonObject>().ToList();
    }

    private static decimal ToDecimal(JsonNode? node)
    {
        if ((decimal?)node is { } v) return v;
        return decimal.TryParse((string?)node, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m;
    }
}

// --------------------------------------------------------------------------
// Битрикс24: HTTP-коннектор (REST webhook) — к реальному Б24 и к имитатору.
// --------------------------------------------------------------------------

public sealed class BitrixHttpAdapter : ISystemAdapter
{
    private readonly HttpClient _http;
    private readonly string _webhookKey;
    private readonly ILogger _logger;

    public string Code => "bitrix";
    public string DisplayName => "Битрикс24 (HTTP-коннектор)";
    public string Endpoint { get; }
    public string SourceTag { get; }

    private static readonly Dictionary<string, string> StageMap = new(StringComparer.Ordinal)
    {
        ["NEW"] = "new",
        ["PREPARATION"] = "qualified",
        ["EXECUTING"] = "proposal",
        ["PREPAYMENT_INVOICE"] = "negotiation",
        ["WON"] = "won",
        ["LOSE"] = "lost"
    };

    public BitrixHttpAdapter(string baseUrl, string webhookKey, string sourceTag,
        ILogger logger)
    {
        Endpoint = baseUrl;
        SourceTag = sourceTag;
        _webhookKey = webhookKey;
        _logger = logger;
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<List<NormalizedBatch>> FetchAsync(CancellationToken ct = default)
    {
        var deals = new List<JsonObject>();
        int start = 0;
        while (true)
        {
            var body = new JsonObject
            {
                ["select"] = new JsonArray("ID", "TITLE", "OPPORTUNITY", "CURRENCY_ID", "STAGE_ID",
                    "UF_CONTACT_NAME", "DATE_CREATE", "COMMENTS"),
                ["start"] = start
            };
            var page = await CallAsync("crm.deal.list.json", body, ct);
            if (page["result"] is not JsonArray arr || arr.Count == 0) break;
            deals.AddRange(arr.OfType<JsonObject>());
            if (page["next"] is not JsonValue nv || (int?)nv is null) break;
            start = (int?)nv ?? start + 50;
            if (deals.Count >= 500) break;
        }

        var records = deals.Select(r => new NormalizedRecord(
            (string?)r["ID"] ?? "",
            null,
            new Dictionary<string, object?>
            {
                ["externalId"] = (string?)r["ID"] ?? "",
                ["title"] = (string?)r["TITLE"] ?? "",
                ["stage"] = StageMap.TryGetValue((string?)r["STAGE_ID"] ?? "", out var s) ? s : "new",
                ["amount"] = ToDecimal(r["OPPORTUNITY"]),
                ["currencyCode"] = (string?)r["CURRENCY_ID"] ?? "RUB",
                ["contactName"] = (string?)r["UF_CONTACT_NAME"] ?? "",
                ["createdOn"] = DateTimeOffset.TryParse((string?)r["DATE_CREATE"], out var ts) ? ts : DateTimeOffset.UtcNow,
                ["comment"] = (string?)r["COMMENTS"] ?? ""
            },
            new Dictionary<string, object?>
            {
                ["dealId"] = (string?)r["ID"] ?? "",
                ["stageId"] = (string?)r["STAGE_ID"] ?? "",
                ["source"] = SourceTag
            })).ToList();

        return [new("crm.deals", "Deal", "bitrix", records)];
    }

    private async Task<JsonObject> CallAsync(string method, JsonObject body, CancellationToken ct)
    {
        var url = $"/rest/crm/{_webhookKey}/{method}";
        _logger.LogDebug("Б24-коннектор: POST {Base}{Url}", _http.BaseAddress, url);
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(url, content, ct);
        resp.EnsureSuccessStatusCode();
        var doc = await JsonNode.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc as JsonObject ?? new JsonObject();
    }

    private static decimal ToDecimal(JsonNode? node) =>
        (decimal?)node ?? 0m;
}

// --------------------------------------------------------------------------
// Adapter Switcher — ядро переключения auto / real / stub с fallback на
// собственные сервисы-имитаторы, когда реальная система недоступна.
// --------------------------------------------------------------------------

/// <summary>Результат health-probe реальной системы (GET {RealBaseUrl}/health).</summary>
public sealed record ProbeResult(bool Ok, string Detail, long LatencyMs, DateTimeOffset CheckedAt);

/// <summary>Адаптер, выбранный для конкретной синхронизации.</summary>
public sealed record ResolvedAdapter(
    ISystemAdapter Adapter, string Mode, string ModeSource, string Endpoint, string SourceTag,
    bool Fallback, string? FallbackReason);

public sealed record AdapterInfo(
    string Code, string DisplayName, string Mode, string ModeSource,
    string RealBaseUrl, string StubBaseUrl,
    ProbeResult? LastRealProbe, DateTimeOffset? LastFallbackAt, string? LastFallbackReason,
    string? LastEndpoint, DateTimeOffset? LastSyncAt, int? LastSyncTotal);

public sealed class AdapterSwitcher
{
    private sealed class Entry
    {
        public required string Code;
        public required string DisplayName;
        public required ISystemAdapter Stub;
        public required ISystemAdapter Real;
        public required string StubBaseUrl;
        public required string RealBaseUrl;
        public string Mode = "auto";
        public string ModeSource = "config";
        public ProbeResult? LastRealProbe;
        public DateTimeOffset? LastFallbackAt;
        public string? LastFallbackReason;
        public string? LastEndpoint;
        public DateTimeOffset? LastSyncAt;
        public int? LastSyncTotal;
    }

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2.5);
    private readonly HttpClient _probe;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly ILogger<AdapterSwitcher> _logger;

    public AdapterSwitcher(IConfiguration cfg, ILogger<AdapterSwitcher> logger)
    {
        _logger = logger;
        _probe = new HttpClient { Timeout = ProbeTimeout };

        // --- 1С -------------------------------------------------------------
        var oneCStubUrl = cfg["Integration:Systems:1c:StubBaseUrl"] ?? "http://localhost:8081";
        var oneCRealUrl = cfg["Integration:Systems:1c:RealBaseUrl"] ?? "";
        var oneCUser = cfg["Integration:Systems:1c:User"] ?? "svc_dp";
        var oneCPass = cfg["Integration:Systems:1c:Password"] ?? "dp-secret";
        _entries["1c"] = new Entry
        {
            Code = "1c",
            DisplayName = "1С:Предприятие",
            Stub = new OneCHttpAdapter(oneCStubUrl, oneCUser, oneCPass, "stub", _logger),
            Real = new OneCHttpAdapter(oneCRealUrl, oneCUser, oneCPass, "real", _logger),
            StubBaseUrl = oneCStubUrl,
            RealBaseUrl = oneCRealUrl,
            Mode = ValidMode(cfg["Integration:Systems:1c:Mode"] ?? "auto", "1c")
        };

        // --- Битрикс24 ------------------------------------------------------
        var bxStubUrl = cfg["Integration:Systems:bitrix:StubBaseUrl"] ?? "http://localhost:8082";
        var bxRealUrl = cfg["Integration:Systems:bitrix:RealBaseUrl"] ?? "";
        var bxKey = cfg["Integration:Systems:bitrix:WebhookKey"] ?? "webhook-dev-key";
        _entries["bitrix"] = new Entry
        {
            Code = "bitrix",
            DisplayName = "Битрикс24",
            Stub = new BitrixHttpAdapter(bxStubUrl, bxKey, "stub", _logger),
            Real = new BitrixHttpAdapter(bxRealUrl, bxKey, "real", _logger),
            StubBaseUrl = bxStubUrl,
            RealBaseUrl = bxRealUrl,
            Mode = ValidMode(cfg["Integration:Systems:bitrix:Mode"] ?? "auto", "bitrix")
        };

        foreach (var e in _entries.Values)
            _logger.LogInformation(
                "AdapterSwitcher: {Code} — режим {Mode} (real: {Real}, заместитель: {Stub})",
                e.Code, e.Mode,
                string.IsNullOrWhiteSpace(e.RealBaseUrl) ? "не настроен" : e.RealBaseUrl,
                e.StubBaseUrl);
    }

    private static string ValidMode(string mode, string code) =>
        mode is "auto" or "stub" or "real"
            ? mode
            : throw new InvalidOperationException(
                $"Integration:Systems:{code}:Mode='{mode}' — допустимы auto|real|stub");

    /// <summary>
    /// Выбор адаптера для синхронизации. В режиме auto реальная система
    /// проверяется health-probe; при недоступности transparentно используется
    /// наш сервис-заместитель (fallback с фиксацией причины).
    /// </summary>
    public async Task<ResolvedAdapter> ResolveAsync(string code, CancellationToken ct = default)
    {
        Entry e;
        lock (_lock)
        {
            if (!_entries.TryGetValue(code, out var found))
                throw new DpException("ADAPTER_NOT_FOUND", 404,
                    $"Интеграционная система '{code}' не зарегистрирована (доступны: {string.Join(", ", _entries.Keys)})");
            e = found;
        }

        var source = e.ModeSource;

        if (e.Mode == "stub")
        {
            return new ResolvedAdapter(e.Stub, "stub", source, e.StubBaseUrl, "stub", false, null);
        }

        if (e.Mode == "real")
        {
            var probe = await ProbeAsync(e.RealBaseUrl, ct);
            RecordProbe(e, probe);
            if (!probe.Ok)
                throw new DpException(DpCodes.ProviderUnavailable, 503,
                    $"Система '{e.Code}' в явном режиме 'real', но endpoint {e.RealBaseUrl} недоступен ({probe.Detail}). " +
                    "Fallback в явном real не выполняется: используйте mode=auto или mode=stub.");
            return new ResolvedAdapter(e.Real, "real", source, e.RealBaseUrl, "real", false, null);
        }

        // --- auto: реальная система или наш заместитель --------------------
        if (string.IsNullOrWhiteSpace(e.RealBaseUrl))
        {
            // Реальная система не настроена вообще — работает заместитель.
            RecordFallback(e, "RealBaseUrl не настроен — работаем через собственный сервис-имитатор");
            return new ResolvedAdapter(e.Stub, "auto", "auto→fallback", e.StubBaseUrl, "stub", true,
                "RealBaseUrl не настроен");
        }

        var autoProbe = await ProbeAsync(e.RealBaseUrl, ct);
        RecordProbe(e, autoProbe);
        if (autoProbe.Ok)
        {
            _logger.LogInformation("AdapterSwitcher: {Code} — real доступен ({Url}, {Latency} мс) — синхронизация из реального контура",
                code, e.RealBaseUrl, autoProbe.LatencyMs);
            return new ResolvedAdapter(e.Real, "auto", "auto→real", e.RealBaseUrl, "real", false, null);
        }

        _logger.LogWarning(
            "AdapterSwitcher: {Code} — реальная система недоступна ({Detail}) — ЗАМЕЩАЕМ собственным сервисом-имитатором {Stub}",
            code, autoProbe.Detail, e.StubBaseUrl);
        RecordFallback(e, $"{autoProbe.Detail}");
        return new ResolvedAdapter(e.Stub, "auto", "auto→fallback", e.StubBaseUrl, "stub", true, autoProbe.Detail);
    }

    /// <summary>
    /// Свежие health-probe реальных систем по всем адаптерам (параллельно) —
    /// используется GET /adapters, чтобы админ видел текущее состояние.
    /// </summary>
    public async Task ProbeAllAsync(CancellationToken ct = default)
    {
        List<(Entry Entry, string Url)> snapshot;
        lock (_lock)
            snapshot = _entries.Values.Select(e => (e, e.RealBaseUrl)).ToList();

        var tasks = snapshot.Select(async pair =>
        {
            var probe = await ProbeAsync(pair.Url, ct);
            RecordProbe(pair.Entry, probe);
        }).ToArray();
        await Task.WhenAll(tasks);
    }

    private async Task<ProbeResult> ProbeAsync(string? baseUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return new ProbeResult(false, "RealBaseUrl не настроен", 0, DateTimeOffset.UtcNow);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);
            using var resp = await _probe.GetAsync(new Uri(new Uri(baseUrl), "/health"), cts.Token);
            sw.Stop();
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            var ok = (int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300;
            return new ProbeResult(ok,
                ok ? $"HTTP {(int)resp.StatusCode} (health ok)" : $"HTTP {(int)resp.StatusCode}: {Trunc(body)}",
                sw.ElapsedMilliseconds, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return new ProbeResult(false, $"тайм-аут {ProbeTimeout.TotalSeconds:0.#} с — endpoint не отвечает",
                sw.ElapsedMilliseconds, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProbeResult(false, Trunc(ex.Message), sw.ElapsedMilliseconds, DateTimeOffset.UtcNow);
        }
    }

    private static string Trunc(string s) => s.Length <= 120 ? s : s[..120] + "…";

    private void RecordProbe(Entry e, ProbeResult probe)
    {
        lock (_lock) e.LastRealProbe = probe;
    }

    private void RecordFallback(Entry e, string reason)
    {
        lock (_lock)
        {
            e.LastFallbackAt = DateTimeOffset.UtcNow;
            e.LastFallbackReason = reason;
        }
    }

    public AdapterInfo Info(string code)
    {
        lock (_lock)
        {
            var e = _entries[code];
            return new AdapterInfo(e.Code, e.DisplayName, e.Mode, e.ModeSource,
                e.RealBaseUrl, e.StubBaseUrl, e.LastRealProbe, e.LastFallbackAt, e.LastFallbackReason,
                e.LastEndpoint, e.LastSyncAt, e.LastSyncTotal);
        }
    }

    public List<AdapterInfo> ListAll()
    {
        lock (_lock) return _entries.Keys.Select(Info).ToList();
    }

    /// <summary>Горячее переключение режима — ядро демонстрации Strangler Fig.</summary>
    public AdapterInfo Switch(string code, string mode)
    {
        mode = ValidMode(mode, code);
        lock (_lock)
        {
            if (!_entries.TryGetValue(code, out var e))
                throw new DpException("ADAPTER_NOT_FOUND", 404,
                    $"Интеграционная система '{code}' не зарегистрирована (доступны: {string.Join(", ", _entries.Keys)})");
            var prev = e.Mode;
            e.Mode = mode;
            e.ModeSource = "runtime";
            _logger.LogInformation(
                "AdapterSwitcher: система '{Code}' — режим {Prev} → {Mode} (горячее переключение, без рестарта и изменения кода)",
                code, prev, mode);
            return Info(code);
        }
    }

    public void MarkSynced(string code, int total, string endpoint)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(code, out var e))
            {
                e.LastSyncAt = DateTimeOffset.UtcNow;
                e.LastSyncTotal = total;
                e.LastEndpoint = endpoint;
            }
        }
    }
}

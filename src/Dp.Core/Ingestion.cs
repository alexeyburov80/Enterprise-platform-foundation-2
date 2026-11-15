// ============================================================================
// Ingestion — синхронизация из внешних систем в logical store'ы через адаптер.
// Тянет нормализованные записи (одинаково для real и stub), делает upsert по
// детерминированным uuid5-ключам, пишет ext-namespace источника (onec/bitrix)
// и порождает события dp.data.changed.v1 через Outbox (см. StoreService).
// Режим (auto/real/stub + fallback) разрешает AdapterSwitcher — ingestion
// работает только с интерфейсом ISystemAdapter.
// ============================================================================

using System.Text.Json.Nodes;

namespace Dp.Core;

public sealed class IngestionService
{
    private readonly AdapterSwitcher _switcher;
    private readonly StoreService _store;
    private readonly SdRegistry _sd;
    private readonly ILogger<IngestionService> _logger;

    public IngestionService(AdapterSwitcher switcher, StoreService store, SdRegistry sd, ILogger<IngestionService> logger)
    {
        _switcher = switcher;
        _store = store;
        _sd = sd;
        _logger = logger;
    }

    public async Task<JsonObject> RunAsync(string code, CancellationToken ct = default)
    {
        var resolved = await _switcher.ResolveAsync(code, ct);
        var adapter = resolved.Adapter;
        var startedAt = DateTimeOffset.UtcNow;
        _logger.LogInformation(
            "Ingestion: система '{Code}' (режим {Mode}/{Source}, endpoint {Endpoint}{Fallback}) — начало синхронизации",
            code, resolved.Mode, resolved.ModeSource, resolved.Endpoint,
            resolved.Fallback ? " [FALLBACK: " + resolved.FallbackReason + "]" : "");

        var batches = await adapter.FetchAsync(ct);
        var results = new JsonArray();
        var totalCreated = 0;
        var totalUpdated = 0;

        foreach (var batch in batches)
        {
            var store = _sd.FindStore(batch.StoreId);
            var re = new ResolvedEntity(store, _sd.GetEntity(store, batch.Entity),
                DdlGenerator.StoreSchema(store), DdlGenerator.EntityTable(store, _sd.GetEntity(store, batch.Entity)));

            var created = 0;
            var updated = 0;
            foreach (var rec in batch.Records)
            {
                // связь «родитель → ребёнок» внутри store'а: orderId для OrderLine
                if (rec.ParentExternalId is not null)
                {
                    var parentEntity = batch.Entity switch
                    {
                        "OrderLine" => "Order",
                        _ => null
                    };
                    if (parentEntity is not null)
                        rec.Fields["orderId"] = _store.IdOf(batch.StoreId, parentEntity, rec.ParentExternalId);
                }

                var (wasCreated, rv) = await _store.UpsertInternalAsync(
                    re, rec.ExternalId, rec.Fields, batch.ExtNamespace, rec.ExtFields,
                    actor: $"sync:{adapter.Code}");
                if (wasCreated) created++; else updated++;
            }

            totalCreated += created;
            totalUpdated += updated;
            results.Add(new JsonObject
            {
                ["store"] = batch.StoreId,
                ["entity"] = batch.Entity,
                ["records"] = batch.Records.Count,
                ["created"] = created,
                ["updated"] = updated
            });
            _logger.LogInformation("Ingestion: {Store}/{Entity} — {Created} создано, {Updated} обновлено",
                batch.StoreId, batch.Entity, created, updated);
        }

        _switcher.MarkSynced(code, totalCreated + totalUpdated, resolved.Endpoint);

        return new JsonObject
        {
            ["system"] = code,
            ["adapter"] = adapter.DisplayName,
            ["mode"] = resolved.Mode,
            ["modeSource"] = resolved.ModeSource,
            ["endpoint"] = resolved.Endpoint,
            ["sourceTag"] = resolved.SourceTag,
            ["fallback"] = resolved.Fallback,
            ["fallbackReason"] = resolved.FallbackReason,
            ["startedAt"] = Rows.Iso(startedAt),
            ["durationMs"] = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
            ["created"] = totalCreated,
            ["updated"] = totalUpdated,
            ["batches"] = results
        };
    }
}

using Dp.Platform.Contracts.Adapters;
using Dp.Platform.Framework;
using System.Text.Json;

namespace Dp.Platform.Adapter.Bitrix;

/// <summary>
/// Профиль системы «Битрикс24»: документ deal, диалект — REST-вебхуки
/// (/rest/crm.deal.*.json). Новая система Bitrix = тот же шаблон профиля.
/// </summary>
public sealed class BitrixProfile : IAdapterProfile
{
    public string SystemName => "bitrix";
    public IReadOnlyList<string> DocumentTypes => ["deal"];
    public IDocumentTranslator Translator { get; } = new BitrixRestTranslator();
}

/// <summary>Перевод: платформа <-> REST Битрикс24 (SRP: только маппинг диалекта).</summary>
public sealed class BitrixRestTranslator : IDocumentTranslator
{
    public string ListPath(string type, string? sinceIso)
        => sinceIso is null
            ? "/rest/crm.deal.list.json"
            : $"/rest/crm.deal.list.json?filter[%3E=UPDATED_TIME]={Uri.EscapeDataString(sinceIso)}";

    public string CreatePath(string type) => "/rest/crm.deal.add.json";

    public IReadOnlyList<IntegrationDocument> ToPlatform(JsonElement upstreamBody, string type)
    {
        if (upstreamBody.ValueKind is not JsonValueKind.Object
            || !upstreamBody.TryGetProperty("result", out var result)
            || result.ValueKind is not JsonValueKind.Array)
            return [];

        return result.EnumerateArray()
            .Select(element => new IntegrationDocument
            {
                Id = element.TryGetProperty("ID", out var id) ? id.GetString() ?? "" : "",
                Type = type,
                UpdatedAt = element.TryGetProperty("UPDATED_TIME", out var updated)
                            && DateTimeOffset.TryParse(updated.GetString(), out var when)
                    ? when
                    : null,
                Data = element.Clone(),
            })
            .ToList();
    }

    public JsonElement ToUpstream(IntegrationDocument document)
    {
        // crm.deal.add.json ожидает { "fields": { ... } }.
        var payload = new Dictionary<string, JsonElement> { ["fields"] = document.Data };
        return JsonSerializer.SerializeToElement(payload);
    }
}

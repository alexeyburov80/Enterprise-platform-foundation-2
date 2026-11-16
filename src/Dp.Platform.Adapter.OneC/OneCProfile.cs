using Dp.Platform.Contracts.Adapters;
using Dp.Platform.Framework;
using System.Text.Json;

namespace Dp.Platform.Adapter.OneC;

/// <summary>
/// Профиль системы «1С»: документ sales-order, диалект — OData 4
/// (/odata/Standard.odata/Document_SalesOrder). Точка расширения:
/// новые типы документов добавляются в DocumentTypes.
/// </summary>
public sealed class OneCProfile : IAdapterProfile
{
    public string SystemName => "1c";
    public IReadOnlyList<string> DocumentTypes => ["sales-order"];
    public IDocumentTranslator Translator { get; } = new OneCOdataTranslator();
}

/// <summary>Перевод: платформа <-> OData 1С (SRP: только маппинг диалекта).</summary>
public sealed class OneCOdataTranslator : IDocumentTranslator
{
    public string ListPath(string type, string? sinceIso)
        => sinceIso is null
            ? "/odata/Standard.odata/Document_SalesOrder?$format=json"
            : $"/odata/Standard.odata/Document_SalesOrder?$format=json&$filter=Date gt datetime'{sinceIso}'";

    public string CreatePath(string type) => "/odata/Standard.odata/Document_SalesOrder";

    public IReadOnlyList<IntegrationDocument> ToPlatform(JsonElement upstreamBody, string type)
    {
        if (upstreamBody.ValueKind is not JsonValueKind.Object
            || !upstreamBody.TryGetProperty("value", out var value)
            || value.ValueKind is not JsonValueKind.Array)
            return [];

        return value.EnumerateArray()
            .Select(element => new IntegrationDocument
            {
                Id = element.TryGetProperty("Ref_Key", out var key) ? key.GetString() ?? "" : "",
                Type = type,
                UpdatedAt = element.TryGetProperty("Date", out var date) && DateTimeOffset.TryParse(date.GetString(), out var when)
                    ? when
                    : null,
                Data = element.Clone(),
            })
            .ToList();
    }

    public JsonElement ToUpstream(IntegrationDocument document)
        => document.Data; // тело 1C-объекта: Number, Date, Counterparty_Key, Amount и т.д.
}

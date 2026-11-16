namespace Dp.Platform.Contracts.Adapters;

using System.Text.Json;

/// <summary>
/// Универсальный контракт документа интеграции (платформа <-> адаптер).
/// Адаптер переводит его в диалект внешней системы (OData 1С / REST Битрикс).
/// Добавление новой системы = новый адаптер, реализующий тот же контракт.
/// </summary>
public sealed record IntegrationDocument
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public required JsonElement Data { get; init; }
}

/// <summary>Ответ адаптера: через какой upstream реально прошёл запрос.</summary>
public sealed record UpstreamRouteInfo(string System, string Mode, string Active, bool PrimaryHealthy);

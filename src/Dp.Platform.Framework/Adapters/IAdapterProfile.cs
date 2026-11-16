using Dp.Platform.Contracts.Adapters;
using System.Text.Json;

namespace Dp.Platform.Framework;

/// <summary>
/// Профиль интеграционного адаптера (SRP): имя системы, типы документов,
/// переводчик диалекта. Универсальный хост AdapterHost обслуживает любой профиль.
/// Новая интеграция = новый профиль + Program.cs на 8 строк.
/// </summary>
public interface IAdapterProfile
{
    /// <summary>Короткое имя системы: 1c, bitrix и т.п. (в пути /api/v1/int/{system}).</summary>
    string SystemName { get; }

    /// <summary>Типы документов, обслуживаемые адаптером, например "sales-order".</summary>
    IReadOnlyList<string> DocumentTypes { get; }

    IDocumentTranslator Translator { get; }
}

/// <summary>Перевод универсального контракта в диалект внешней системы и обратно.</summary>
public interface IDocumentTranslator
{
    /// <summary>Путь выборки документов во внешней системе (относительно её base URL).</summary>
    string ListPath(string type, string? sinceIso);

    /// <summary>Путь создания документа во внешней системе.</summary>
    string CreatePath(string type);

    /// <summary>Тело ответа внешней системы -> список универсальных документов.</summary>
    IReadOnlyList<IntegrationDocument> ToPlatform(JsonElement upstreamBody, string type);

    /// <summary>Универсальный документ -> тело запроса внешней системы.</summary>
    JsonElement ToUpstream(IntegrationDocument document);
}

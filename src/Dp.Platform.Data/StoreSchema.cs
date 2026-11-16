using Dp.Platform.Contracts.Schema;
using Dp.Platform.Framework;

namespace Dp.Platform.Data;

/// <summary>
/// Разрешённая схема logical store: определение реестра + физическое имя таблицы.
/// Data-сервис получает схему ТОЛЬКО через API реестра (нет прямого доступа
/// к чужой БД) и кеширует на короткий TTL (KISS).
/// </summary>
public sealed record StoreSchema
{
    public required string Store { get; init; }
    public required string Version { get; init; }
    public required string Table { get; init; }
    public required string KeyField { get; init; }
    public required IReadOnlyDictionary<string, FieldKind> Fields { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, FieldKind>> Extensions { get; init; }

    public static StoreSchema FromDefinition(StoreDefinition definition) => new()
    {
        Store = definition.Store,
        Version = definition.Version,
        Table = definition.Store.Replace('.', '_'),
        KeyField = definition.PrimaryKey,
        Fields = definition.Fields.ToDictionary(f => f.Name, f => FieldKindMapper.TryParse(f.Type, out var kind) ? kind : FieldKind.String),
        Extensions = (definition.Extensions ?? []).ToDictionary(
            e => e.Namespace,
            e => (IReadOnlyDictionary<string, FieldKind>)e.Fields.ToDictionary(
                f => f.Name, f => FieldKindMapper.TryParse(f.Type, out var kind) ? kind : FieldKind.String)),
    };

    /// <summary>"ns.field" -> (ns, field) или null для обычного поля.</summary>
    public (string Namespace, string Field, FieldKind Kind)? AsExtension(string name)
    {
        var dot = name.IndexOf('.');
        if (dot <= 0) return null;
        var ns = name[..dot];
        var field = name[(dot + 1)..];
        return Extensions.TryGetValue(ns, out var fields) && fields.TryGetValue(field, out var kind)
            ? (ns, field, kind)
            : null;
    }
}

/// <summary>Настройки реестра для Data-сервиса.</summary>
public sealed class RegistryOptions
{
    public const string SectionName = "Registry";
    public string BaseUrl { get; set; } = "http://localhost:8081";
}

/// <summary>Поставщик схем (DIP: Data-сервис зависит от интерфейса, не от HTTP).</summary>
public interface IStoreSchemaProvider
{
    Task<Result<StoreSchema>> GetAsync(string store, CancellationToken ct = default);
}

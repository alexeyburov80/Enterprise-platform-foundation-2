namespace Dp.Platform.Contracts.Schema;

/// <summary>
/// Канонические типы полей SD DSL v1 (Этап A: 16 типов). Универсальный
/// словарь соответствий PostgreSQL-типам — расширение типа = одна строка.
/// </summary>
public enum FieldKind
{
    String,
    Text,
    Uri,
    Uuid,
    Int,
    Long,
    Decimal,
    Bool,
    Date,
    Datetime,
    Time,
    Json,
    File,
    Enum,
    Array,
    Binary,
}

public static class FieldKindMapper
{
    /// <summary>DSN имён DSL -> FieldKind (регистронезависимо).</summary>
    public static readonly IReadOnlyDictionary<string, FieldKind> Names =
        new Dictionary<string, FieldKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["string"] = FieldKind.String,
            ["text"] = FieldKind.Text,
            ["uri"] = FieldKind.Uri,
            ["uuid"] = FieldKind.Uuid,
            ["int"] = FieldKind.Int,
            ["long"] = FieldKind.Long,
            ["decimal"] = FieldKind.Decimal,
            ["bool"] = FieldKind.Bool,
            ["date"] = FieldKind.Date,
            ["datetime"] = FieldKind.Datetime,
            ["time"] = FieldKind.Time,
            ["json"] = FieldKind.Json,
            ["file"] = FieldKind.File,
            ["enum"] = FieldKind.Enum,
            ["array"] = FieldKind.Array,
            ["binary"] = FieldKind.Binary,
        };

    /// <summary>Соответствие PostgreSQL (таблицы создаются провизионером Data-сервиса).</summary>
    public static readonly IReadOnlyDictionary<FieldKind, string> PgTypes =
        new Dictionary<FieldKind, string>
        {
            [FieldKind.String] = "text",
            [FieldKind.Text] = "text",
            [FieldKind.Uri] = "text",
            [FieldKind.Uuid] = "uuid",
            [FieldKind.Int] = "integer",
            [FieldKind.Long] = "bigint",
            [FieldKind.Decimal] = "numeric(38,10)",
            [FieldKind.Bool] = "boolean",
            [FieldKind.Date] = "date",
            [FieldKind.Datetime] = "timestamptz",
            [FieldKind.Time] = "time",
            [FieldKind.Json] = "jsonb",
            [FieldKind.File] = "uuid",
            [FieldKind.Enum] = "text",
            [FieldKind.Array] = "jsonb",
            [FieldKind.Binary] = "bytea",
        };

    public static bool TryParse(string name, out FieldKind kind) => Names.TryGetValue(name, out kind);

    public static string ToPgType(FieldKind kind) => PgTypes[kind];
}

/// <summary>Поле определения logical store (SD DSL v1).</summary>
public sealed record FieldDefinition
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public bool Required { get; init; }
    public bool Unique { get; init; }
    public IReadOnlyList<string>? EnumValues { get; init; }
    public string? Description { get; init; }
}

/// <summary>Расширение (Extension DTO, jsonb + namespace) — Этап A.</summary>
public sealed record ExtensionDefinition
{
    public required string Namespace { get; init; }
    public required IReadOnlyList<FieldDefinition> Fields { get; init; }
}

/// <summary>Определение logical store = единица версии (sales.orders@1.0.0).</summary>
public sealed record StoreDefinition
{
    public required string Store { get; init; }
    public required string Version { get; init; }
    public string? Title { get; init; }
    public required IReadOnlyList<FieldDefinition> Fields { get; init; }
    public required string PrimaryKey { get; init; }
    public IReadOnlyList<ExtensionDefinition>? Extensions { get; init; }
}

/// <summary>Зарегистрированная версия хранилища (ответ реестра).</summary>
public sealed record StoreVersionInfo
{
    public required string Store { get; init; }
    public required string Version { get; init; }
    public required StoreDefinition Definition { get; init; }
    public DateTimeOffset RegisteredAt { get; init; }
    public string Status { get; init; } = "active";
}

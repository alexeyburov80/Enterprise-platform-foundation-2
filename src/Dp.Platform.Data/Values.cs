using System.Text.Json;
using Dp.Platform.Contracts.Data;
using Dp.Platform.Contracts.Schema;
using Dp.Platform.Framework;
using Npgsql;
using NpgsqlTypes;

namespace Dp.Platform.Data;

/// <summary>Копилка параметров SQL (SRP: безопасная подстановка значений).</summary>
public sealed class ParameterSink
{
    private readonly List<NpgsqlParameter> _parameters = [];
    private int _index;

    public IReadOnlyList<NpgsqlParameter> Parameters => _parameters;

    public void AddRange(IEnumerable<NpgsqlParameter> items) => _parameters.AddRange(items);

    public string Add(object? value, NpgsqlDbType type)
    {
        var name = $"p{_index++}";
        _parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
        return "@" + name;
    }
}

/// <summary>
/// Преобразование значений DSL/JSON в типы PostgreSQL (SRP, DRY: используется
/// фильтрами, записями и ключами). Единственное место знания о типах.
/// </summary>
public static class Values
{
    /// <summary>Приведение колонки (в т.ч. ext_ns->>'f') к типу DSL для сравнения.</summary>
    public static string CastSuffix(FieldKind kind) => kind switch
    {
        FieldKind.Int or FieldKind.Long or FieldKind.Decimal => "::numeric",
        FieldKind.Uuid or FieldKind.File => "::uuid",
        FieldKind.Bool => "::boolean",
        FieldKind.Date => "::date",
        FieldKind.Datetime => "::timestamptz",
        FieldKind.Time => "::time",
        FieldKind.Json or FieldKind.Array => "::text",
        _ => "",
    };

    /// <summary>NpgsqlDbType для поля (единственный словарь соответствий).</summary>
    public static NpgsqlDbType ToNpgsqlType(FieldKind kind) => kind switch
    {
        FieldKind.Int => NpgsqlDbType.Integer,
        FieldKind.Long => NpgsqlDbType.Bigint,
        FieldKind.Decimal => NpgsqlDbType.Numeric,
        FieldKind.Bool => NpgsqlDbType.Boolean,
        FieldKind.Date => NpgsqlDbType.Date,
        FieldKind.Datetime => NpgsqlDbType.TimestampTz,
        FieldKind.Time => NpgsqlDbType.Time,
        FieldKind.Uuid or FieldKind.File => NpgsqlDbType.Uuid,
        FieldKind.Json or FieldKind.Array => NpgsqlDbType.Jsonb,
        FieldKind.Binary => NpgsqlDbType.Bytea,
        _ => NpgsqlDbType.Text,
    };

    /// <summary>SQL-выражение колонки по имени поля (обычное или ns.field).</summary>
    public static (string Sql, FieldKind Kind) ColumnExpr(StoreSchema schema, string name)
    {
        if (schema.Fields.TryGetValue(name, out var kind))
            return ($"\"{name}\"", kind);

        if (schema.AsExtension(name) is { } ext)
            return ($"(\"ext_{ext.Namespace}\"->>'{ext.Field}')", ext.Kind);

        throw new DpFaultException(DpCodes.InvalidQuery,
            $"Unknown field '{name}'. Check '{schema.Store}@{schema.Version}' definition (extensions use 'ns.field').");
    }

    /// <summary>Ключ из HTTP-маршрута (строка) -> типизированное значение.</summary>
    public static object? CoerceKey(string raw, FieldKind kind) => kind switch
    {
        FieldKind.Uuid or FieldKind.File => Guid.Parse(raw),
        FieldKind.Int => int.Parse(raw),
        FieldKind.Long => long.Parse(raw),
        FieldKind.Decimal => decimal.Parse(raw),
        FieldKind.Bool => bool.Parse(raw),
        FieldKind.Date => DateOnly.Parse(raw),
        FieldKind.Datetime => AsUtc(raw),
        FieldKind.Time => TimeOnly.Parse(raw),
        _ => raw,
    };

    /// <summary>Параметр условия с приведением к типу поля.</summary>
    public static string ToParam(ParameterSink sink, JsonElementSafe? value, FieldKind kind)
    {
        if (value is null || value.IsNull) return sink.Add(null, NpgsqlDbType.Text);

        var raw = value.StringValue ?? value.NumberValue?.ToString() ?? value.BoolValue?.ToString();
        return kind switch
        {
            FieldKind.Uuid or FieldKind.File => sink.Add(Guid.Parse(raw!), NpgsqlDbType.Uuid),
            FieldKind.Int => sink.Add(AsInt(value), NpgsqlDbType.Integer),
            FieldKind.Long => sink.Add((long)AsDecimal(value), NpgsqlDbType.Bigint),
            FieldKind.Decimal => sink.Add(AsDecimal(value), NpgsqlDbType.Numeric),
            FieldKind.Bool => sink.Add(value.BoolValue ?? bool.Parse(raw!), NpgsqlDbType.Boolean),
            FieldKind.Date => sink.Add(DateOnly.Parse(raw!), NpgsqlDbType.Date),
            FieldKind.Datetime => sink.Add(AsUtc(raw!), NpgsqlDbType.TimestampTz),
            FieldKind.Time => sink.Add(TimeOnly.Parse(raw!), NpgsqlDbType.Time),
            FieldKind.Binary => sink.Add(Convert.FromBase64String(raw ?? ""), NpgsqlDbType.Bytea),
            _ => sink.Add(raw, NpgsqlDbType.Text),
        };
    }

    /// <summary>Значение записи из JSON-тела -> .NET-значение типизированной колонки.</summary>
    public static object? FromJson(JsonElement element, FieldKind kind)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return kind switch
        {
            FieldKind.Int => element.GetInt32(),
            FieldKind.Long => element.GetInt64(),
            FieldKind.Decimal => element.GetDecimal(),
            FieldKind.Bool => element.GetBoolean(),
            FieldKind.Date => DateOnly.FromDateTime(element.GetDateTime()),
            FieldKind.Datetime => AsUtc(element.GetString() ?? element.GetDateTime().ToString("O")),
            FieldKind.Time => TimeOnly.FromDateTime(element.GetDateTime()),
            FieldKind.Uuid or FieldKind.File => element.GetGuid(),
            FieldKind.Binary => Convert.FromBase64String(element.GetString() ?? ""),
            FieldKind.Json or FieldKind.Array => element.Clone(),
            _ => element.GetString(),
        };
    }

    private static int AsInt(JsonElementSafe value)
        => value.NumberValue is { } n ? (int)Math.Round(n) : int.Parse(value.StringValue!);

    private static decimal AsDecimal(JsonElementSafe value)
        => value.NumberValue is { } n ? (decimal)n : decimal.Parse(value.StringValue!);

    private static DateTime AsUtc(string raw)
    {
        var parsed = DateTime.Parse(raw);
        return parsed.Kind == DateTimeKind.Utc ? parsed : DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    /// <summary>Значение записи (JsonElement или null) -> JsonElement для конвертации.</summary>
    public static JsonElement AsElement(object? raw)
        => raw is JsonElement element ? element : default;
}

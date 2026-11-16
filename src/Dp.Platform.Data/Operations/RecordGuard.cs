using System.Collections.Immutable;
using System.Text.Json;
using Dp.Platform.Framework;

namespace Dp.Platform.Data;

/// <summary>
/// Страж валидации записи (DRY: общие правила для insert/replace/upsert/patch).
/// MVP: обязательным является только ключ; флаг Required DSL проверяется
/// с Этапа B (точка расширения в TableProvisioner + здесь).
/// </summary>
public static class RecordGuard
{
    /// <summary>JSON-объект тела -> словарь значений (JsonElement/null).</summary>
    public static Dictionary<string, object?> Parse(JsonElement body)
    {
        if (body.ValueKind is not JsonValueKind.Object)
            throw new DpFaultException(DpCodes.Validation, "Body must be a JSON object of field values.");

        return body.EnumerateObject().ToDictionary(
            property => property.Name,
            property => (object?)property.Value.Clone());
    }

    /// <summary>Проверка имён: только поля схемы и ns.field расширений.</summary>
    public static void ValidateNames(StoreSchema schema, Dictionary<string, object?> record)
    {
        var unknown = record.Keys
            .Where(name => !schema.Fields.ContainsKey(name) && schema.AsExtension(name) is null)
            .ToImmutableArray();

        if (unknown.Length > 0)
            throw new DpFaultException(DpCodes.Validation,
                "Unknown fields. Plain fields and 'ns.field' extension fields only.",
                new { unknown, known = schema.Fields.Keys.Concat(schema.Extensions.Keys.Select(ns => ns + ".*")) });
    }

    /// <summary>Ключ обязателен и не null для создающих операций.</summary>
    public static void ValidateKeyPresent(StoreSchema schema, Dictionary<string, object?> record)
    {
        if (!record.TryGetValue(schema.KeyField, out var raw)
            || raw is not JsonElement element
            || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new DpFaultException(DpCodes.Validation, $"Key field '{schema.KeyField}' is required.");
    }

    /// <summary>Ключ в теле должен совпадать с ключом маршрута (защита от рассинхрона).</summary>
    public static void ValidateKeyMatch(StoreSchema schema, Dictionary<string, object?> record, string? routeKey)
    {
        if (routeKey is null || !record.TryGetValue(schema.KeyField, out var raw) || raw is not JsonElement element)
            return;
        var bodyKey = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        if (bodyKey is not null && !string.Equals(bodyKey, routeKey, StringComparison.OrdinalIgnoreCase))
            throw new DpFaultException(DpCodes.Validation,
                $"Key in body ({bodyKey}) does not match key in route ({routeKey}).");
    }
}

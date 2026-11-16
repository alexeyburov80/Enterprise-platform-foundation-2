using System.Text.Json;
using Dp.Platform.Contracts.Schema;

namespace Dp.Platform.Data;

/// <summary>
/// Значения полей расширений (ns.field) сгруппированы в jsonb-объекты
/// namespace-ов (SRP: только расширения, не базовые колонки).
/// </summary>
public static class RecordExtensions
{
    /// <summary>ns -> jsonb-объект. При replace=false отсутствующие ns не возвращаются.</summary>
    public static Dictionary<string, JsonElement> GroupByNamespace(
        StoreSchema schema, Dictionary<string, object?> fields, bool replace)
    {
        var result = new Dictionary<string, JsonElement>();
        foreach (var ns in schema.Extensions.Keys)
        {
            var members = fields
                .Where(kv => kv.Key.StartsWith(ns + '.', StringComparison.Ordinal))
                .Select(kv =>
                {
                    var field = kv.Key[(ns.Length + 1)..];
                    var kind = schema.Extensions[ns].GetValueOrDefault(field, FieldKind.Json);
                    var typed = Values.FromJson(Values.AsElement(kv.Value), kind);
                    return (field, element: JsonSerializer.SerializeToElement(typed));
                })
                .ToList();

            if (members.Count == 0 && !replace) continue;

            var obj = new Dictionary<string, JsonElement?>();
            foreach (var (field, element) in members) obj[field] = element;
            result[ns] = JsonSerializer.SerializeToElement(obj);
        }
        return result;
    }
}

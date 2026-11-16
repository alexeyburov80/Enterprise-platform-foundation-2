using Dp.Platform.Contracts.Data;
using Dp.Platform.Contracts.Schema;
using Dp.Platform.Framework;
using Npgsql;
using NpgsqlTypes;

namespace Dp.Platform.Data;

/// <summary>
/// Построители SQL для записей (SRP: только текст SQL и параметры).
/// Идентификаторы приходят из валидированной схемы, значения — ParameterSink.
/// </summary>
public static class RecordSqlBuilder
{
    public static (List<string> Columns, List<string> Params) ColumnsForInsert(
        StoreSchema schema, Dictionary<string, object?> record, ParameterSink sink)
    {
        var columns = new List<string>();
        var parameters = new List<string>();
        foreach (var (name, kind) in schema.Fields)
        {
            if (!record.TryGetValue(name, out var raw)) continue;
            columns.Add($"\"{name}\"");
            parameters.Add(sink.Add(Values.FromJson(Values.AsElement(raw), kind), Values.ToNpgsqlType(kind)));
        }
        foreach (var (ns, json) in RecordExtensions.GroupByNamespace(schema, record, replace: true))
        {
            columns.Add($"\"ext_{ns}\"");
            parameters.Add(sink.Add(json, NpgsqlDbType.Jsonb));
        }
        return (columns, parameters);
    }

    public static List<string> Assignments(
        StoreSchema schema, Dictionary<string, object?> fields, bool replace, ParameterSink sink)
    {
        var assignments = new List<string>();
        foreach (var (name, kind) in schema.Fields)
        {
            if (name == schema.KeyField) continue;
            if (!fields.TryGetValue(name, out var raw))
            {
                if (!replace) continue;
                raw = null; // replace: отсутствующее поле обнуляется
            }
            var value = Values.FromJson(Values.AsElement(raw), kind);
            assignments.Add($"\"{name}\" = {sink.Add(value, Values.ToNpgsqlType(kind))}");
        }
        foreach (var (ns, json) in RecordExtensions.GroupByNamespace(schema, fields, replace))
        {
            assignments.Add(
                replace
                    ? $"\"ext_{ns}\" = {sink.Add(json, NpgsqlDbType.Jsonb)}"
                    : $"\"ext_{ns}\" = COALESCE(\"ext_{ns}\", '{{}}'::jsonb) || {sink.Add(json, NpgsqlDbType.Jsonb)}");
        }
        return assignments;
    }

    public static string BuildOrder(StoreSchema schema, IReadOnlyList<SortSpec>? sort)
    {
        if (sort is null || sort.Count == 0) return "";
        var parts = sort.Select(spec =>
        {
            if (!schema.Fields.ContainsKey(spec.Field))
                throw new DpFaultException(DpCodes.InvalidQuery,
                    $"Sorting by '{spec.Field}' is not allowed: unknown field (extensions are not sortable in MVP).");
            return $"\"{spec.Field}\" {(spec.Descending ? "DESC" : "ASC")}";
        });
        return "ORDER BY " + string.Join(", ", parts);
    }

    public static string SelectByKey(StoreSchema schema, ParameterSink sink, string key)
        => $"SELECT to_jsonb(x.*) FROM dp_data.\"{schema.Table}\" AS x {WhereByKey(schema, sink, key)}";

    public static string WhereClause(SqlFilterTranslator.Translation filter)
        => filter.Where.Length > 0 ? "WHERE " + filter.Where : "";

    public static string WhereByKey(StoreSchema schema, ParameterSink sink, string key)
    {
        var kind = schema.Fields.GetValueOrDefault(schema.KeyField, FieldKind.String);
        var param = sink.Add(Values.CoerceKey(key, kind), Values.ToNpgsqlType(kind));
        return $"WHERE \"{schema.KeyField}\" = {param}";
    }
}

using Dp.Platform.Contracts.Data;
using Dp.Platform.Contracts.Schema;
using Dp.Platform.Framework;
using Npgsql;

namespace Dp.Platform.Data;

/// <summary>
/// Транслятор Query DSL -> параметризованный SQL (SRP). Единый словарь
/// операторов — добавление оператора = одна строка switch (OCP). Идентификаторы
/// колонок берутся только из схемы реестра, значения всегда параметрами.
/// </summary>
public sealed class SqlFilterTranslator
{
    public sealed record Translation(string Where, IReadOnlyList<NpgsqlParameter> Parameters);

    public Translation Translate(StoreSchema schema, FilterNode? filter)
    {
        var sink = new ParameterSink();
        var where = filter is null ? "" : Build(sink, schema, filter);
        return new Translation(where, sink.Parameters);
    }

    private static string Build(ParameterSink sink, StoreSchema schema, FilterNode node)
    {
        if (node.And is { Count: > 0 })
            return "(" + string.Join(" AND ", node.And.Select(n => Build(sink, schema, n))) + ")";
        if (node.Or is { Count: > 0 })
            return "(" + string.Join(" OR ", node.Or.Select(n => Build(sink, schema, n))) + ")";
        if (node.Not is not null)
            return "NOT " + Build(sink, schema, node.Not);
        return BuildAtom(sink, schema, node);
    }

    private static string BuildAtom(ParameterSink sink, StoreSchema schema, FilterNode node)
    {
        if (node.Field is null || node.Op is null)
            throw new DpFaultException(DpCodes.InvalidQuery, "Filter atom requires 'field' and 'op'.");

        var (column, kind) = Values.ColumnExpr(schema, node.Field);
        column += Values.CastSuffix(kind);
        var op = node.Op.ToLowerInvariant();

        // null-сравнения не требуют параметров.
        if (node.AtomValue is null or { IsNull: true })
        {
            if (op is "eq" or "isnull") return $"({column} IS NULL)";
            if (op is "ne" or "notnull") return $"({column} IS NOT NULL)";
        }

        return op switch
        {
            "eq" => $"{column} = {Values.ToParam(sink, node.AtomValue, kind)}",
            "ne" => $"{column} <> {Values.ToParam(sink, node.AtomValue, kind)}",
            "gt" => $"{column} > {Values.ToParam(sink, node.AtomValue, kind)}",
            "ge" or "gte" => $"{column} >= {Values.ToParam(sink, node.AtomValue, kind)}",
            "lt" => $"{column} < {Values.ToParam(sink, node.AtomValue, kind)}",
            "le" or "lte" => $"{column} <= {Values.ToParam(sink, node.AtomValue, kind)}",
            "like" => $"{column} ILIKE {Values.ToParam(sink, node.AtomValue, kind)}",
            "isnull" => $"({column} IS NULL)",
            "notnull" => $"({column} IS NOT NULL)",
            "in" or "nin" => BuildIn(sink, node, column, kind, op == "nin"),
            _ => throw new DpFaultException(DpCodes.InvalidQuery, $"Unknown filter operator '{node.Op}'."),
        };
    }

    private static string BuildIn(ParameterSink sink, FilterNode node, string column, FieldKind kind, bool negate)
    {
        var items = node.AtomValue?.ListValue;
        if (items is null || items.Count == 0)
            throw new DpFaultException(DpCodes.InvalidQuery, $"Operator '{node.Op}' requires non-empty list value.");

        var clauses = items.Select(item => $"{column} = {Values.ToParam(sink, item, kind)}");
        return negate
            ? $"NOT ({string.Join(" OR ", clauses)})"
            : "(" + string.Join(" OR ", clauses) + ")";
    }
}

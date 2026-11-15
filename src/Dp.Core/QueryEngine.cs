// ============================================================================
// Query Engine — декларативный Query DSL v1 (таблица 14) → параметризованный SQL.
// Произвольный SQL и вычисляемый код запрещены (инвариант платформы): клиент
// передаёт только дерево операторов, значения строго типизируются по SD.
// Вложенность where — до трёх уровней; сортировка — по колонкам from-сущности.
// ============================================================================

using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Dp.Core;

public static class QueryEngine
{
    private static readonly string[] SupportedOperators =
    {
        "eq", "ne", "gt", "gte", "lt", "lte", "in", "notIn",
        "like", "isNull", "isNotNull", "and", "or", "not"
    };

    public static string[] Operators => SupportedOperators;

    public static async Task<JsonObject> QueryAsync(NpgsqlDataSource db, ResolvedEntity re, JsonObject body)
    {
        var (whereSql, parameters) = CompileWhere(re, body);

        // from
        if (body.TryGetPropertyValue("from", out var fromNode))
        {
            var from = (string?)fromNode;
            if (!string.Equals(from, re.Entity.Name, StringComparison.OrdinalIgnoreCase))
                throw new DpException(DpCodes.DslSyntaxInvalid, 400,
                    $"Query DSL: from='{from}' не совпадает с сущностью маршрута '{re.Entity.Name}' (MVP: joinSpec не поддерживается — DP-1009)");
        }

        // select
        var selects = CompileSelect(re, body);

        // orderBy — только колонки from-сущности (обяз. 7.2)
        var orderSql = "";
        if (body.TryGetPropertyValue("orderBy", out var obNode) && obNode is JsonArray obArr && obArr.Count > 0)
        {
            var parts = new List<string>();
            foreach (var item in obArr.OfType<JsonObject>())
            {
                var field = (string?)item["field"] ?? throw new DpException(DpCodes.DslSyntaxInvalid, 400, "orderBy: отсутствует field");
                var dir = ((string?)item["dir"] ?? "asc").ToLowerInvariant() == "desc" ? "DESC" : "ASC";
                var f = re.Entity.Fields.FirstOrDefault(x => string.Equals(x.Name, field, StringComparison.OrdinalIgnoreCase))
                    ?? throw new DpException(DpCodes.DslSyntaxInvalid, 400, $"orderBy: поле '{field}' отсутствует в сущности '{re.Entity.Name}'");
                parts.Add($"{DdlGenerator.Column(f)} {dir}");
            }
            if (parts.Count > 0) orderSql = " ORDER BY " + string.Join(", ", parts);
        }

        var limit = 100;
        if (body.TryGetPropertyValue("limit", out var limNode) && limNode is JsonValue limVal)
            limit = (int?)limVal ?? 100;
        if (limit is < 1 or > 1000)
            throw new DpException(DpCodes.DslSyntaxInvalid, 400, "Query DSL: limit должен быть 1..1000 (см. GetCapabilities)");
        var offset = 0;
        if (body.TryGetPropertyValue("offset", out var offNode) && offNode is JsonValue offVal)
            offset = (int?)offVal ?? 0;
        if (offset < 0)
            throw new DpException(DpCodes.DslSyntaxInvalid, 400, "Query DSL: offset >= 0");

        // deleted-фильтр
        var deletedFilter = re.Entity.Lifecycle.SoftDelete ? " AND deleted_at IS NULL" : "";

        var sql = $"SELECT {string.Join(", ", selects.Select(s => s.Sql))} FROM {re.Schema}.{re.Table}" +
                  $" WHERE 1=1{deletedFilter}{(whereSql.Length > 0 ? " AND " + whereSql : "")}{orderSql}" +
                  $" LIMIT @__limit OFFSET @__offset";

        await using var conn = await db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var prm in parameters) cmd.Parameters.Add(prm);
        cmd.Parameters.AddWithValue("__limit", limit + 1);
        cmd.Parameters.AddWithValue("__offset", offset);

        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new JsonArray();
        var count = 0;
        var hasMore = false;
        while (await r.ReadAsync())
        {
            if (count == limit) { hasMore = true; break; }
            var row = new JsonObject();
            foreach (var s in selects)
                row[s.Name] = r.IsDBNull(r.GetOrdinal(s.Alias)) ? null : ReadValue(r, s, r.GetOrdinal(s.Alias));
            rows.Add(row);
            count++;
        }

        return new JsonObject
        {
            ["storeId"] = re.Store.StoreId,
            ["version"] = re.Store.Metadata.Version,
            ["entity"] = re.Entity.Name,
            ["rows"] = rows,
            ["count"] = count,
            ["hasMore"] = hasMore,
            ["limit"] = limit,
            ["offset"] = offset
        };
    }

    // ------------------------------------------------------------------ select

    private sealed record SelectItem(string Name, string Alias, string Sql, CanonType Type, bool IsExt);

    private static List<SelectItem> CompileSelect(ResolvedEntity re, JsonObject body)
    {
        var items = new List<SelectItem>();
        var aliases = new HashSet<string>(StringComparer.Ordinal);

        void Add(string name, string sql, CanonType type, bool isExt)
        {
            var alias = "__c" + items.Count;
            if (!aliases.Add(alias)) return;
            items.Add(new SelectItem(name, alias, $"{sql} AS \"{alias}\"", type, isExt));
        }

        if (body.TryGetPropertyValue("select", out var selNode) && selNode is JsonArray selArr && selArr.Count > 0)
        {
            foreach (var s in selArr.OfType<JsonValue>())
            {
                var name = (string?)s ?? throw new DpException(DpCodes.DslSyntaxInvalid, 400, "select: элементы должны быть строками");
                if (string.Equals(name, "ext", StringComparison.Ordinal))
                {
                    Add("ext", "ext", CanonType.Json, false);
                    continue;
                }
                var f = re.Entity.Fields.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (f is not null) { Add(f.Name, DdlGenerator.Column(f), f.Canon, false); continue; }

                if (name.Contains('.'))
                {
                    var (ns, key) = SplitPath(name);
                    Add(name, $"ext->'{ns}'->>'{key}'", CanonType.String, true);
                    continue;
                }
                throw new DpException(DpCodes.DslSyntaxInvalid, 400,
                    $"select: поле '{name}' отсутствует в сущности '{re.Entity.Name}' (ext-поля адресуются путём namespace.field)");
            }
        }
        else
        {
            foreach (var f in re.Entity.Fields) Add(f.Name, DdlGenerator.Column(f), f.Canon, false);
        }

        return items;
    }

    private static JsonNode? ReadValue(NpgsqlDataReader r, SelectItem s, int ord) =>
        s.IsExt
            ? JsonValue.Create(r.GetString(ord))
            : Rows.ValueToJsonNode(s.Type, ReadTyped(r, s.Type, ord));

    private static object? ReadTyped(NpgsqlDataReader r, CanonType t, int ord) => t switch
    {
        CanonType.Uuid or CanonType.Ref or CanonType.FileRef => r.GetGuid(ord),
        CanonType.String or CanonType.Text or CanonType.Enum => r.GetString(ord),
        CanonType.Boolean => r.GetBoolean(ord),
        CanonType.Int32 => r.GetInt32(ord),
        CanonType.Int64 => r.GetInt64(ord),
        CanonType.Decimal or CanonType.Money => r.GetDecimal(ord),
        CanonType.Date => DateOnly.FromDateTime((DateTime)r.GetValue(ord)),
        CanonType.Time => TimeOnly.FromTimeSpan((TimeSpan)r.GetValue(ord)),
        CanonType.Timestamp => Rows.AsDto(r.GetValue(ord)),
        CanonType.Json => r.GetString(ord),
        CanonType.Binary => (byte[])r.GetValue(ord),
        _ => r.GetValue(ord)
    };

    // ------------------------------------------------------------------ where

    private static (string Sql, List<NpgsqlParameter> Params) CompileWhere(ResolvedEntity re, JsonObject body)
    {
        var parameters = new List<NpgsqlParameter>();
        if (!body.TryGetPropertyValue("where", out var whereNode) || whereNode is null)
            return ("", parameters);

        var sql = CompileNode(re, whereNode, parameters, depth: 1);
        return (sql, parameters);
    }

    private static string CompileNode(ResolvedEntity re, JsonNode node, List<NpgsqlParameter> parameters, int depth)
    {
        if (depth > 3)
            throw new DpException(DpCodes.DslSyntaxInvalid, 400, "Query DSL: вложенность where — до трёх уровней (обяз. 7.2)");
        if (node is not JsonObject obj || obj.Count != 1)
            throw new DpException(DpCodes.DslSyntaxInvalid, 400, "Query DSL: узел where — объект с одним оператором");

        var (op, inner) = obj.First();
        switch (op)
        {
            case "and":
            case "or":
            {
                var joiner = op == "and" ? " AND " : " OR ";
                if (inner is not JsonArray arr || arr.Count is < 2 or > 16)
                    throw new DpException(DpCodes.DslSyntaxInvalid, 400, $"Query DSL: {op} принимает массив узлов (2–16)");
                var parts = arr.Select(n => CompileNode(re, n!, parameters, depth + 1)).ToList();
                return "(" + string.Join(joiner, parts) + ")";
            }
            case "not":
                return "NOT " + CompileNode(re, inner!, parameters, depth + 1);

            case "isNull":
            case "isNotNull":
            {
                var col = ColumnOf(re, inner as JsonObject, op);
                return $"{col} {(op == "isNull" ? "IS NULL" : "IS NOT NULL")}";
            }

            case "eq": case "ne": case "gt": case "gte": case "lt": case "lte":
            {
                var (field, value) = BinaryArgs(re, inner as JsonObject, op);
                var (col, prm) = ParamFor(re, field, value, parameters);
                var sym = op switch { "eq" => "=", "ne" => "<>", "gt" => ">", "gte" => ">=", "lt" => "<", _ => "<=" };
                return $"{col} {sym} {prm}";
            }

            case "in": case "notIn":
            {
                if (inner is not JsonObject io)
                    throw new DpException(DpCodes.DslSyntaxInvalid, 400, $"Query DSL: {op} требует {{field, values[]}}");
                var field = (string?)io["field"] ?? throw new DpException(DpCodes.DslSyntaxInvalid, 400, $"Query DSL: {op} требует field");
                if (io["values"] is not JsonArray vals || vals.Count == 0 || vals.Count > 1000)
                    throw new DpException(DpCodes.DslSyntaxInvalid, 400, $"Query DSL: {op} — массив 1..1000 значений");

                var f = FieldOf(re, field);
                if (f.Canon is CanonType.Json or CanonType.Binary)
                    throw new DpException(DpCodes.DslOperatorUnsupported, 400, $"Query DSL: {op} по типу {f.Type} не поддержан в MVP");
                var converted = vals.Select(v => FieldValidator.ValidateValue(f, v!, "query")).Cast<object>().ToArray();
                var typedArray = BuildTypedArray(f.Canon, converted);
                var prmName = "@p" + parameters.Count;
                var prm = new NpgsqlParameter(prmName, typedArray);
                parameters.Add(prm);
                var col = DdlGenerator.Column(f);
                return op == "in"
                    ? $"{col} = ANY({prmName})"
                    : $"NOT ({col} = ANY({prmName}))";
            }

            case "like":
            {
                if (inner is not JsonObject lo)
                    throw new DpException(DpCodes.DslSyntaxInvalid, 400, "Query DSL: like требует {field, pattern, escape?}");
                var field = (string?)lo["field"] ?? throw new DpException(DpCodes.DslSyntaxInvalid, 400, "like: отсутствует field");
                var pattern = (string?)lo["pattern"] ?? throw new DpException(DpCodes.DslSyntaxInvalid, 400, "like: отсутствует pattern");
                var escape = (string?)lo["escape"] ?? "\\";
                if (escape.Length != 1)
                    throw new DpException(DpCodes.DslSyntaxInvalid, 400, "like: escape — одиночный символ");

                var f = FieldOf(re, field);
                if (f.Canon is not (CanonType.String or CanonType.Text or CanonType.Enum))
                    throw new DpException(DpCodes.DslOperatorUnsupported, 400, $"Query DSL: like поддержан только по string/text/enum (поле '{field}': {f.Type})");

                var prmName = "@p" + parameters.Count;
                var prm = new NpgsqlParameter(prmName, pattern);
                parameters.Add(prm);
                return $"{DdlGenerator.Column(f)} LIKE {prmName} ESCAPE '{escape}'";
            }

            default:
                throw new DpException(DpCodes.DslOperatorUnsupported, 400,
                    $"Query DSL: оператор '{op}' не входит в capabilities store'а (поддержаны: {string.Join(", ", SupportedOperators)})");
        }
    }

    private static string ColumnOf(ResolvedEntity re, JsonObject? spec, string op)
    {
        var field = (string?)spec?["field"] ?? throw new DpException(DpCodes.DslSyntaxInvalid, 400, $"{op}: отсутствует field");
        return DdlGenerator.Column(FieldOf(re, field));
    }

    private static (string Field, JsonNode Value) BinaryArgs(ResolvedEntity re, JsonObject? spec, string op)
    {
        if (spec is null)
            throw new DpException(DpCodes.DslSyntaxInvalid, 400, $"Query DSL: {op} требует {{field, value}}");
        var field = (string?)spec["field"] ?? throw new DpException(DpCodes.DslSyntaxInvalid, 400, $"{op}: отсутствует field");
        var value = spec["value"] ?? throw new DpException(DpCodes.DslSyntaxInvalid, 400, $"{op}: отсутствует value");
        return (field, value);
    }

    private static (string Col, string Prm) ParamFor(ResolvedEntity re, string field, JsonNode value,
        List<NpgsqlParameter> parameters)
    {
        var f = FieldOf(re, field);
        if (f.Canon is CanonType.Json or CanonType.Binary)
            throw new DpException(DpCodes.DslOperatorUnsupported, 400, $"Query DSL: сравнение по типу {f.Type} не поддержано в MVP (поле '{field}')");
        if (value is null)
            throw new DpException(DpCodes.DslSyntaxInvalid, 400, $"Query DSL: для сравнения с null используйте isNull/isNotNull (поле '{field}')");

        var typed = FieldValidator.ValidateValue(f, value, "query");
        var prmName = "@p" + parameters.Count;
        var prm = new NpgsqlParameter(prmName, typed);
        parameters.Add(prm);
        return (DdlGenerator.Column(f), prmName);
    }

    private static SdField FieldOf(ResolvedEntity re, string field)
    {
        var f = re.Entity.Fields.FirstOrDefault(x => string.Equals(x.Name, field, StringComparison.OrdinalIgnoreCase))
            ?? throw new DpException(DpCodes.DslSyntaxInvalid, 400,
                $"Query DSL: поле '{field}' отсутствует в сущности '{re.Entity.Name}' (ext-поля в where MVP не поддержаны — DP-1009)");
        return f;
    }

    private static object BuildTypedArray(CanonType t, object[] values) => t switch
    {
        CanonType.Uuid or CanonType.Ref or CanonType.FileRef => values.Cast<Guid>().ToArray(),
        CanonType.String or CanonType.Text or CanonType.Enum => values.Cast<string>().ToArray(),
        CanonType.Boolean => values.Cast<bool>().ToArray(),
        CanonType.Int32 => values.Cast<int>().ToArray(),
        CanonType.Int64 => values.Cast<long>().ToArray(),
        CanonType.Decimal or CanonType.Money => values.Cast<decimal>().ToArray(),
        CanonType.Date => values.Cast<DateOnly>().ToArray(),
        CanonType.Time => values.Cast<TimeOnly>().ToArray(),
        CanonType.Timestamp => values.Cast<DateTimeOffset>().ToArray(),
        _ => throw new DpException(DpCodes.DslOperatorUnsupported, 400, $"Query DSL: in/notIn по типу {t} не поддержан")
    };

    private static (string Ns, string Key) SplitPath(string path)
    {
        var i = path.IndexOf('.');
        if (i <= 0) throw new DpException(DpCodes.DslSyntaxInvalid, 400, $"Query DSL: ext-путь '{path}' должен быть namespace.field");
        return (path[..i], path[(i + 1)..]);
    }
}

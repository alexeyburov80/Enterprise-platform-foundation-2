// ============================================================================
// Store Service — движок записи/чтения logical store'ов поверх PostgreSQL.
// Семантика missing/null/default — таблица 6; Idempotency-Key 24 ч (DP-5001);
// ETag/If-Match по row_version (DP-5002); soft-delete; события — Outbox
// в той же транзакции, что и бизнес-данные (обяз., решение В6).
// Extension DTO — jsonb-колонка ext, мердж namespace-ключей без затирания чужих.
// ============================================================================

using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Dp.Core;

public sealed record ResolvedEntity(SdDocument Store, SdEntity Entity, string Schema, string Table);

public sealed record WriteOutcome(JsonObject Row, bool Replay);

/// <summary>Маппинг строк: канонические поля + ext + аудиторские колонки (порядок фиксирован).</summary>
public static class Rows
{
    public static string[] SelectCols(ResolvedEntity re)
    {
        var list = re.Entity.Fields.Select(DdlGenerator.Column).ToList();
        list.Add("ext");
        if (re.Entity.Lifecycle.Audited)
            list.AddRange(new[] { "created_at", "updated_at", "created_by", "updated_by", "row_version" });
        if (re.Entity.Lifecycle.SoftDelete)
            list.Add("deleted_at");
        return list.ToArray();
    }

    public static JsonNode? ValueToJsonNode(CanonType t, object? v)
    {
        if (v is null) return null;
        return t switch
        {
            CanonType.Uuid or CanonType.Ref or CanonType.FileRef => JsonValue.Create(((Guid)v).ToString()),
            CanonType.String or CanonType.Text or CanonType.Enum => JsonValue.Create((string)v),
            CanonType.Boolean => JsonValue.Create((bool)v),
            CanonType.Int32 => JsonValue.Create((int)v),
            CanonType.Int64 => JsonValue.Create((long)v),
            CanonType.Decimal or CanonType.Money => JsonValue.Create((decimal)v),
            CanonType.Date => JsonValue.Create(((DateOnly)v).ToString("yyyy-MM-dd")),
            CanonType.Time => JsonValue.Create(((TimeOnly)v).ToString("HH:mm:ss")),
            CanonType.Timestamp => JsonValue.Create(Iso((DateTimeOffset)v)),
            CanonType.Json => JsonNode.Parse((string)v),
            CanonType.Binary => JsonValue.Create(Convert.ToBase64String((byte[])v)),
            _ => JsonValue.Create(v.ToString())
        };
    }

    public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    /// <summary>Npgsql 8 читает timestamptz как DateTime(UTC) — приводим к DateTimeOffset.</summary>
    public static DateTimeOffset AsDto(object o) => o switch
    {
        DateTimeOffset d => d,
        DateTime t when t.Kind == DateTimeKind.Local => new DateTimeOffset(t.ToUniversalTime()),
        DateTime t => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)),
        _ => (DateTimeOffset)o
    };

    private static bool IsNull(NpgsqlDataReader r, int ord) => r.IsDBNull(ord);

    public static JsonObject RowToJson(NpgsqlDataReader r, ResolvedEntity re)
    {
        var obj = new JsonObject();
        var ord = 0;
        foreach (var f in re.Entity.Fields)
        {
            obj[f.Name] = IsNull(r, ord) ? null : ValueToJsonNode(f.Canon, TypedValue(r, f.Canon, ord));
            ord++;
        }
        obj["ext"] = IsNull(r, ord) ? null : JsonNode.Parse(r.GetString(ord));
        ord++;
        if (re.Entity.Lifecycle.Audited)
        {
            obj["createdAt"] = Iso(AsDto(r.GetValue(ord)));
            ord++;
            obj["updatedAt"] = IsNull(r, ord) ? null : Iso(AsDto(r.GetValue(ord)));
            ord++;
            obj["createdBy"] = IsNull(r, ord) ? null : r.GetString(ord);
            ord++;
            obj["updatedBy"] = IsNull(r, ord) ? null : r.GetString(ord);
            ord++;
            obj["rowVersion"] = r.GetInt64(ord);
            ord++;
        }
        if (re.Entity.Lifecycle.SoftDelete)
        {
            obj["deletedAt"] = IsNull(r, ord) ? null : Iso(AsDto(r.GetValue(ord)));
            ord++;
        }
        return obj;
    }

    /// <summary>Текущая строка в типах C# — для сравнения при UPDATE (patch/replace).</summary>
    public static (Dictionary<string, object?> Values, JsonObject Ext) TypedRow(NpgsqlDataReader r, ResolvedEntity re)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var ord = 0;
        foreach (var f in re.Entity.Fields)
        {
            values[f.Name] = IsNull(r, ord) ? null : TypedValue(r, f.Canon, ord);
            ord++;
        }
        var ext = IsNull(r, ord)
            ? new JsonObject()
            : (JsonNode.Parse(r.GetString(ord)) as JsonObject ?? new JsonObject());
        return (values, ext);
    }

    private static object? TypedValue(NpgsqlDataReader r, CanonType t, int ord) => t switch
    {
        CanonType.Uuid or CanonType.Ref or CanonType.FileRef => r.GetGuid(ord),
        CanonType.String or CanonType.Text or CanonType.Enum => r.GetString(ord),
        CanonType.Boolean => r.GetBoolean(ord),
        CanonType.Int32 => r.GetInt32(ord),
        CanonType.Int64 => r.GetInt64(ord),
        CanonType.Decimal or CanonType.Money => r.GetDecimal(ord),
        CanonType.Date => DateOnly.FromDateTime((DateTime)r.GetValue(ord)),
        CanonType.Time => TimeOnly.FromTimeSpan((TimeSpan)r.GetValue(ord)),
        CanonType.Timestamp => AsDto(r.GetValue(ord)),
        CanonType.Json => r.GetString(ord),
        CanonType.Binary => (byte[])r.GetValue(ord),
        _ => r.GetValue(ord)
    };
}

public sealed class StoreService
{
    private readonly NpgsqlDataSource _db;
    private readonly SdRegistry _sd;
    private readonly OutboxWriter _outbox;
    private readonly ILogger<StoreService> _logger;

    public StoreService(NpgsqlDataSource db, SdRegistry sd, OutboxWriter outbox, ILogger<StoreService> logger)
    {
        _db = db; _sd = sd; _outbox = outbox; _logger = logger;
    }

    public Guid IdOf(string storeId, string entity, string externalId) =>
        DpUtil.Uuid5($"{storeId}/{entity}/{externalId}");

    public ResolvedEntity Resolve(string storeId, string entityName, string? versionPin)
    {
        var store = _sd.FindStore(storeId);
        if (!string.IsNullOrEmpty(versionPin) && versionPin != store.Metadata.Version)
            throw new DpException(DpCodes.StoreVersionDeprecated, 410,
                $"Запрошен пин версии {versionPin}, но live-версия — {store.Metadata.Version}. " +
                "MVP обслуживает одну live-версию (окно N/N-1 включается при первом MAJOR-изменении).");
        var entity = _sd.GetEntity(store, entityName);
        return new ResolvedEntity(store, entity, DdlGenerator.StoreSchema(store), DdlGenerator.EntityTable(store, entity));
    }

    // ------------------------------------------------------------------ Get

    public async Task<JsonObject> GetAsync(ResolvedEntity re, Guid id, bool includeDeleted)
    {
        await using var conn = await _db.OpenConnectionAsync();
        return await GetCoreAsync(conn, re, id, includeDeleted);
    }

    private async Task<JsonObject> GetCoreAsync(NpgsqlConnection conn, ResolvedEntity re, Guid id, bool includeDeleted)
    {
        var where = re.Entity.Lifecycle.SoftDelete && !includeDeleted ? " AND deleted_at IS NULL" : "";
        var sql = $"SELECT {string.Join(", ", Rows.SelectCols(re))} FROM {re.Schema}.{re.Table} WHERE {PkWhere(re)}{where}";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id0", GuidType(re), id);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync())
            throw DpException.RecordNotFound($"Запись id={id} не найдена в {re.Store.StoreId}/{re.Entity.Name}");
        return Rows.RowToJson(r, re);
    }

    private static string PkWhere(ResolvedEntity re) =>
        string.Join(" AND ", re.Entity.PrimaryKey.Select((pk, i) =>
            $"{DdlGenerator.Column(PkField(re, i))} = @id{i}"));

    private static SdField PkField(ResolvedEntity re, int idx = 0)
    {
        var name = re.Entity.PrimaryKey[idx];
        return re.Entity.Fields.First(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static NpgsqlDbType GuidType(ResolvedEntity re) => NpgsqlDbType.Uuid;

    // ---------------------------------------------------------------- Insert

    public async Task<WriteOutcome> InsertAsync(ResolvedEntity re, JsonObject payload, string actor,
        string? idemKey, string idemHash)
    {
        var parsed = PayloadParser.Parse(re.Entity, payload, _sd, re.Store.StoreId);
        var values = BuildInsertValues(re, parsed);

        await using var conn = await _db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        if (idemKey is not null)
        {
            var replay = await CheckIdempotencyAsync(conn, tx, idemKey, re.Store.StoreId, idemHash);
            if (replay is not null) return new WriteOutcome(replay.Value.Body, true);
        }

        var row = await InsertCoreAsync(conn, tx, re, values, parsed.ExtChanges, actor);
        if (idemKey is not null)
            await StoreIdempotencyAsync(conn, tx, idemKey, re.Store.StoreId, idemHash, 201, row);

        await tx.CommitAsync();
        _logger.LogInformation("Insert {Store}/{Entity} id={Id}", re.Store.StoreId, re.Entity.Name, (string?)row["id"]);
        return new WriteOutcome(row, false);
    }

    /// <summary>Insert-семантика табл. 6: missing → default; явный null required → DP-1002.</summary>
    private Dictionary<string, object?> BuildInsertValues(ResolvedEntity re, ParsedPayload parsed)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var f in re.Entity.Fields)
        {
            if (parsed.Values.TryGetValue(f.Name, out var v))
            {
                if (v is null && f.Required)
                    throw new DpException(DpCodes.NullNotAllowed, 400,
                        $"{re.Entity.Name}.{f.Name}: явный null для required-поля (missing ≠ null, табл. 6)");
                values[f.Name] = v;
            }
            else
            {
                values[f.Name] = FieldValidator.ApplyDefault(f, re.Entity.Name);
            }
        }
        return values;
    }

    // ---------------------------------------------------------------- Update

    public async Task<WriteOutcome> UpdateAsync(ResolvedEntity re, Guid id, JsonObject payload, string mode,
        string actor, long? ifMatch, string? idemKey, string idemHash)
    {
        var parsed = PayloadParser.Parse(re.Entity, payload, _sd, re.Store.StoreId);

        await using var conn = await _db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        if (idemKey is not null)
        {
            var replay = await CheckIdempotencyAsync(conn, tx, idemKey, re.Store.StoreId, idemHash);
            if (replay is not null) return new WriteOutcome(replay.Value.Body, true);
        }

        var (row, _) = await UpdateCoreAsync(conn, tx, re, id, parsed, mode, actor, ifMatch);
        if (idemKey is not null)
            await StoreIdempotencyAsync(conn, tx, idemKey, re.Store.StoreId, idemHash, 200, row);

        await tx.CommitAsync();
        _logger.LogInformation("Update {Store}/{Entity} id={Id} mode={Mode}", re.Store.StoreId, re.Entity.Name, id, mode);
        return new WriteOutcome(row, false);
    }

    // ---------------------------------------------------------------- Delete

    public async Task<WriteOutcome> DeleteAsync(ResolvedEntity re, Guid id, bool hard, string actor,
        string? idemKey, string idemHash)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        if (idemKey is not null)
        {
            var replay = await CheckIdempotencyAsync(conn, tx, idemKey, re.Store.StoreId, idemHash);
            if (replay is not null) return new WriteOutcome(replay.Value.Body, true);
        }

        var row = await DeleteCoreAsync(conn, tx, re, id, hard, actor);
        if (idemKey is not null)
            await StoreIdempotencyAsync(conn, tx, idemKey, re.Store.StoreId, idemHash, 200, row);

        await tx.CommitAsync();
        _logger.LogInformation("Delete {Store}/{Entity} id={Id} hard={Hard}", re.Store.StoreId, re.Entity.Name, id, hard);
        return new WriteOutcome(row, false);
    }

    // -------------------------------------------- Batch / ExecuteTransaction

    public async Task<JsonObject> BatchAsync(string storeId, JsonArray operations, string actor,
        string? idemKey, string idemHash)
    {
        if (operations.Count > 500)
            throw new DpException(DpCodes.PayloadTooLarge, 413,
                $"Пакет содержит {operations.Count} операций — лимит 500 (см. GetCapabilities)");
        var store = _sd.FindStore(storeId);

        await using var conn = await _db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        if (idemKey is not null)
        {
            var replay = await CheckIdempotencyAsync(conn, tx, idemKey, storeId, idemHash);
            if (replay is not null) return replay.Value.Body;
        }

        var results = new JsonArray();
        var index = 0;
        foreach (var opNode in operations)
        {
            if (opNode is not JsonObject op)
                throw new DpException(DpCodes.DslSyntaxInvalid, 400, $"Операция [{index}] должна быть объектом {{op, entity, ...}}");
            var kind = (string?)op["op"] ?? throw new DpException(DpCodes.DslSyntaxInvalid, 400, $"Операция [{index}]: отсутствует 'op'");
            var entityName = (string?)op["entity"] ?? throw new DpException(DpCodes.DslSyntaxInvalid, 400, $"Операция [{index}]: отсутствует 'entity'");
            var entity = _sd.GetEntity(store, entityName);
            var re = new ResolvedEntity(store, entity, DdlGenerator.StoreSchema(store), DdlGenerator.EntityTable(store, entity));

            JsonObject result;
            switch (kind)
            {
                case "insert":
                {
                    var fields = op["fields"] as JsonObject ?? throw new DpException(DpCodes.DslSyntaxInvalid, 400, $"Операция [{index}]: insert требует 'fields'");
                    var parsed = PayloadParser.Parse(entity, fields, _sd, storeId);
                    var values = BuildInsertValues(re, parsed);
                    var row = await InsertCoreAsync(conn, tx, re, values, parsed.ExtChanges, actor);
                    result = new JsonObject
                    {
                        ["op"] = "insert",
                        ["entity"] = entityName,
                        ["id"] = (string?)row["id"],
                        ["rowVersion"] = row["rowVersion"]?.DeepClone()
                    };
                    break;
                }
                case "update":
                {
                    var id = Guid.Parse((string?)op["id"] ?? throw new DpException(DpCodes.DslSyntaxInvalid, 400, $"Операция [{index}]: update требует 'id'"));
                    var fields = op["fields"] as JsonObject ?? throw new DpException(DpCodes.DslSyntaxInvalid, 400, $"Операция [{index}]: update требует 'fields'");
                    var mode = (string?)op["mode"] ?? "patch";
                    if (mode is not ("patch" or "replace"))
                        throw new DpException(DpCodes.DslSyntaxInvalid, 400, $"Операция [{index}]: mode должен быть patch|replace");
                    var parsed = PayloadParser.Parse(entity, fields, _sd, storeId);
                    var (row, _) = await UpdateCoreAsync(conn, tx, re, id, parsed, mode, actor, null);
                    result = new JsonObject
                    {
                        ["op"] = "update",
                        ["entity"] = entityName,
                        ["id"] = id.ToString(),
                        ["rowVersion"] = row["rowVersion"]?.DeepClone()
                    };
                    break;
                }
                case "delete":
                {
                    var id = Guid.Parse((string?)op["id"] ?? throw new DpException(DpCodes.DslSyntaxInvalid, 400, $"Операция [{index}]: delete требует 'id'"));
                    var hard = (bool?)op["hard"] ?? false;
                    await DeleteCoreAsync(conn, tx, re, id, hard, actor);
                    result = new JsonObject { ["op"] = "delete", ["entity"] = entityName, ["id"] = id.ToString() };
                    break;
                }
                default:
                    throw new DpException(DpCodes.DslSyntaxInvalid, 400,
                        $"Операция [{index}]: op='{kind}' — допустимы insert|update|delete");
            }
            result["index"] = index;
            results.Add(result);
            index++;
        }

        if (idemKey is not null)
            await StoreIdempotencyAsync(conn, tx, idemKey, storeId, idemHash, 200,
                new JsonObject { ["results"] = results.DeepClone() });

        await tx.CommitAsync();
        return new JsonObject { ["results"] = results };
    }

    // ------------------------------------------------ Внутренний upsert (ingestion)

    public async Task<(bool Created, long RowVersion)> UpsertInternalAsync(
        ResolvedEntity re, string externalId, Dictionary<string, object?> fields,
        string extNamespace, Dictionary<string, object?> extFields, string actor)
    {
        var id = IdOf(re.Store.StoreId, re.Entity.Name, externalId);
        if (!fields.ContainsKey("id") && re.Entity.PrimaryKey.Any(pk => string.Equals(pk, "id", StringComparison.OrdinalIgnoreCase)))
            fields["id"] = id;

        var parsed = new ParsedPayload();
        foreach (var (k, v) in fields) parsed.Values[k] = v;
        foreach (var (k, v) in extFields)
            parsed.ExtChanges[$"{extNamespace}.{k}"] = v is null ? null : JsonValue.Create(v);

        await using var conn = await _db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var exists = await ExistsAsync(conn, re, id);
        if (exists)
        {
            var (row, _) = await UpdateCoreAsync(conn, tx, re, id, parsed, "patch", actor, null);
            await tx.CommitAsync();
            return (false, (long?)row["rowVersion"] ?? 1);
        }

        var inserted = await InsertCoreAsync(conn, tx, re, parsed.Values, parsed.ExtChanges, actor);
        await tx.CommitAsync();
        return (true, (long?)inserted["rowVersion"] ?? 1);
    }

    private async Task<bool> ExistsAsync(NpgsqlConnection conn, ResolvedEntity re, Guid id)
    {
        var sql = $"SELECT 1 FROM {re.Schema}.{re.Table} WHERE {PkWhere(re)} FOR UPDATE";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id0", GuidType(re), id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync();
    }

    // ---------------------------------------------------------------- Ядро записи

    private async Task<JsonObject> InsertCoreAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        ResolvedEntity re, Dictionary<string, object?> values, Dictionary<string, JsonNode?> extChanges, string actor)
    {
        var cols = new List<string>();
        var placeholders = new List<string>();
        await using var cmd = new NpgsqlCommand { Connection = conn, Transaction = tx };
        var p = 0;

        foreach (var f in re.Entity.Fields)
        {
            if (!values.TryGetValue(f.Name, out var v)) continue;
            if (v is null) continue; // NULL optional — колонка nullable, не включаем
            cols.Add(DdlGenerator.Column(f));
            var name = "@p" + p++;
            placeholders.Add(name);
            AddParam(cmd, name, f.Canon, v);
        }

        var insertExt = MergeInsertExt(extChanges);
        if (insertExt is not null)
        {
            cols.Add("ext");
            placeholders.Add("@ext");
            cmd.Parameters.AddWithValue("@ext", NpgsqlDbType.Jsonb, insertExt.ToJsonString());
        }

        if (re.Entity.Lifecycle.Audited)
        {
            cols.Add("created_by");
            placeholders.Add("@actor");
            cmd.Parameters.AddWithValue("@actor", actor);
        }

        cmd.CommandText =
            $"INSERT INTO {re.Schema}.{re.Table} ({string.Join(", ", cols)}) " +
            $"VALUES ({string.Join(", ", placeholders)}) RETURNING {string.Join(", ", Rows.SelectCols(re))}";
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync())
            throw new DpException(DpCodes.Internal, 500, "INSERT RETURNING не вернул строку");
        var row = Rows.RowToJson(r, re);
        await r.CloseAsync();

        // Outbox в той же транзакции (обяз., В6)
        await _outbox.AppendAsync(conn, tx, re, "insert", KeyId(re, row), CanonicalData(re, row), null, actor);
        return row;
    }

    private async Task<(JsonObject Row, JsonObject Previous)> UpdateCoreAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        ResolvedEntity re, Guid id, ParsedPayload parsed, string mode, string actor, long? ifMatch)
    {
        var cols = Rows.SelectCols(re);
        var where = re.Entity.Lifecycle.SoftDelete ? " AND deleted_at IS NULL" : "";

        var curValues = new Dictionary<string, object?>(StringComparer.Ordinal);
        var curExt = new JsonObject();
        long? curRowVersion = null;

        await using (var cmd = new NpgsqlCommand(
            $"SELECT {string.Join(", ", cols)} FROM {re.Schema}.{re.Table} WHERE {PkWhere(re)}{where} FOR UPDATE", conn, tx))
        {
            cmd.Parameters.AddWithValue("@id0", GuidType(re), id);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                throw DpException.RecordNotFound($"Запись id={id} не найдена в {re.Store.StoreId}/{re.Entity.Name}");
            (curValues, curExt) = Rows.TypedRow(r, re);
            if (re.Entity.Lifecycle.Audited)
                curRowVersion = r.GetInt64(re.Entity.Fields.Count + 5); // ext(+1) + created_at/updated_at/created_by/updated_by(+4) → row_version последняя
            await r.CloseAsync();
        }

        if (ifMatch is not null && curRowVersion is not null && ifMatch != curRowVersion)
            throw new DpException(DpCodes.ConcurrentUpdate, 409,
                $"row_version {curRowVersion} != If-Match {ifMatch}");

        var changedOld = new JsonObject(); // previous для события
        var sets = new List<string>();
        await using var upd = new NpgsqlCommand { Connection = conn, Transaction = tx };
        var p = 0;

        foreach (var f in re.Entity.Fields)
        {
            // PK-поля неизменяемы: запись адресуется текущим ключом (WHERE id),
            // replace-сброс к default на PK не распространяется
            if (re.Entity.PrimaryKey.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (parsed.Values.TryGetValue(f.Name, out var pkv)
                    && !SameValue(f.Canon, curValues.TryGetValue(f.Name, out var ck) ? ck : null, pkv))
                    throw new DpException(DpCodes.TypeMismatch, 400,
                        $"{re.Entity.Name}.{f.Name}: PK неизменяем — указанное значение '{pkv}' не совпадает с текущей записью");
                continue;
            }

            object? target;
            if (mode == "replace")
                target = parsed.Values.TryGetValue(f.Name, out var tv) ? tv : DefaultLenient(f, re.Entity.Name);
            else // patch
            {
                if (!parsed.Values.TryGetValue(f.Name, out var pv)) continue;
                target = pv;
                if (target is null && f.Required)
                    throw new DpException(DpCodes.NullNotAllowed, 400,
                        $"{re.Entity.Name}.{f.Name}: явный null для required-поля (patch, табл. 6)");
            }

            var current = curValues.TryGetValue(f.Name, out var cv) ? cv : null;
            if (SameValue(f.Canon, current, target)) continue;

            changedOld[f.Name] = Rows.ValueToJsonNode(f.Canon, current);

            if (target is null)
            {
                if (f.Required)
                    throw new DpException(DpCodes.NullNotAllowed, 400,
                        $"{re.Entity.Name}.{f.Name}: явный null для required-поля (replace, табл. 6)");
                sets.Add($"{DdlGenerator.Column(f)} = NULL");
            }
            else
            {
                sets.Add($"{DdlGenerator.Column(f)} = @s{p}");
                AddParam(upd, "@s" + p, f.Canon, target);
                p++;
            }
        }

        // ext: patch — мердж ключей своего namespace (null удаляет ключ); replace — замена своего namespace
        var extParam = BuildExtMerge(mode, curExt, parsed.ExtChanges);
        if (extParam is not null)
        {
            sets.Add("ext = COALESCE(ext, '{}'::jsonb) || @ext");
            upd.Parameters.AddWithValue("@ext", NpgsqlDbType.Jsonb, extParam.ToJsonString());
        }

        if (re.Entity.Lifecycle.Audited)
        {
            sets.Add("row_version = row_version + 1");
            sets.Add("updated_at = now()");
            sets.Add("updated_by = @actor");
            upd.Parameters.AddWithValue("@actor", actor);
        }

        if (sets.Count == 0)
            return (await GetCoreAsync(conn, re, id, includeDeleted: false), new JsonObject());

        upd.CommandText =
            $"UPDATE {re.Schema}.{re.Table} SET {string.Join(", ", sets)} WHERE {PkWhere(re)} " +
            $"RETURNING {string.Join(", ", cols)}";
        upd.Parameters.AddWithValue("@id0", GuidType(re), id);
        await using var r2 = await upd.ExecuteReaderAsync();
        if (!await r2.ReadAsync())
            throw new DpException(DpCodes.Internal, 500, "UPDATE RETURNING не вернул строку");
        var row2 = Rows.RowToJson(r2, re);
        await r2.CloseAsync();

        await _outbox.AppendAsync(conn, tx, re, "update", id, CanonicalData(re, row2), changedOld, actor);
        return (row2, changedOld);
    }

    private async Task<JsonObject> DeleteCoreAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        ResolvedEntity re, Guid id, bool hard, string actor)
    {
        var cols = Rows.SelectCols(re);
        var where = re.Entity.Lifecycle.SoftDelete && !hard ? " AND deleted_at IS NULL" : "";
        JsonObject current;
        await using (var cmd = new NpgsqlCommand(
            $"SELECT {string.Join(", ", cols)} FROM {re.Schema}.{re.Table} WHERE {PkWhere(re)}{where} FOR UPDATE", conn, tx))
        {
            cmd.Parameters.AddWithValue("@id0", GuidType(re), id);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                throw DpException.RecordNotFound($"Запись id={id} не найдена в {re.Store.StoreId}/{re.Entity.Name}");
            current = Rows.RowToJson(r, re);
            await r.CloseAsync();
        }

        if (!hard && re.Entity.Lifecycle.SoftDelete)
        {
            await using var cmd = new NpgsqlCommand(
                $"UPDATE {re.Schema}.{re.Table} SET deleted_at = now(), row_version = row_version + 1, updated_at = now(), updated_by = @actor WHERE {PkWhere(re)}", conn, tx);
            cmd.Parameters.AddWithValue("@actor", actor);
            cmd.Parameters.AddWithValue("@id0", GuidType(re), id);
            await cmd.ExecuteNonQueryAsync();
        }
        else
        {
            await using var cmd = new NpgsqlCommand($"DELETE FROM {re.Schema}.{re.Table} WHERE {PkWhere(re)}", conn, tx);
            cmd.Parameters.AddWithValue("@id0", GuidType(re), id);
            await cmd.ExecuteNonQueryAsync();
        }

        await _outbox.AppendAsync(conn, tx, re, "delete", id,
            new JsonObject { ["id"] = id.ToString() }, null, actor);
        return current;
    }

    /// <summary>Replace-семантика (табл. 6): missing → default; required без default → NULL
    /// (нарушение NOT NULL поймает PostgreSQL → DP-1002 через SqlState 23502).</summary>
    private static object? DefaultLenient(SdField f, string entityName)
    {
        try
        {
            return FieldValidator.ApplyDefault(f, entityName);
        }
        catch (DpException ex) when (ex.Code == DpCodes.MissingField)
        {
            return null;
        }
    }

    private static Guid KeyId(ResolvedEntity re, JsonObject row)
    {
        var pk = (string?)row[re.Entity.PrimaryKey[0]];
        return Guid.TryParse(pk, out var g) ? g : Guid.Empty;
    }

    private static JsonObject CanonicalData(ResolvedEntity re, JsonObject row)
    {
        var data = new JsonObject();
        foreach (var f in re.Entity.Fields)
            data[f.Name] = row[f.Name]?.DeepClone();
        return data;
    }

    // ------------------------------------------------------------- ext-мерж (Extension DTO)

    private static (string Ns, string Key) SplitExtKey(string key)
    {
        var i = key.IndexOf('.');
        return i <= 0 ? ("__stray", key) : (key[..i], key[(i + 1)..]);
    }

    private static JsonObject? MergeInsertExt(Dictionary<string, JsonNode?> changes)
    {
        if (changes.Count == 0) return null;
        var root = new JsonObject();
        foreach (var (key, val) in changes)
        {
            var (ns, k) = SplitExtKey(key);
            if (root[ns] is not JsonObject nsObj)
            {
                nsObj = new JsonObject();
                root[ns] = nsObj;
            }
            nsObj[k] = val?.DeepClone();
        }
        return root;
    }

    /// <summary>
    /// Extension DTO (2.7): мердж namespace-ключей без затирания чужих.
    /// patch: missing-ключ не трогает значение, явный null удаляет ключ из namespace;
    /// replace: свой namespace заменяется целиком.
    /// </summary>
    private static JsonObject? BuildExtMerge(string mode, JsonObject currentExt, Dictionary<string, JsonNode?> changes)
    {
        if (changes.Count == 0) return null;

        var byNs = new Dictionary<string, Dictionary<string, JsonNode?>>(StringComparer.Ordinal);
        foreach (var (key, val) in changes)
        {
            var (ns, k) = SplitExtKey(key);
            if (!byNs.TryGetValue(ns, out var m)) byNs[ns] = m = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            m[k] = val;
        }

        var result = new JsonObject();
        foreach (var (ns, keys) in byNs)
        {
            JsonObject nsObj;
            if (mode == "replace")
            {
                nsObj = new JsonObject();
                foreach (var (k, v) in keys) nsObj[k] = v?.DeepClone();
            }
            else
            {
                var existing = currentExt[ns] as JsonObject;
                nsObj = existing is null ? new JsonObject() : (JsonObject)existing.DeepClone();
                foreach (var (k, v) in keys)
                {
                    if (v is null) nsObj.Remove(k);
                    else nsObj[k] = v.DeepClone();
                }
            }
            result[ns] = nsObj;
        }
        return result;
    }

    // ------------------------------------------------------------- Idempotency (24 ч)

    private async Task<(JsonObject Body, int Status)?> CheckIdempotencyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string key, string storeId, string hash)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT request_hash, response_status, response_body, created_at FROM dp.idempotency WHERE key = @key FOR UPDATE", conn, tx);
        cmd.Parameters.AddWithValue("@key", key);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        var storedHash = r.GetString(0);
        var status = r.GetInt32(1);
        var body = r.GetString(2);
        var createdAt = Rows.AsDto(r.GetValue(3));
        await r.CloseAsync();

        if (storedHash != hash)
            throw new DpException(DpCodes.IdempotencyKeyConflict, 409,
                "Ключ Idempotency-Key уже использован с иным телом запроса (окно дедупликации 24 ч)");

        if (DateTimeOffset.UtcNow - createdAt > TimeSpan.FromHours(24))
        {
            await using var del = new NpgsqlCommand("DELETE FROM dp.idempotency WHERE key = @key", conn, tx);
            del.Parameters.AddWithValue("@key", key);
            await del.ExecuteNonQueryAsync();
            return null;
        }

        return (JsonNode.Parse(body) as JsonObject ?? new JsonObject(), status);
    }

    private static async Task StoreIdempotencyAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        string key, string storeId, string hash, int status, JsonObject body)
    {
        await using var cmd = new NpgsqlCommand("""
                INSERT INTO dp.idempotency (key, store_id, request_hash, response_status, response_body)
                VALUES (@key, @store, @hash, @status, @body)
                ON CONFLICT (key) DO NOTHING
                """, conn, tx);
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@store", storeId);
        cmd.Parameters.AddWithValue("@hash", hash);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@body", NpgsqlDbType.Jsonb, body.ToJsonString());
        await cmd.ExecuteNonQueryAsync();
    }

    // ------------------------------------------------------------- вспомогательное

    private static void AddParam(NpgsqlCommand cmd, string name, CanonType t, object? v) => _ = t switch
    {
        CanonType.Uuid or CanonType.Ref or CanonType.FileRef => cmd.Parameters.AddWithValue(name, (Guid)v!),
        CanonType.String or CanonType.Text or CanonType.Enum => cmd.Parameters.AddWithValue(name, (string)v!),
        CanonType.Boolean => cmd.Parameters.AddWithValue(name, (bool)v!),
        CanonType.Int32 => cmd.Parameters.AddWithValue(name, Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture)),
        CanonType.Int64 => cmd.Parameters.AddWithValue(name, Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture)),
        CanonType.Decimal or CanonType.Money => cmd.Parameters.AddWithValue(name, Convert.ToDecimal(v, System.Globalization.CultureInfo.InvariantCulture)),
        CanonType.Date => cmd.Parameters.AddWithValue(name, (DateOnly)v!),
        CanonType.Time => cmd.Parameters.AddWithValue(name, (TimeOnly)v!),
        CanonType.Timestamp => cmd.Parameters.AddWithValue(name, (DateTimeOffset)v!),
        CanonType.Json => cmd.Parameters.AddWithValue(name, NpgsqlDbType.Jsonb, (string)v!),
        CanonType.Binary => cmd.Parameters.AddWithValue(name, (byte[])v!),
        _ => throw new DpException(DpCodes.TypeMismatch, 400, $"Тип {t} не поддержан в MVP-записи")
    };

    private static bool SameValue(CanonType t, object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return t switch
        {
            CanonType.Decimal or CanonType.Money => Convert.ToDecimal(a, System.Globalization.CultureInfo.InvariantCulture)
                .Equals(Convert.ToDecimal(b, System.Globalization.CultureInfo.InvariantCulture)),
            CanonType.Int64 => Convert.ToInt64(a, System.Globalization.CultureInfo.InvariantCulture)
                .Equals(Convert.ToInt64(b, System.Globalization.CultureInfo.InvariantCulture)),
            CanonType.Int32 => Convert.ToInt32(a, System.Globalization.CultureInfo.InvariantCulture)
                .Equals(Convert.ToInt32(b, System.Globalization.CultureInfo.InvariantCulture)),
            CanonType.Timestamp => ((DateTimeOffset)a).UtcDateTime == ((DateTimeOffset)b).UtcDateTime,
            CanonType.Binary => ((byte[])a).AsSpan().SequenceEqual((byte[])b),
            CanonType.Json => string.Equals((string)a, (string)b, StringComparison.Ordinal),
            _ => a.Equals(b)
        };
    }
}

// ============================================================================
// Генератор DDL из канона SD + forward-only мигратор (решения A-8…A-12).
// Журнал dp.migration_journal — по листингу 3 спецификации. Пакет детерминирован,
// хешируется SHA-256, применение идемпотентно, откатов нет (forward-only).
// ============================================================================

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Dp.Core;

public static class DpUtil
{
    /// <summary>Детерминированный маппинг camelCase → snake_case (обяз. спецификации).</summary>
    public static string ToSnake(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (!char.IsUpper(s[i - 1]) || (i + 1 < s.Length && char.IsLower(s[i + 1]))))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public static string Sha256Hex(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>RFC 4122 uuid5 — детерминированные внешние ключи интеграции.</summary>
    public static Guid Uuid5(string name)
    {
        byte[] ns = [0x6b, 0xa7, 0xb8, 0x10, 0x9d, 0xad, 0x11, 0xd1, 0x80, 0xb4, 0x00, 0xc0, 0x4f, 0xd4, 0x30, 0xc8];
        var hash = SHA1.HashData(ns.Concat(Encoding.UTF8.GetBytes(name)).ToArray());
        var b = new byte[16];
        Array.Copy(hash, b, 16);
        b[6] = (byte)((b[6] & 0x0f) | 0x50);
        b[8] = (byte)((b[8] & 0x3f) | 0x80);
        return new Guid(b);
    }
}

public static class DdlGenerator
{
    public static string StoreSchema(SdDocument store) =>
        $"st_{DpUtil.ToSnake(store.Metadata.Module)}_{DpUtil.ToSnake(store.Metadata.Store)}";

    public static string EntityTable(SdDocument store, SdEntity e) =>
        string.IsNullOrEmpty(e.Table) ? DpUtil.ToSnake(e.Name) : DpUtil.ToSnake(e.Table);

    public static string Column(SdField f) => DpUtil.ToSnake(f.Name);

    /// <summary>Миграционный пакет store'а from 0.0.0 (первичное развёртывание MVP).</summary>
    public static string GenerateStorePackage(SdDocument store)
    {
        var schema = StoreSchema(store);
        var sb = new StringBuilder();
        sb.AppendLine("-- dp-migration-package (сгенерирован из канона SD, детерминированный вид)");
        sb.AppendLine($"-- store: {store.StoreId}");
        sb.AppendLine($"-- to_version: {store.Metadata.Version}");
        sb.AppendLine("-- lockRisk: none");
        sb.AppendLine($"CREATE SCHEMA IF NOT EXISTS {schema};");

        foreach (var e in store.Spec.Entities)
        {
            var table = EntityTable(store, e);
            sb.AppendLine();
            sb.AppendLine($"CREATE TABLE IF NOT EXISTS {schema}.{table} (");
            var cols = new List<string>();
            foreach (var f in e.Fields)
                cols.Add($"    {Column(f)} {PgType(f)}{Nullability(f)}{Default(f)}");
            cols.Add("    ext jsonb NULL");
            if (e.Lifecycle.Audited)
            {
                cols.Add("    created_at timestamptz NOT NULL DEFAULT now()");
                cols.Add("    updated_at timestamptz NULL");
                cols.Add("    created_by varchar(120) NULL");
                cols.Add("    updated_by varchar(120) NULL");
                cols.Add("    row_version bigint NOT NULL DEFAULT 1");
            }
            if (e.Lifecycle.SoftDelete)
                cols.Add("    deleted_at timestamptz NULL");
            cols.Add($"    CONSTRAINT pk_{table} PRIMARY KEY ({string.Join(", ", e.PrimaryKey.Select(fn => Column(ResolveField(e, fn))))})");
            sb.AppendLine(string.Join(",\n", cols));
            sb.AppendLine(");");

            // канонические констрейнты
            foreach (var c in e.Constraints)
            {
                switch (c.Type)
                {
                    case "unique":
                        sb.AppendLine($"CREATE UNIQUE INDEX IF NOT EXISTS {DpUtil.ToSnake(c.Name)} ON {schema}.{table} ({string.Join(", ", c.Fields.Select(fn => Column(ResolveField(e, fn))))});");
                        break;
                    case "check":
                        sb.AppendLine($"ALTER TABLE {schema}.{table} ADD CONSTRAINT {DpUtil.ToSnake(c.Name)} CHECK ({TranslateCheckExpr(e, c.Expr ?? "")});");
                        break;
                    case "fk":
                        var fkTarget = store.Spec.Entities.First(x => string.Equals(x.Name, c.RefEntity, StringComparison.OrdinalIgnoreCase));
                        sb.AppendLine($"ALTER TABLE {schema}.{table} ADD CONSTRAINT {DpUtil.ToSnake(c.Name)} FOREIGN KEY ({string.Join(", ", c.Fields.Select(fn => Column(ResolveField(e, fn))))}) REFERENCES {schema}.{EntityTable(store, fkTarget)} ({string.Join(", ", c.RefFields.Select(fn => Column(ResolveField(fkTarget, fn))))}) ON DELETE {c.OnDelete.ToUpperInvariant()};");
                        break;
                }
            }

            // индексы
            foreach (var ix in e.Indexes)
            {
                var parts = ix.Fields.Select(fspec =>
                {
                    var tokens = fspec.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var col = Column(ResolveField(e, tokens[0]));
                    var dir = tokens.Length > 1 && tokens[1].Equals("desc", StringComparison.OrdinalIgnoreCase) ? " DESC" : " ASC";
                    return col + dir;
                });
                sb.AppendLine($"CREATE INDEX IF NOT EXISTS {DpUtil.ToSnake(ix.Name)} ON {schema}.{table} USING {ix.Method} ({string.Join(", ", parts)});");
            }
        }

        return sb.ToString();
    }

    private static SdField ResolveField(SdEntity e, string name) =>
        e.Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Поле '{name}' не найдено в сущности '{e.Name}'");

    /// <summary>
    /// Трансляция check-выражения DSL: имена канонических полей (camelCase)
    /// заменяются на физические колонки (snake_case) — детерминированный маппинг.
    /// </summary>
    private static string TranslateCheckExpr(SdEntity e, string expr)
    {
        foreach (var f in e.Fields.OrderByDescending(f => f.Name.Length))
            expr = Regex.Replace(expr, $@"\b{Regex.Escape(f.Name)}\b", Column(f));
        return expr;
    }

    private static string PgType(SdField f) => f.Canon switch
    {
        CanonType.Uuid or CanonType.Ref or CanonType.FileRef => "uuid",
        CanonType.String => $"varchar({f.Length ?? 256})",
        CanonType.Text => "text",
        CanonType.Boolean => "boolean",
        CanonType.Int32 => "integer",
        CanonType.Int64 => "bigint",
        CanonType.Decimal => $"numeric({f.Precision ?? 18},{f.Scale ?? 2})",
        CanonType.Money => "numeric(19,4)",
        CanonType.Date => "date",
        CanonType.Time => "time",
        CanonType.Timestamp => "timestamptz",
        CanonType.Json => "jsonb",
        CanonType.Binary => "bytea",
        CanonType.Enum => "varchar(64)",
        _ => throw new InvalidOperationException($"Тип {f.Type} не спроецирован в PostgreSQL")
    };

    private static string Nullability(SdField f) => f.Required ? " NOT NULL" : " NULL";

    private static string Default(SdField f)
    {
        if (string.IsNullOrWhiteSpace(f.Default)) return "";
        var d = f.Default.Trim();
        return d switch
        {
            "gen_random_uuid()" => " DEFAULT gen_random_uuid()",
            "now()" or "current_timestamp" => " DEFAULT now()",
            _ => $" DEFAULT {Literal(f, d)}"
        };
    }

    private static string Literal(SdField f, string raw)
    {
        switch (f.Canon)
        {
            case CanonType.Int32 or CanonType.Int64:
                if (!long.TryParse(raw, out _)) throw new InvalidOperationException($"default '{raw}' не является целым");
                return raw;
            case CanonType.Decimal or CanonType.Money:
                if (!decimal.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out _))
                    throw new InvalidOperationException($"default '{raw}' не является десятичным литералом");
                return raw;
            case CanonType.Boolean:
                return raw is "true" or "false" ? raw : throw new InvalidOperationException($"default '{raw}' не boolean");
            case CanonType.String or CanonType.Text or CanonType.Enum:
                return "'" + raw.Replace("'", "''") + "'";
            case CanonType.Date or CanonType.Time or CanonType.Timestamp:
                return "'" + raw.Replace("'", "''") + "'";
            default:
                throw new InvalidOperationException($"default для типа {f.Type} вне whitelist (MVP: литералы, gen_random_uuid(), now())");
        }
    }
}

public sealed record MigrationResult(string StoreId, string FromVersion, string ToVersion, string Status, string PackageHash, long DurationMs);

public sealed class Migrator
{
    private readonly NpgsqlDataSource _db;
    private readonly ILogger<Migrator> _logger;

    public Migrator(NpgsqlDataSource db, ILogger<Migrator> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Системные объекты платформы (схема dp): журнал, outbox, idempotency.</summary>
    public async Task EnsureSystemAsync()
    {
        const string sql = """
            CREATE SCHEMA IF NOT EXISTS dp;

            CREATE TABLE IF NOT EXISTS dp.migration_journal (
                id            uuid PRIMARY KEY,
                store_id      text        NOT NULL,
                from_version  text        NOT NULL,
                to_version    text        NOT NULL,
                package_hash  text        NOT NULL,
                status        text        NOT NULL,
                applied_by    text        NOT NULL,
                applied_at    timestamptz NOT NULL,
                duration_ms   integer     NOT NULL,
                detail        jsonb       NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS uq_mj_store_to
                ON dp.migration_journal (store_id, to_version)
                WHERE status <> 'failed';

            CREATE TABLE IF NOT EXISTS dp.outbox (
                id            uuid PRIMARY KEY,
                occurred_at   timestamptz NOT NULL,
                store_id      text        NOT NULL,
                store_version text        NOT NULL,
                entity        text        NOT NULL,
                op            text        NOT NULL,
                key           jsonb       NOT NULL,
                data          jsonb       NULL,
                previous      jsonb       NULL,
                tx_id         text        NOT NULL,
                source        text        NOT NULL DEFAULT 'provider.outbox',
                published_at  timestamptz NULL
            );
            CREATE INDEX IF NOT EXISTS ix_outbox_unpublished
                ON dp.outbox (occurred_at) WHERE published_at IS NULL;

            CREATE TABLE IF NOT EXISTS dp.idempotency (
                key             text PRIMARY KEY,
                store_id        text        NOT NULL,
                request_hash    text        NOT NULL,
                response_status integer     NOT NULL,
                response_body   jsonb       NOT NULL,
                created_at      timestamptz NOT NULL DEFAULT now()
            );
            """;
        await using var conn = await _db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
        _logger.LogInformation("Migrator: системные объекты dp.* готовы");
    }

    /// <summary>Идемпотентное применение пакетов всех store'ов (forward-only).</summary>
    public async Task<List<MigrationResult>> ApplyAllAsync(IEnumerable<SdDocument> stores)
    {
        var results = new List<MigrationResult>();
        foreach (var store in stores)
            results.Add(await ApplyStoreAsync(store));
        return results;
    }

    public async Task<MigrationResult> ApplyStoreAsync(SdDocument store)
    {
        var package = DdlGenerator.GenerateStorePackage(store);
        var hash = DpUtil.Sha256Hex(package);

        await using var conn = await _db.OpenConnectionAsync();

        var last = await GetLastAppliedAsync(conn, store.StoreId);
        if (last is not null && last.Value.ToVersion == store.Metadata.Version)
        {
            if (last.Value.PackageHash != hash)
                throw new DpException(DpCodes.MigrationPackageMismatch, 409,
                    $"Хеш пакета {store.StoreId}@{store.Metadata.Version} расходится с журналом: " +
                    $"journal={last.Value.PackageHash[..12]}…, generated={hash[..12]}…");
            return new MigrationResult(store.StoreId, last.Value.FromVersion, last.Value.ToVersion, "already", hash, 0);
        }

        var fromVersion = last?.ToVersion ?? "0.0.0";
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand(package, conn, tx))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand("""
                INSERT INTO dp.migration_journal
                    (id, store_id, from_version, to_version, package_hash, status, applied_by, applied_at, duration_ms, detail)
                VALUES
                    (@id, @store, @from, @to, @hash, 'applied', 'dp-api', now(), @duration, @detail::jsonb)
                """, conn, tx))
        {
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("@store", store.StoreId);
            cmd.Parameters.AddWithValue("@from", fromVersion);
            cmd.Parameters.AddWithValue("@to", store.Metadata.Version);
            cmd.Parameters.AddWithValue("@hash", hash);
            cmd.Parameters.AddWithValue("@duration", (int)sw.ElapsedMilliseconds);
            cmd.Parameters.AddWithValue("@detail", NpgsqlTypes.NpgsqlDbType.Jsonb,
                new JsonObject { ["statements"] = Regex.Matches(package, ";(?=(?:[^']*'[^']*')*[^']*$)").Count }.ToJsonString());
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        sw.Stop();
        _logger.LogInformation("Migrator: {Store} {From} → {To} применён за {Ms} мс (hash {Hash12}…)",
            store.StoreId, fromVersion, store.Metadata.Version, sw.ElapsedMilliseconds, hash[..12]);
        return new MigrationResult(store.StoreId, fromVersion, store.Metadata.Version, "applied", hash, sw.ElapsedMilliseconds);
    }

    private static async Task<(string FromVersion, string ToVersion, string PackageHash)?> GetLastAppliedAsync(
        NpgsqlConnection conn, string storeId)
    {
        await using var cmd = new NpgsqlCommand("""
                SELECT from_version, to_version, package_hash
                FROM dp.migration_journal
                WHERE store_id = @s AND status <> 'failed'
                ORDER BY applied_at DESC
                LIMIT 1
                """, conn);
        cmd.Parameters.AddWithValue("@s", storeId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return (r.GetString(0), r.GetString(1), r.GetString(2));
    }

    public async Task<JsonArray> JournalAsync()
    {
        await using var conn = await _db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
                SELECT store_id, from_version, to_version, package_hash, status, applied_by, applied_at, duration_ms
                FROM dp.migration_journal
                ORDER BY applied_at
                """, conn);
        var arr = new JsonArray();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            arr.Add(new JsonObject
            {
                ["storeId"] = r.GetString(0),
                ["fromVersion"] = r.GetString(1),
                ["toVersion"] = r.GetString(2),
                ["packageHash"] = r.GetString(3),
                ["status"] = r.GetString(4),
                ["appliedBy"] = r.GetString(5),
                ["appliedAt"] = Rows.Iso(Rows.AsDto(r.GetValue(6))),
                ["durationMs"] = r.GetInt32(7)
            });
        }
        return arr;
    }
}

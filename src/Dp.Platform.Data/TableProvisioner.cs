using Dp.Platform.Contracts.Schema;
using Dp.Platform.Framework;
using Npgsql;

namespace Dp.Platform.Data;

/// <summary>
/// Провизионер физических таблиц logical store (KISS-MVP):
/// CREATE TABLE IF NOT EXISTS + триггер outbox для CDC.
/// Все идентификаторы прошли валидацию реестра (безопасность по построению).
/// В prod DDL переносится в мигратор (Этап B) — точка расширения.
/// </summary>
public sealed class TableProvisioner(NpgsqlDataSource dataSource, ILogger<TableProvisioner> log)
{
    private readonly HashSet<string> _provisioned = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public async Task<StoreSchema> EnsureAsync(StoreSchema schema, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_provisioned.Contains(schema.Table)) return schema;
        }

        var columns = string.Join(",\n  ", BuildColumnSql(schema));
        var sql = $"""
            CREATE TABLE IF NOT EXISTS dp_data."{schema.Table}" (
              {columns},
              "__created_at" timestamptz NOT NULL DEFAULT now(),
              "__updated_at" timestamptz NOT NULL DEFAULT now(),
              PRIMARY KEY ("{schema.KeyField}")
            );
            """;

        var triggerSql = $"""
            DROP TRIGGER IF EXISTS trg_capture ON dp_data."{schema.Table}";
            CREATE TRIGGER trg_capture
            AFTER INSERT OR UPDATE OR DELETE ON dp_data."{schema.Table}"
            FOR EACH ROW EXECUTE FUNCTION dp_cdc.capture_change('{schema.Store}', '{schema.KeyField}');
            """;

        await using (var cmd = dataSource.CreateCommand(sql))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = dataSource.CreateCommand(triggerSql))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        lock (_gate)
        {
            _provisioned.Add(schema.Table);
        }
        log.LogInformation("Table dp_data.{Table} ensured for {Store}@{Version}", schema.Table, schema.Store, schema.Version);
        return schema;
    }

    private static IEnumerable<string> BuildColumnSql(StoreSchema schema)
    {
        foreach (var (name, kind) in schema.Fields)
        {
            var type = FieldKindMapper.ToPgType(kind);
            var notNull = kind is not FieldKind.Json && schema.KeyField == name ? " NOT NULL" : "";
            yield return $"\"{name}\" {type}{notNull}";
        }
        foreach (var ns in schema.Extensions.Keys)
            yield return $"\"ext_{ns}\" jsonb";
    }
}

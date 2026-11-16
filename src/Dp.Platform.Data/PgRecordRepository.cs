using System.Text.Json;
using Dp.Platform.Contracts.Data;
using Dp.Platform.Framework;
using Npgsql;

namespace Dp.Platform.Data;

/// <summary>Доступ к строкам logical store (SRP: только SQL-контракт, без бизнес-правил).</summary>
public interface IRecordRepository
{
    Task<Dictionary<string, object?>> InsertAsync(StoreSchema schema, Dictionary<string, object?> record, CancellationToken ct = default);
    Task<Dictionary<string, object?>?> GetAsync(StoreSchema schema, string key, CancellationToken ct = default);
    Task<PageDto> QueryAsync(StoreSchema schema, QueryDto query, CancellationToken ct = default);
    Task<Dictionary<string, object?>?> UpdateAsync(StoreSchema schema, string key, Dictionary<string, object?> fields, bool replace, bool upsert, CancellationToken ct = default);
    Task<long> PatchWhereAsync(StoreSchema schema, FilterNode? filter, Dictionary<string, object?> fields, CancellationToken ct = default);
    Task<bool> DeleteAsync(StoreSchema schema, string key, CancellationToken ct = default);
    Task<long> CountAsync(StoreSchema schema, FilterNode? filter, CancellationToken ct = default);
}

/// <summary>Композиция SQL-конструкторов и исполнения (тонкий класс, без построения текста).</summary>
public sealed class PgRecordRepository(NpgsqlDataSource dataSource, SqlFilterTranslator translator) : IRecordRepository
{
    public async Task<Dictionary<string, object?>> InsertAsync(
        StoreSchema schema, Dictionary<string, object?> record, CancellationToken ct = default)
    {
        var sink = new ParameterSink();
        var (columns, values) = RecordSqlBuilder.ColumnsForInsert(schema, record, sink);

        var sql = $"""
            INSERT INTO dp_data."{schema.Table}" AS x ({string.Join(", ", columns)})
            VALUES ({string.Join(", ", values)})
            RETURNING to_jsonb(x.*)
            """;
        return await ExecuteRowAsync(sql, sink, ct)
               ?? throw new DpFaultException(DpCodes.Internal, "Insert returned no row.");
    }

    public async Task<Dictionary<string, object?>?> GetAsync(
        StoreSchema schema, string key, CancellationToken ct = default)
    {
        var sink = new ParameterSink();
        return await ExecuteRowAsync(RecordSqlBuilder.SelectByKey(schema, sink, key), sink, ct);
    }

    public async Task<PageDto> QueryAsync(StoreSchema schema, QueryDto query, CancellationToken ct = default)
    {
        var sink = new ParameterSink();
        var filter = translator.Translate(schema, query.Filter);
        sink.AddRange(filter.Parameters);

        var limit = Math.Clamp(query.Limit is > 0 ? query.Limit : 50, 1, 500);
        var sql = $"""
            SELECT to_jsonb(x.*) FROM dp_data."{schema.Table}" AS x
            {RecordSqlBuilder.WhereClause(filter)} {RecordSqlBuilder.BuildOrder(schema, query.Sort)} LIMIT {limit} OFFSET {Math.Max(0, query.Offset)}
            """;

        var items = new List<Dictionary<string, object?>>();
        await using (var cmd = dataSource.CreateCommand(sql))
        {
            cmd.Parameters.AddRange(sink.Parameters.ToArray());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                items.Add(Deserialize(reader.GetString(0)));
        }
        return new PageDto(items, await CountAsync(schema, query.Filter, ct), limit, Math.Max(0, query.Offset));
    }

    public async Task<Dictionary<string, object?>?> UpdateAsync(
        StoreSchema schema, string key, Dictionary<string, object?> fields, bool replace, bool upsert, CancellationToken ct = default)
    {
        var sink = new ParameterSink();
        var assignments = RecordSqlBuilder.Assignments(schema, fields, replace, sink);

        if (upsert)
        {
            var (columns, values) = RecordSqlBuilder.ColumnsForInsert(schema, fields, sink);
            var sql = $"""
                INSERT INTO dp_data."{schema.Table}" AS x ({string.Join(", ", columns)})
                VALUES ({string.Join(", ", values)})
                ON CONFLICT ("{schema.KeyField}") DO UPDATE
                SET {string.Join(", ", assignments)}
                RETURNING to_jsonb(x.*)
                """;
            return await ExecuteRowAsync(sql, sink, ct)
                   ?? throw new DpFaultException(DpCodes.Internal, "Upsert returned no row.");
        }

        var update = $"""
            UPDATE dp_data."{schema.Table}" AS x
            SET {string.Join(", ", assignments)}, "__updated_at" = now()
            {RecordSqlBuilder.WhereByKey(schema, sink, key)}
            RETURNING to_jsonb(x.*)
            """;
        return await ExecuteRowAsync(update, sink, ct);
    }

    public async Task<long> PatchWhereAsync(
        StoreSchema schema, FilterNode? filter, Dictionary<string, object?> fields, CancellationToken ct = default)
    {
        var sink = new ParameterSink();
        var assignments = RecordSqlBuilder.Assignments(schema, fields, replace: false, sink);
        var translated = translator.Translate(schema, filter);
        sink.AddRange(translated.Parameters);

        var sql = $"""
            UPDATE dp_data."{schema.Table}" AS x
            SET {string.Join(", ", assignments)}, "__updated_at" = now()
            {RecordSqlBuilder.WhereClause(translated)}
            """;
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddRange(sink.Parameters.ToArray());
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteAsync(StoreSchema schema, string key, CancellationToken ct = default)
    {
        var sink = new ParameterSink();
        var sql = $"DELETE FROM dp_data.\"{schema.Table}\" {RecordSqlBuilder.WhereByKey(schema, sink, key)}";
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddRange(sink.Parameters.ToArray());
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<long> CountAsync(StoreSchema schema, FilterNode? filter, CancellationToken ct = default)
    {
        var sink = new ParameterSink();
        var translated = translator.Translate(schema, filter);
        sink.AddRange(translated.Parameters);
        var sql = $"SELECT count(*) FROM dp_data.\"{schema.Table}\" {RecordSqlBuilder.WhereClause(translated)}";
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddRange(sink.Parameters.ToArray());
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<Dictionary<string, object?>?> ExecuteRowAsync(string sql, ParameterSink sink, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddRange(sink.Parameters.ToArray());
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        return json is null ? null : Deserialize(json);
    }

    private static Dictionary<string, object?> Deserialize(string json)
        => JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? [];
}

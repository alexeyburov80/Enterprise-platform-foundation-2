using Npgsql;

namespace Dp.Platform.Cdc;

/// <summary>Строка outbox (транзакционный ящик: пишется тем же COMMIT, что и данные).</summary>
public sealed record OutboxRow(long Id, string Store, string Op, string Key, string? PayloadJson);

/// <summary>
/// Транзакционный outbox (SRP: только SQL и границы транзакции).
/// Публикация идёт ВНУТРИ транзакции: при сбое транспорта — откат, строки
/// остаются в очереди; при успехе — помечаются и коммитятся. At-least-once.
/// </summary>
public interface IOutboxProcessor
{
    Task<int> ProcessPendingAsync(int batchSize, Func<OutboxRow, CancellationToken, Task> publish, CancellationToken ct = default);
}

public sealed class PgOutboxProcessor(NpgsqlDataSource dataSource) : IOutboxProcessor
{
    public async Task<int> ProcessPendingAsync(
        int batchSize, Func<OutboxRow, CancellationToken, Task> publish, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        List<OutboxRow> rows;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT id, store, op, key, payload::text FROM dp_cdc.outbox
                WHERE processed_at IS NULL
                ORDER BY id
                LIMIT @batch
                FOR UPDATE SKIP LOCKED
                """;
            select.Parameters.AddWithValue("batch", batchSize);

            rows = [];
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new OutboxRow(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        foreach (var row in rows)
        {
            await publish(row, ct);
        }

        if (rows.Count > 0)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE dp_cdc.outbox SET processed_at = now() WHERE id = ANY(@ids)";
            update.Parameters.AddWithValue("ids", rows.Select(r => r.Id).ToArray());
            await update.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return rows.Count;
    }
}

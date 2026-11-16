using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Dp.Platform.Migrator;

/// <summary>
/// Forward-only мигратор (Этап A): применяет *.sql по порядку имён,
/// пишет dp_meta.migration_journal и защищает историю:
/// изменение уже применённого файла останавливает запуск (checksum mismatch).
/// Expand/contract обеспечивается самими скриптами.
/// </summary>
public sealed class MigrationRunner(string connectionString, string migrationsDir, Action<string> log)
{
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await EnsureJournalAsync(connection, ct);

        var applied = await LoadJournalAsync(connection, ct);
        var files = Directory.GetFiles(migrationsDir, "*.sql").OrderBy(f => f, StringComparer.Ordinal).ToList();

        var executed = 0;
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var checksum = await ChecksumAsync(file, ct);

            if (applied.TryGetValue(name, out var record))
            {
                if (record != checksum)
                    throw new InvalidOperationException(
                        $"Migration '{name}' was modified after being applied (journal {record}, file {checksum}). " +
                        "Forward-only policy: create a new migration instead.");
                continue;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
            try
            {
                var sql = await File.ReadAllTextAsync(file, ct);
                await using (var batch = new NpgsqlBatch(connection, transaction))
                {
                    foreach (var statement in SqlSplitter.Split(sql))
                    {
                        batch.BatchCommands.Add(new NpgsqlBatchCommand(statement));
                    }
                    await batch.ExecuteNonQueryAsync(ct);
                }

                await using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO dp_meta.migration_journal(name, checksum, applied_at, duration_ms)
                        VALUES (@name, @checksum, now(), @duration)
                        """;
                    insert.Parameters.AddWithValue("name", name);
                    insert.Parameters.AddWithValue("checksum", checksum);
                    insert.Parameters.AddWithValue("duration", stopwatch.ElapsedMilliseconds);
                    await insert.ExecuteNonQueryAsync(ct);
                }

                await transaction.CommitAsync(ct);
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }

            executed++;
            log($"[migrator] applied {name} in {stopwatch.ElapsedMilliseconds} ms");
        }

        log($"[migrator] done: {executed} applied, {applied.Count} already in journal, {files.Count} total");
        return executed;
    }

    private static async Task EnsureJournalAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE SCHEMA IF NOT EXISTS dp_meta
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        await using var table = connection.CreateCommand();
        table.CommandText = """
            CREATE TABLE IF NOT EXISTS dp_meta.migration_journal (
              id serial PRIMARY KEY,
              name text NOT NULL UNIQUE,
              checksum text NOT NULL,
              applied_at timestamptz NOT NULL DEFAULT now(),
              duration_ms bigint NOT NULL
            )
            """;
        await table.ExecuteNonQueryAsync(ct);
    }

    private static async Task<Dictionary<string, string>> LoadJournalAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name, checksum FROM dp_meta.migration_journal";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }
        return result;
    }

    private static async Task<string> ChecksumAsync(string file, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(file, ct);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}

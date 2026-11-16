using Dp.Platform.Contracts.Files;
using Dp.Platform.Framework;
using Npgsql;

namespace Dp.Platform.Files;

/// <summary>Доступ к метаданным файлов (SRP: только SQL, без HTTP и файловой системы).</summary>
public interface IFileMetaRepository
{
    Task UpsertAsync(FileMetaDto meta, CancellationToken ct = default);
    Task<FileMetaDto?> GetAsync(string id, CancellationToken ct = default);
    Task<bool> MarkDeletedAsync(string id, CancellationToken ct = default);
}

public sealed class PgFileMetaRepository(NpgsqlDataSource dataSource) : IFileMetaRepository
{
    public async Task UpsertAsync(FileMetaDto meta, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO dp_files.files(id, size, content_type, original_name, created_at)
            VALUES (@id, @size, @contentType, @originalName, now())
            ON CONFLICT (id) DO UPDATE
            SET deleted_at = NULL, original_name = @originalName, content_type = @contentType
            """);
        cmd.Parameters.AddWithValue("id", meta.Id);
        cmd.Parameters.AddWithValue("size", meta.Size);
        cmd.Parameters.AddWithValue("contentType", meta.ContentType);
        cmd.Parameters.AddWithValue("originalName", meta.OriginalName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<FileMetaDto?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT id, size, content_type, original_name, created_at, deleted_at
            FROM dp_files.files WHERE id = @id
            """);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new FileMetaDto
        {
            Id = reader.GetString(0),
            Size = reader.GetInt64(1),
            ContentType = reader.GetString(2),
            OriginalName = reader.GetString(3),
            CreatedAt = new DateTimeOffset(reader.GetDateTime(4)),
            DeletedAt = reader.IsDBNull(5) ? null : new DateTimeOffset(reader.GetDateTime(5)),
        };
    }

    public async Task<bool> MarkDeletedAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "UPDATE dp_files.files SET deleted_at = now() WHERE id = @id AND deleted_at IS NULL");
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }
}

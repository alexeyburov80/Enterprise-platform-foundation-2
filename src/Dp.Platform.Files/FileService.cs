using Dp.Platform.Contracts.Files;
using Dp.Platform.Framework;

namespace Dp.Platform.Files;

/// <summary>
/// Сервис файлов (SRP): контент -> IBlobStore, метаданные -> репозиторий.
/// Дедупликация бесплатна: одинаковый контент = один blob = тот же Id.
/// Tombstone: удаление помечает строку; физическая чистка — задача GC (Этап B).
/// </summary>
public sealed class FileService(IBlobStore blobs, IFileMetaRepository meta, FilesOptions options)
{
    public async Task<Result<FileMetaDto>> UploadAsync(Stream content, string contentType, string originalName, CancellationToken ct = default)
    {
        if (content.CanSeek && content.Length > options.MaxBytes)
            return Result<FileMetaDto>.Fail(DpCodes.FileTooLarge,
                $"File exceeds limit {options.MaxBytes} bytes.", new { size = content.Length, limit = options.MaxBytes });

        if (content.CanSeek) content.Position = 0;
        var (hash, size) = await blobs.SaveAsync(content, ct);

        var stored = new FileMetaDto
        {
            Id = hash,
            Size = size,
            ContentType = contentType,
            OriginalName = originalName,
        };
        await meta.UpsertAsync(stored, ct);
        return stored;
    }

    public async Task<Result<(FileMetaDto Meta, Stream Content)>> DownloadAsync(string id, CancellationToken ct = default)
    {
        var metadata = await meta.GetAsync(id, ct);
        if (metadata is null || metadata.DeletedAt is not null)
            return Result<(FileMetaDto, Stream)>.Fail(DpCodes.FileNotFound, $"File '{id}' not found.");

        var content = await blobs.OpenAsync(id, ct);
        return content is null
            ? Result<(FileMetaDto, Stream)>.Fail(DpCodes.FileCorrupted, "Blob is missing but metadata exists.", new { id })
            : (metadata, content);
    }

    public async Task<Result<FileMetaDto>> GetMetaAsync(string id, CancellationToken ct = default)
    {
        var metadata = await meta.GetAsync(id, ct);
        return metadata is { DeletedAt: null }
            ? metadata
            : Result<FileMetaDto>.Fail(DpCodes.FileNotFound, $"File '{id}' not found.");
    }

    public async Task<Result<FileMetaDto>> DeleteAsync(string id, CancellationToken ct = default)
    {
        var metadata = await meta.GetAsync(id, ct);
        if (metadata is null || metadata.DeletedAt is not null)
            return Result<FileMetaDto>.Fail(DpCodes.FileNotFound, $"File '{id}' not found.");
        await meta.MarkDeletedAsync(id, ct);
        return metadata with { DeletedAt = DateTimeOffset.UtcNow };
    }
}

public sealed class FilesOptions
{
    public const string SectionName = "Files";
    public string DataRoot { get; set; } = "/data/blobs";
    public long MaxBytes { get; set; } = 2L * 1024 * 1024 * 1024; // 2 ГБ (Этап A)
}

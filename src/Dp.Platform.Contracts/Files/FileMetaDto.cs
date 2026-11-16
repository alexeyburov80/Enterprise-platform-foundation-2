namespace Dp.Platform.Contracts.Files;

/// <summary>Метаданные файла content-addressed хранилища. Id = SHA-256 контента.</summary>
public sealed record FileMetaDto
{
    public required string Id { get; init; }
    public long Size { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
    public string OriginalName { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
}

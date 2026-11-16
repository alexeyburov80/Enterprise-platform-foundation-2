namespace Dp.Platform.Framework;

/// <summary>
/// Контентно-адресованное хранилище файлов (Этап A: SHA-256 = адрес, blobs/ab/cd/&lt;hash&gt;).
/// DIP: сервис файлов не знает детали файловой системы.
/// </summary>
public interface IBlobStore
{
    Task<(string Hash, long Size)> SaveAsync(Stream content, CancellationToken ct = default);
    Task<Stream?> OpenAsync(string hash, CancellationToken ct = default);
    Task<bool> ExistsAsync(string hash, CancellationToken ct = default);
    Task<bool> DeleteAsync(string hash, CancellationToken ct = default);
}

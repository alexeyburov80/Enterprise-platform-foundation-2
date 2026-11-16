using System.Security.Cryptography;

namespace Dp.Platform.Framework;

/// <summary>
/// Content-addressed реализация IBlobStore (Этап A): одинаковый контент = один файл,
/// дедупликация бесплатна, запись атомарна (temp + move), путь безопасен по построению.
/// Хеш считается за один проход при записи (поток не обязан поддерживать Position).
/// </summary>
public sealed class ContentAddressedBlobStore : IBlobStore
{
    private readonly string _root;

    public ContentAddressedBlobStore(string root) => _root = root;

    public async Task<(string Hash, long Size)> SaveAsync(Stream content, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);
        var tempPath = Path.Combine(_root, $"tmp-{Guid.NewGuid():N}");

        string hash;
        long size;
        try
        {
            await using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var hasher = SHA256.Create();
                await using (var hashing = new CryptoStream(temp, hasher, CryptoStreamMode.Write, leaveOpen: true))
                {
                    await content.CopyToAsync(hashing, ct);
                }
                // CryptoStream при освобождении завершает финальный блок — Hash готов.
                hash = Convert.ToHexString(hasher.Hash ?? throw new InvalidOperationException("Hash not computed."))
                    .ToLowerInvariant();
                size = temp.Length;
            }

            var finalPath = PathFor(hash);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            if (File.Exists(finalPath))
            {
                // Дедупликация: контент уже сохранён, временный файл не нужен.
                File.Delete(tempPath);
            }
            else
            {
                File.Move(tempPath, finalPath);
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        return (hash, size);
    }

    public Task<Stream?> OpenAsync(string hash, CancellationToken ct = default)
    {
        ThrowIfNotHash(hash);
        var path = PathFor(hash);
        return Task.FromResult<Stream?>(
            File.Exists(path)
                ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                : null);
    }

    public Task<bool> ExistsAsync(string hash, CancellationToken ct = default)
        => Task.FromResult(File.Exists(PathFor(hash)));

    public Task<bool> DeleteAsync(string hash, CancellationToken ct = default)
    {
        ThrowIfNotHash(hash);
        var path = PathFor(hash);
        if (!File.Exists(path)) return Task.FromResult(false);
        File.Delete(path);
        return Task.FromResult(true);
    }

    private string PathFor(string hash) => Path.Combine(_root, hash[..2], hash[2..4], hash);

    private static void ThrowIfNotHash(string hash)
    {
        if (hash.Length != 64 || !hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("Hash must be lower-case SHA-256 hex string.", nameof(hash));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* лучшее усилие при очистке */ }
    }
}

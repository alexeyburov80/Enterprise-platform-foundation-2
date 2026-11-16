using System.Collections.Concurrent;
using System.Text.Json;
using Dp.Platform.Contracts.Schema;
using Microsoft.Extensions.Options;

namespace Dp.Platform.Cdc;

/// <summary>
/// Резолвинг актуальной версии logical store через API реестра (TTL-кеш).
/// MVP-упрощение: событие несёт актуальную версию store; точную версию на
/// момент изменения даёт Этап B (version в expanded-колонках outbox).
/// </summary>
public sealed class VersionResolver(HttpClient http, IOptions<CdcOptions> options)
{
    private sealed record Cached(string Version, DateTimeOffset LoadedAt);
    private readonly ConcurrentDictionary<string, Cached> _cache = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public async Task<string> GetVersionAsync(string store, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(store, out var cached) && DateTimeOffset.UtcNow - cached.LoadedAt < Ttl)
            return cached.Version;

        try
        {
            var url = $"{options.Value.RegistryBaseUrl.TrimEnd('/')}/api/v1/stores/{store}/versions/current";
            var definition = await http.GetFromJsonAsync<StoreDefinition>(url, ct);
            if (definition is not null)
            {
                _cache[store] = new Cached(definition.Version, DateTimeOffset.UtcNow);
                return definition.Version;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // Реестр недоступен: не блокируем публикацию, версия будет "0.0.0".
        }
        return "0.0.0";
    }
}

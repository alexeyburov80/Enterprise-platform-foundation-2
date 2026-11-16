using System.Collections.Concurrent;
using System.Text.Json;
using Dp.Platform.Contracts.Schema;
using Dp.Platform.Framework;
using Microsoft.Extensions.Options;

namespace Dp.Platform.Data;

/// <summary>
/// Клиент реестра схем с TTL-кешированием (SRP: только резолвинг схем).
/// Реестр недоступен/неизвестный store — единые коды ошибок DP-1010/DP-1011.
/// </summary>
public sealed class RegistryClient(
    HttpClient http,
    IOptions<RegistryOptions> options,
    ILogger<RegistryClient> log) : IStoreSchemaProvider
{
    private sealed record Cached(StoreSchema Schema, DateTimeOffset LoadedAt);

    private readonly ConcurrentDictionary<string, Cached> _cache = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public async Task<Result<StoreSchema>> GetAsync(string store, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(store, out var cached) && DateTimeOffset.UtcNow - cached.LoadedAt < Ttl)
            return cached.Schema;

        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(
                $"{options.Value.BaseUrl.TrimEnd('/')}/api/v1/stores/{store}/versions/current", ct);
        }
        catch (HttpRequestException ex)
        {
            log.LogError(ex, "Registry unreachable");
            return Result<StoreSchema>.Fail(DpCodes.StoreNotFound,
                "Schema registry is unreachable.", new { store });
        }

        if (!response.IsSuccessStatusCode)
            return Result<StoreSchema>.Fail(DpCodes.StoreNotFound,
                $"Store '{store}' is not registered (or has no active version).", new { store, status = (int)response.StatusCode });

        var definition = await response.Content.ReadFromJsonAsync<StoreDefinition>(ct);
        if (definition is null)
            return Result<StoreSchema>.Fail(DpCodes.StoreNotFound, "Registry returned empty definition.", new { store });

        var schema = StoreSchema.FromDefinition(definition);
        _cache[store] = new Cached(schema, DateTimeOffset.UtcNow);
        return schema;
    }
}

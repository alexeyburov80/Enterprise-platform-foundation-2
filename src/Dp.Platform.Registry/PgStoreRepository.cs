using Dp.Platform.Contracts.Schema;
using Dp.Platform.Framework;
using Npgsql;
using NpgsqlTypes;

namespace Dp.Platform.Registry;

/// <summary>Хранилище определений (SRP: только доступ к данным, без правил совместимости).</summary>
public interface IStoreRepository
{
    Task UpsertStoreAsync(string store, CancellationToken ct = default);
    Task InsertVersionAsync(StoreDefinition definition, CancellationToken ct = default);
    Task<StoreDefinition?> GetVersionAsync(string store, string version, CancellationToken ct = default);
    Task<StoreDefinition?> GetLatestAsync(string store, CancellationToken ct = default);
    Task<IReadOnlyList<StoreVersionInfo>> ListVersionsAsync(string? store, CancellationToken ct = default);
}

/// <summary>PostgreSQL-реализация (schema dp.registry). Прямой доступ к чужим схемам запрещён.</summary>
public sealed class PgStoreRepository(NpgsqlDataSource dataSource) : IStoreRepository
{
    public async Task UpsertStoreAsync(string store, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO dp_registry.stores(name) VALUES (@name) ON CONFLICT (name) DO NOTHING");
        cmd.Parameters.AddWithValue("name", store);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertVersionAsync(StoreDefinition definition, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO dp_registry.store_versions(store, version, definition, registered_at)
            VALUES (@store, @version, @definition::jsonb, now())
            """);
        cmd.Parameters.AddWithValue("store", definition.Store);
        cmd.Parameters.AddWithValue("version", definition.Version);
        cmd.Parameters.AddWithValue("definition", NpgsqlDbType.Jsonb, System.Text.Json.JsonSerializer.Serialize(definition));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<StoreDefinition?> GetVersionAsync(string store, string version, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT definition FROM dp_registry.store_versions WHERE store = @store AND version = @version");
        cmd.Parameters.AddWithValue("store", store);
        cmd.Parameters.AddWithValue("version", version);
        return await ReadSingleAsync(cmd, ct);
    }

    public async Task<StoreDefinition?> GetLatestAsync(string store, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT definition FROM dp_registry.store_versions
            WHERE store = @store ORDER BY registered_at DESC, version DESC LIMIT 1
            """);
        cmd.Parameters.AddWithValue("store", store);
        return await ReadSingleAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<StoreVersionInfo>> ListVersionsAsync(string? store, CancellationToken ct = default)
    {
        var query = store is null
            ? "SELECT store, version, definition, registered_at FROM dp_registry.store_versions ORDER BY store, registered_at"
            : "SELECT store, version, definition, registered_at FROM dp_registry.store_versions WHERE store = @store ORDER BY registered_at";
        await using var cmd = dataSource.CreateCommand(query);
        if (store is not null) cmd.Parameters.AddWithValue("store", store);

        var list = new List<StoreVersionInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var definition = System.Text.Json.JsonSerializer.Deserialize<StoreDefinition>(
                reader.GetString(2))!;
            list.Add(new StoreVersionInfo
            {
                Store = reader.GetString(0),
                Version = reader.GetString(1),
                Definition = definition,
                RegisteredAt = new DateTimeOffset(reader.GetDateTime(3)),
            });
        }
        return list;
    }

    private static async Task<StoreDefinition?> ReadSingleAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        return json is null ? null : System.Text.Json.JsonSerializer.Deserialize<StoreDefinition>(json);
    }
}

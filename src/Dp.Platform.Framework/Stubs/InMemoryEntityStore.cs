using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Dp.Platform.Framework.Stubs;

/// <summary>
/// Потокобезопасное in-memory хранилище сущностей для имитаторов (KISS:
/// состояние живёт в памяти, персистентность имитатору не нужна).
/// </summary>
public sealed class InMemoryEntityStore
{
    private readonly ConcurrentDictionary<string, JsonNode> _entities = new();

    public IReadOnlyList<(string Id, JsonNode Entity)> GetAll()
        => _entities.Select(kv => (kv.Key, kv.Value)).OrderBy(kv => kv.Key).ToList();

    public JsonNode? Get(string id) => _entities.TryGetValue(id, out var entity) ? entity : null;

    public string Add(JsonNode entity)
    {
        var id = Guid.NewGuid().ToString();
        entity["id"] = id;
        _entities[id] = entity;
        return id;
    }

    public bool AddOrUpdate(string id, JsonNode entity)
    {
        entity["id"] = id;
        _entities[id] = entity;
        return true;
    }

    public bool Delete(string id) => _entities.TryRemove(id, out _);

    public int Count => _entities.Count;
}

/// <summary>Рекурсивное слияние PATCH-семантики для JSON (неглубокие объекты склеиваются).</summary>
public static class JsonMergeKit
{
    public static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, patchValue) in patch)
        {
            if (patchValue is null)
            {
                target.Remove(key);
                continue;
            }
            if (target[key] is JsonObject targetObject && patchValue is JsonObject patchObject)
            {
                Merge(targetObject, patchObject);
                continue;
            }
            target[key] = patchValue.DeepClone();
        }
    }
}

using Dp.Platform.Framework;
using Dp.Platform.Framework.Stubs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Text.Json.Nodes;

namespace Dp.Platform.Stub.Bitrix;

/// <summary>
/// Имитатор REST-вебхуков Битрикс24: crm.deal.list/get/add/update/delete.
/// Живёт в СВОЁМ контейнере; тумблер отказа — для испытания замещения.
/// </summary>
public static class BitrixRestEndpoints
{
    public static WebApplication MapBitrixStub(this WebApplication app)
    {
        var store = new InMemoryEntityStore();
        foreach (var deal in DealSeed.Create(10))
        {
            store.AddOrUpdate(deal["ID"]!.GetValue<string>(), deal);
        }

        var webhookKey = app.Configuration["StubBitrix:WebhookKey"] ?? "demo";

        app.MapPost("/rest/{key}/crm.deal.{method}.json", async (
            string key, string method, HttpRequest request, CancellationToken ct) =>
        {
            if (!key.Equals(webhookKey, StringComparison.Ordinal))
                return Results.Json(BitrixError("INVALID_CREDENTIALS", "Wrong webhook key."), statusCode: 401);

            JsonObject? body = null;
            if (request.ContentLength > 0)
            {
                body = await JsonNode.ParseAsync(request.Body, cancellationToken: ct) as JsonObject;
            }
            body ??= [];

            return method switch
            {
                "list" => List(store, body),
                "get" => Get(store, body),
                "add" => Add(store, body),
                "update" => Update(store, body),
                "delete" => Delete(store, body),
                _ => Results.Json(BitrixError("METHOD_NOT_FOUND", $"Unknown method crm.deal.{method}."), statusCode: 404),
            };
        });

        // Совместимость с реальными вебхуками: список отдаётся и по GET (с ключом и без).
        app.MapGet("/rest/{key}/crm.deal.list.json", (string key) =>
            key.Equals(webhookKey, StringComparison.Ordinal)
                ? Results.Json(new { result = store.GetAll().Select(p => p.Entity).ToList(), total = store.Count })
                : Results.Json(BitrixError("INVALID_CREDENTIALS", "Wrong webhook key."), statusCode: 401));
        app.MapGet("/rest/crm.deal.list.json", () =>
            Results.Json(new { result = store.GetAll().Select(p => p.Entity).ToList(), total = store.Count }));

        // Совместимость: вызовы без ключа в пути, ключ в body.session.
        app.MapPost("/rest/crm.deal.{method}.json", async (
            string method, HttpRequest request, CancellationToken ct) =>
        {
            var body = request.ContentLength > 0
                ? await JsonNode.ParseAsync(request.Body, cancellationToken: ct) as JsonObject
                : null;
            var session = body?["session"]?.GetValue<string>();
            return session == webhookKey
                ? Results.Json(BitrixError("WRONG_ENTRY_POINT", "Use /rest/{key}/crm.deal.{method}.json"), statusCode: 404)
                : Results.Json(BitrixError("INVALID_CREDENTIALS", "Webhook key required."), statusCode: 401);
        });

        return app;
    }

    private static IResult List(InMemoryEntityStore store, JsonObject body)
    {
        var items = store.GetAll().Select(pair => (JsonObject)pair.Entity).ToList();
        if (body["filter"] is JsonObject filter)
        {
            foreach (var (rawField, rawValue) in filter)
            {
                var (op, field) = ParseFilterKey(rawField);
                var predicate = FilterKit.Compile(field, op, rawValue);
                items = items.Where(entity => predicate(entity)).ToList();
            }
        }
        if (body["select"] is JsonArray select)
        {
            var names = select.Select(n => n?.GetValue<string>()).Where(n => n is not null)
                .Cast<string>().ToHashSet();
            items = items.Select(entity => Project(entity, names)).ToList();
        }
        return Results.Json(new { result = items, total = items.Count });
    }

    private static IResult Get(InMemoryEntityStore store, JsonObject body)
    {
        var id = body["ID"]?.GetValue<string>() ?? body["id"]?.GetValue<string>();
        return store.Get(id ?? "") is { } entity
            ? Results.Json(new { result = entity })
            : Results.Json(BitrixError("NOT_FOUND", $"Deal {id} not found."), statusCode: 404);
    }

    private static IResult Add(InMemoryEntityStore store, JsonObject body)
    {
        if (body["fields"] is not JsonObject fields)
            return Results.Json(BitrixError("PARAMETERS", "'fields' object required."), statusCode: 400);

        var nextId = store.GetAll().Count + 1;
        var id = nextId.ToString();
        fields["ID"] = id;
        fields["DATE_CREATE"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        fields["UPDATED_TIME"] = fields["DATE_CREATE"];
        store.AddOrUpdate(id, fields);
        return Results.Json(new { result = nextId });
    }

    private static IResult Update(InMemoryEntityStore store, JsonObject body)
    {
        var id = body["ID"]?.GetValue<string>();
        if (store.Get(id ?? "") is not JsonObject entity || body["fields"] is not JsonObject patch)
            return Results.Json(BitrixError("NOT_FOUND", $"Deal {id} not found."), statusCode: 404);

        JsonMergeKit.Merge(entity, patch);
        entity["UPDATED_TIME"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        return Results.Json(new { result = true });
    }

    private static IResult Delete(InMemoryEntityStore store, JsonObject body)
    {
        var id = body["ID"]?.GetValue<string>();
        return store.Delete(id ?? "")
            ? Results.Json(new { result = true })
            : Results.Json(BitrixError("NOT_FOUND", $"Deal {id} not found."), statusCode: 404);
    }

    private static JsonObject Project(JsonObject entity, HashSet<string> names)
    {
        var copy = new JsonObject();
        foreach (var (name, value) in entity)
        {
            if (names.Contains(name)) copy[name] = value?.DeepClone();
        }
        return copy;
    }

    private static (string Op, string Field) ParseFilterKey(string key)
        => key switch
        {
            var k when k.StartsWith(">=") => ("ge", k[2..]),
            var k when k.StartsWith("<=") => ("le", k[2..]),
            var k when k.StartsWith('>') => ("gt", k[1..]),
            var k when k.StartsWith('<') => ("lt", k[1..]),
            var k when k.StartsWith('!') => ("ne", k[1..]),
            _ => ("eq", key),
        };

    private static JsonObject BitrixError(string code, string description) => new()
    {
        ["error"] = code,
        ["error_description"] = description,
    };
}

using Dp.Platform.Framework;
using Dp.Platform.Framework.Stubs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text.Json.Nodes;

namespace Dp.Platform.Stub.OneC;

/// <summary>
/// Имитатор API 1С: OData-эндпоинты Document_SalesOrder. Живёт в СВОЁМ
/// контейнере; используется адаптером как замещение при недоступной 1С
/// и для опробования переключения real<->stub.
/// </summary>
public static class OneCOdataEndpoints
{
    public static WebApplication MapOneCStub(this WebApplication app)
    {
        var store = new InMemoryEntityStore();
        foreach (var order in SalesOrderSeed.Create(10))
        {
            store.AddOrUpdate(order["Ref_Key"]!.GetValue<string>(), order);
        }

        var odata = app.MapGroup("/odata/Standard.odata");

        odata.MapGet("/$metadata", () => Results.Text(
            OdataMetadata.Xml, "application/xml"));

        odata.MapGet("/Document_SalesOrder", (string? filter, int? top, int? skip) =>
        {
            var predicate = filter is null
                ? (Func<JsonNode, bool>)(_ => true)
                : OdataFilterParser.Parse(filter);
            var items = store.GetAll()
                .Where(pair => predicate(pair.Entity))
                .Skip(skip ?? 0)
                .Take(top ?? 100)
                .Select(pair => pair.Entity)
                .ToList();
            return Results.Json(new { value = items });
        });

        odata.MapGet("/Document_SalesOrder({key})", (string key) =>
        {
            var entity = store.Get(key.Trim('\'', '"'));
            return entity is null
                ? Results.Json(new { error = "Entity not found" }, statusCode: 404)
                : Results.Json(entity);
        });

        odata.MapPost("/Document_SalesOrder", async (HttpRequest request) =>
        {
            var node = await JsonNode.ParseAsync(request.Body);
            if (node is not JsonObject entity)
                return Results.Json(new { error = "JSON object expected" }, statusCode: 400);

            var key = entity["Ref_Key"]?.GetValue<string>() ?? Guid.NewGuid().ToString();
            entity["Ref_Key"] = key;
            store.AddOrUpdate(key, entity);
            return Results.Json(entity, statusCode: 201);
        });

        odata.MapPatch("/Document_SalesOrder({key})", async (string key, HttpRequest request) =>
        {
            var patch = await JsonNode.ParseAsync(request.Body);
            var current = store.Get(key.Trim('\'', '"'));
            if (current is not JsonObject currentObject || patch is not JsonObject patchObject)
                return Results.Json(new { error = "Entity not found" }, statusCode: 404);

            JsonMergeKit.Merge(currentObject, patchObject);
            return Results.Json(currentObject);
        });

        odata.MapDelete("/Document_SalesOrder({key})", (string key) =>
            store.Delete(key.Trim('\'', '"')) ? Results.NoContent() : Results.NotFound());

        return app;
    }
}

/// <summary>Парсер простых OData-$filter: a op v (and|or b op v)*, op: eq/ne/gt/ge/lt/le.</summary>
public static class OdataFilterParser
{
    public static Func<JsonNode, bool> Parse(string filter)
    {
        var predicates = Split(filter);
        var compiled = predicates.Select(p => FilterKit.Compile(p.Field, p.Op, p.Value)).ToList();
        var connectors = Connectors(filter);

        return entity =>
        {
            var result = compiled[0](entity);
            for (var i = 1; i < compiled.Count; i++)
            {
                result = connectors[i - 1] == "or"
                    ? result || compiled[i](entity)
                    : result && compiled[i](entity);
            }
            return result;
        };
    }

    private static string[] Connectors(string filter)
    {
        var connectors = new List<string>();
        foreach (var token in filter.Split(' '))
        {
            if (token.Equals("and", StringComparison.OrdinalIgnoreCase)) connectors.Add("and");
            if (token.Equals("or", StringComparison.OrdinalIgnoreCase)) connectors.Add("or");
        }
        return [.. connectors];
    }

    private static List<(string Field, string Op, JsonNode? Value)> Split(string filter)
    {
        var parts = System.Text.RegularExpressions.Regex
            .Split(filter, @"\s+(and|or)\s+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Where(p => p is not ("and" or "or" or "And" or "Or" or "AND" or "OR"))
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        var result = new List<(string, string, JsonNode?)>();
        foreach (var part in parts)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                part, @"(\w+)\s+(eq|ne|gt|ge|lt|le)\s+(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            result.Add((match.Groups[1].Value, match.Groups[2].Value.ToLowerInvariant(), ParseValue(match.Groups[3].Value.Trim())));
        }
        return result;
    }

    private static JsonNode? ParseValue(string token)
    {
        // datetime'2025-01-01T00:00:00' | guid'000...' | 'text' | 123 | true
        var quoted = System.Text.RegularExpressions.Regex.Match(token, @"^\w+'(.+)'$");
        if (quoted.Success) return JsonValue.Create(quoted.Groups[1].Value);
        if (token.StartsWith('\'') && token.EndsWith('\'')) return JsonValue.Create(token.Trim('\''));
        if (bool.TryParse(token, out var flag)) return JsonValue.Create(flag);
        if (double.TryParse(token, out var number)) return JsonValue.Create(number);
        return JsonValue.Create(token);
    }
}

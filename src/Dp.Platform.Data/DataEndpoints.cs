using Dp.Platform.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Text.Json;

namespace Dp.Platform.Data;

/// <summary>
/// HTTP-контракт Data Access API: 9 операций (Этап A). Каждый маршрут —
/// одна строка вызова резолвера: добавление маршрута не трогает операции.
/// </summary>
public static class DataEndpoints
{
    public static WebApplication MapDataEndpoints(this WebApplication app)
    {
        var operations = app.Services.GetRequiredService<DataOperations>();

        var api = app.MapGroup("/api/v1/data");

        api.MapPost("/{store}", (string store, JsonElement body, HttpContext http, CancellationToken ct)
            => operations.InvokeAsync("insert", store, null, body, http, ct));
        api.MapPost("/{store}/bulk", (string store, JsonElement body, HttpContext http, CancellationToken ct)
            => operations.InvokeAsync("bulk-insert", store, null, body, http, ct));
        api.MapPost("/{store}/query", (string store, JsonElement body, HttpContext http, CancellationToken ct)
            => operations.InvokeAsync("query", store, null, body, http, ct));
        api.MapPatch("/{store}", (string store, JsonElement body, HttpContext http, CancellationToken ct)
            => operations.InvokeAsync("patch-all", store, null, body, http, ct));
        api.MapGet("/{store}/count", (string store, HttpContext http, CancellationToken ct)
            => operations.InvokeAsync("count", store, null, null, http, ct));

        api.MapGet("/{store}/{key}", (string store, string key, HttpContext http, CancellationToken ct)
            => operations.InvokeAsync("get", store, key, null, http, ct));
        api.MapPatch("/{store}/{key}", (string store, string key, JsonElement body, HttpContext http, CancellationToken ct)
            => operations.InvokeAsync("patch", store, key, body, http, ct));
        api.MapPut("/{store}/{key}", (string store, string key, JsonElement body, HttpContext http, CancellationToken ct)
            => operations.InvokeAsync("replace", store, key, body, http, ct));
        api.MapDelete("/{store}/{key}", (string store, string key, HttpContext http, CancellationToken ct)
            => operations.InvokeAsync("delete", store, key, null, http, ct));

        return app;
    }
}

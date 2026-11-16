using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Dp.Platform.Gateway;

/// <summary>
/// Прозрачный HTTP-прокси по префиксам маршрутов (KISS: без YARP —
/// прямая пересылка потока; K8s Ingress заменит при росте).
/// </summary>
public static class ProxyEndpoints
{
    public static WebApplication MapGatewayProxy(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<GatewayOptions>>().Value;
        var http = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("dp-gateway");
        http.Timeout = TimeSpan.FromSeconds(30);

        // Явный catch-all: MapFallback() без шаблона использует :nonfile и не
        // пропускает пути с точкой (например store 'sales.orders') — ломает API.
        app.MapFallback("{*path}", async context =>
        {
            var target = MatchRoute(options.Routes, context.Request.Path.Value ?? "/");
            if (target is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(new Dictionary<string, object?>
                {
                    ["type"] = "urn:dp:errors:DP-1010",
                    ["title"] = "DP-1010",
                    ["detail"] = $"No gateway route for '{context.Request.Path}'.",
                    ["status"] = 404,
                    ["code"] = "DP-1010",
                });
                return;
            }

            var upstreamUri = target + context.Request.Path + context.Request.QueryString;
            using var upstream = new HttpRequestMessage(new HttpMethod(context.Request.Method), upstreamUri)
            {
                Content = new StreamContent(context.Request.Body),
            };
            // Content-Type живёт в Content.Headers — переносим как есть,
            // без парсинга (MediaTypeHeaderValue не понимает boundary от curl).
            if (!string.IsNullOrEmpty(context.Request.ContentType))
                upstream.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);

            foreach (var (name, values) in context.Request.Headers)
            {
                if (HopByHop.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                upstream.Headers.TryAddWithoutValidation(name, values.ToArray());
            }

            using var response = await http.SendAsync(upstream, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            {
                if (HopByHop.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                context.Response.Headers[name] = values.ToArray();
            }

            // 204/304 не допускают тела — копирование пустого контента Kestrel отвергнет.
            if (response.StatusCode is not (System.Net.HttpStatusCode.NoContent or System.Net.HttpStatusCode.NotModified))
                await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
        return app;
    }

    /// <summary>Самый длинный префикс побеждает (OCP: маршруты = данные конфигурации).</summary>
    private static string? MatchRoute(Dictionary<string, string> routes, string path)
        => routes
            .Where(kv => path.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase)
                         && (path.Length == kv.Key.Length || path[kv.Key.Length] == '/'))
            .OrderByDescending(kv => kv.Key.Length)
            .Select(kv => kv.Value.TrimEnd('/'))
            .FirstOrDefault();

    private static readonly string[] HopByHop =
    [
        "Connection", "Keep-Alive", "Transfer-Encoding", "TE", "Trailer",
        "Upgrade", "Proxy-Authorization", "Proxy-Authenticate", "Host", "Authorization",
    ];
}

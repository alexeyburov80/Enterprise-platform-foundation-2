using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;

namespace Dp.Platform.Gateway;

/// <summary>
/// Диагностика платформы: /info — карта сервисов, /health/services —
/// агрегированный health всех контейнеров (единственная точка контроля).
/// </summary>
public static class GatewayDiagnostics
{
    public static WebApplication MapGatewayDiagnostics(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<GatewayOptions>>().Value;
        var http = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("dp-diagnostics");
        http.Timeout = TimeSpan.FromSeconds(3);

        app.MapGet("/info", () => Results.Ok(new
        {
            platform = "Dp.Platform MVP",
            services = options.Services.OrderBy(kv => kv.Key)
                .Select(kv => new { name = kv.Key, url = kv.Value }),
            routes = options.Routes.OrderBy(kv => kv.Key),
            docs = "см. README.md и docs/ARCHITECTURE.md",
        }));

        app.MapGet("/health/services", async () =>
        {
            var checks = await Task.WhenAll(options.Services.Select(async kv =>
            {
                try
                {
                    using var response = await http.GetAsync($"{kv.Value.TrimEnd('/')}/health");
                    var body = await response.Content.ReadFromJsonAsync<JsonElementSafeBridge>();
                    return new
                    {
                        service = kv.Key,
                        alive = response.IsSuccessStatusCode,
                        status = body?.Status ?? (response.IsSuccessStatusCode ? "Healthy" : "Unhealthy"),
                    };
                }
                catch (Exception)
                {
                    return new { service = kv.Key, alive = false, status = "Unreachable" };
                }
            }));

            var allAlive = checks.All(c => c.alive);
            return Results.Json(new { status = allAlive ? "Healthy" : "Degraded", services = checks },
                statusCode: allAlive ? 200 : 207);
        });

        return app;
    }
}

/// <summary>Мини-парсер ответа /health сервиса (status/service/checks).</summary>
public sealed record JsonElementSafeBridge(string Status);

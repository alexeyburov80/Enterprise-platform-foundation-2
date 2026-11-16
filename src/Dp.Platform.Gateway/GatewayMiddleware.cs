using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Dp.Platform.Gateway;

/// <summary>
/// Единая точка входа платформы: bearer-проверка (MVP) + прозрачный прокси.
/// SRP: шлюз ничего не знает о бизнес-логике сервисов.
/// </summary>
public static class GatewayMiddleware
{
    /// <summary>Проверка токена. /health и /info открыты (для compose healthcheck и диагностики).</summary>
    public static WebApplication UseGatewayAuth(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value;

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            var isPublic = path.StartsWithSegments("/health")
                           || path.StartsWithSegments("/info")
                           || path.StartsWithSegments("/health/services");
            if (isPublic) { await next(context); return; }

            var provided = context.Request.Headers.Authorization.ToString();
            if (provided == $"Bearer {options.ApiToken}") { await next(context); return; }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new Dictionary<string, object?>
            {
                ["type"] = "urn:dp:errors:DP-1000",
                ["title"] = "DP-1000",
                ["detail"] = "Missing or invalid bearer token (Authorization: Bearer <token>).",
                ["status"] = 401,
                ["code"] = "DP-1000",
            });
        });
        return app;
    }
}

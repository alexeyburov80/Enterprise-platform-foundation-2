using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dp.Platform.Framework.Stubs;

/// <summary>
/// Тумблер отказа имитатора: STUB_FAIL_MODE=down или GET /__fail?mode=down —
/// имитатор отвечает 503. Служит для опробования замещения в auto-режиме.
/// </summary>
public sealed class StubFailMode
{
    private volatile string _mode = "ok";

    public bool IsDown => _mode == "down";

    public void Set(string mode, int? forSeconds)
    {
        _mode = mode == "down" ? "down" : "ok";
        if (_mode == "down" && forSeconds is > 0)
        {
            _ = ResetLaterAsync(forSeconds.Value);
        }
    }

    private async Task ResetLaterAsync(int seconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        _mode = "ok";
    }
}

public static class StubFailModeExtensions
{
    /// <summary>Регистрируется в Program.cs до остальных эндпоинтов. Экземпляр StubFailMode должен быть в DI.</summary>
    public static WebApplication MapStubFailMode(this WebApplication app)
    {
        var failMode = app.Services.GetRequiredService<StubFailMode>();

        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/__fail"))
            {
                if (context.Request.Query.TryGetValue("mode", out var mode))
                {
                    int? forSeconds = int.TryParse(context.Request.Query["for"], out var seconds) ? seconds : null;
                    failMode.Set(mode.ToString(), forSeconds);
                }
                await context.Response.WriteAsJsonAsync(new { failMode = failMode.IsDown ? "down" : "ok" });
                return;
            }

            if (failMode.IsDown)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new Dictionary<string, object?>
                {
                    ["type"] = "urn:dp:errors:DP-1050",
                    ["title"] = DpCodes.UpstreamUnavailable,
                    ["detail"] = "Stub is in simulated failure mode (toggle /__fail?mode=ok).",
                    ["status"] = 503,
                    ["code"] = DpCodes.UpstreamUnavailable,
                });
                return;
            }

            await next(context);
        });
        return app;
    }
}

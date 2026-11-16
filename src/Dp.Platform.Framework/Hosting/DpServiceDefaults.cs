using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dp.Platform.Framework;

/// <summary>
/// Универсальная сборка любого сервиса платформы (DRY):
/// необработанные исключения -> RFC 9457, /health, общая диагностика.
/// Program.cs каждого сервиса остаётся на несколько строк.
/// </summary>
public static class DpServiceDefaults
{
    public static TBuilder AddDpService<TBuilder>(this TBuilder builder, string serviceName)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks();
        builder.Services.AddSingleton(new DpServiceInfo(serviceName));
        return builder;
    }

    public static WebApplication UseDpService(this WebApplication app)
    {
        app.UseExceptionHandler(exceptionHandlerApp => exceptionHandlerApp.Run(async context =>
        {
            var error = context.Features.Get<IExceptionHandlerPathFeature>()?.Error
                        ?? new Exception("unknown");
            var log = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("dp.errors");
            log.LogError(error, "Unhandled error on {Path}", context.Request.Path);

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new Dictionary<string, object?>
            {
                ["type"] = "urn:dp:errors:DP-1099",
                ["title"] = DpCodes.Internal,
                ["detail"] = "Internal error. Contact platform team with trace id.",
                ["status"] = 500,
                ["code"] = DpCodes.Internal,
            });
        }));

        app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = WriteHealthJson });
        return app;
    }

    private static async Task WriteHealthJson(HttpContext context, HealthReport report)
    {
        var info = context.RequestServices.GetRequiredService<DpServiceInfo>();
        await context.Response.WriteAsJsonAsync(new Dictionary<string, object?>
        {
            ["status"] = report.Status.ToString(),
            ["service"] = info.Name,
            ["checks"] = report.Entries.ToDictionary(kv => kv.Key,
                kv => new { status = kv.Value.Status.ToString(), description = kv.Value.Description }),
        });
    }
}

public sealed record DpServiceInfo(string Name);

/// <summary>Health-check доступности PostgreSQL (универсальный, настраивается строкой подключения).</summary>
public sealed class NpgsqlPingCheck(string connectionString) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy("PostgreSQL reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL unreachable", ex);
        }
    }
}

public static class DpHealthChecks
{
    /// <summary>Добавляет проверку БД по имени и строке подключения (единственная DRY-точка настройки).</summary>
    public static IHealthChecksBuilder AddNpgsqlCheck(
        this IHealthChecksBuilder builder, string name, string connectionString)
    {
        builder.Services.AddSingleton(new NpgsqlPingCheck(connectionString));
        return builder.AddCheck<NpgsqlPingCheck>(
            name,
            failureStatus: HealthStatus.Unhealthy,
            tags: ["ready"]);
    }
}

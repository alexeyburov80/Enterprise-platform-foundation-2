using Dp.Platform.Contracts.Adapters;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Collections.Immutable;
using System.Net.Http.Json;
using System.Text.Json;

namespace Dp.Platform.Framework;

/// <summary>
/// Универсальный хост интеграционного адаптера (OCP в чистом виде):
/// эндпоинты, health, выбор upstream и замещение настроены один раз,
/// системы различаются только профилем-переводчиком.
/// </summary>
public static class AdapterHost
{
    public static WebApplicationBuilder AddDpAdapter<TProfile>(this WebApplicationBuilder builder)
        where TProfile : class, IAdapterProfile
    {
        builder.AddDpService("dp-adapter");
        builder.Services.AddOptions<UpstreamOptions>()
            .BindConfiguration(UpstreamOptions.SectionName)
            .ValidateOnStart();

        builder.Services.AddSingleton<UpstreamSwitcher>();
        builder.Services.AddSingleton<IUpstreamState>(sp => sp.GetRequiredService<UpstreamSwitcher>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<UpstreamSwitcher>());
        builder.Services.AddTransient<UpstreamSwitchHandler>();

        builder.Services.AddHttpClient("dp-upstream").AddHttpMessageHandler<UpstreamSwitchHandler>();
        builder.Services.AddHttpClient("dp-upstream-probe");
        builder.Services.AddSingleton<IAdapterProfile, TProfile>();
        return builder;
    }

    public static WebApplication MapAdapterEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/int/{system}/route", (string system, IAdapterProfile profile, IUpstreamState state)
            => MatchSystem(system, profile)
                ? Results.Ok(new UpstreamRouteInfo(
                    profile.SystemName, state.Options.Mode, state.ActiveName, state.PrimaryHealthy))
                : ProblemSystemNotFound(system));

        app.MapGet("/api/v1/int/{system}/health", async (string system, IAdapterProfile profile,
            IUpstreamState state, IHttpClientFactory http, CancellationToken ct) =>
        {
            if (!MatchSystem(system, profile)) return ProblemSystemNotFound(system);
            var client = http.CreateClient("dp-upstream");
            var url = state.Active.AbsoluteUri.TrimEnd('/') + state.Options.ProbePath;
            try
            {
                using var response = await client.GetAsync(url, ct);
                return Results.Ok(new { upstream = state.ActiveName, alive = response.IsSuccessStatusCode });
            }
            catch (HttpRequestException ex)
            {
                return Results.Problem(title: DpCodes.UpstreamUnavailable, statusCode: 502,
                    detail: ex.Message, type: $"urn:dp:errors:{DpCodes.UpstreamUnavailable}",
                    extensions: new Dictionary<string, object?> { ["code"] = DpCodes.UpstreamUnavailable });
            }
        });

        app.MapGet("/api/v1/int/{system}/documents", async (string system, string? type, string? since,
            IAdapterProfile profile, IUpstreamState state, IHttpClientFactory http, CancellationToken ct) =>
        {
            if (!MatchSystem(system, profile)) return ProblemSystemNotFound(system);
            if (!ResolveType(profile, type, out var documentType))
                return ResultHttp.ToProblem(Error(DpCodes.Validation, $"Unknown document type '{type}'. Allowed: {string.Join(", ", profile.DocumentTypes)}."));
            try
            {
                var client = http.CreateClient("dp-upstream");
                var url = state.Options.Primary.AbsoluteUri.TrimEnd('/') + profile.Translator.ListPath(documentType, since);
                using var response = await client.GetAsync(url, ct);
                var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                if (!response.IsSuccessStatusCode)
                    return ResultHttp.ToProblem(Error(DpCodes.UpstreamBadResponse, $"Upstream returned {response.StatusCode}."));
                return Results.Ok(new { system, upstream = state.ActiveName, items = profile.Translator.ToPlatform(json, documentType) });
            }
            catch (HttpRequestException ex)
            {
                return ResultHttp.ToProblem(Error(DpCodes.UpstreamUnavailable, ex.Message));
            }
            catch (JsonException ex)
            {
                return ResultHttp.ToProblem(Error(DpCodes.UpstreamBadResponse, $"Upstream returned invalid JSON: {ex.Message}"));
            }
        });

        app.MapPost("/api/v1/int/{system}/documents", async (string system, IntegrationDocument document,
            IAdapterProfile profile, IUpstreamState state, IHttpClientFactory http, CancellationToken ct) =>
        {
            if (!MatchSystem(system, profile)) return ProblemSystemNotFound(system);
            if (!ResolveType(profile, document.Type, out var documentType))
                return ResultHttp.ToProblem(Error(DpCodes.Validation, $"Unknown document type '{document.Type}'. Allowed: {string.Join(", ", profile.DocumentTypes)}."));
            try
            {
                var client = http.CreateClient("dp-upstream");
                var url = state.Options.Primary.AbsoluteUri.TrimEnd('/') + profile.Translator.CreatePath(documentType);
                var payload = profile.Translator.ToUpstream(document with { Type = documentType });
                using var response = await client.PostAsJsonAsync(url, payload, ct);
                var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                if (!response.IsSuccessStatusCode)
                    return ResultHttp.ToProblem(Error(DpCodes.UpstreamBadResponse, $"Upstream returned {response.StatusCode}."));
                return Results.Json(new { system, upstream = state.ActiveName, upstreamResponse = json }, statusCode: 201);
            }
            catch (HttpRequestException ex)
            {
                return ResultHttp.ToProblem(Error(DpCodes.UpstreamUnavailable, ex.Message));
            }
        });

        return app;
    }

    private static bool MatchSystem(string requested, IAdapterProfile profile)
        => requested.Equals(profile.SystemName, StringComparison.OrdinalIgnoreCase);

    private static bool ResolveType(IAdapterProfile profile, string? requested, out string type)
    {
        type = profile.DocumentTypes.FirstOrDefault(t => t.Equals(requested, StringComparison.OrdinalIgnoreCase))
               ?? profile.DocumentTypes[0];
        return requested is null || profile.DocumentTypes.Contains(type, StringComparer.OrdinalIgnoreCase);
    }

    private static IResult ProblemSystemNotFound(string system)
        => ResultHttp.ToProblem(Error(DpCodes.StoreNotFound, $"Adapter for system '{system}' is not deployed here."));

    private static Error Error(string code, string message) => new(code, message);
}

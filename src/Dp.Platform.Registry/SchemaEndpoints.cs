using Dp.Platform.Contracts.Schema;
using Dp.Platform.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Dp.Platform.Registry;

/// <summary>HTTP-контракт реестра (SRP: только маппинг запросов на SchemaService).</summary>
public static class SchemaEndpoints
{
    public static WebApplication MapSchemaEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1/stores");

        api.MapPost("/", RegisterStore);
        api.MapGet("/", ListStores);
        api.MapGet("/{store}/versions/{version}", GetVersion);
        api.MapGet("/{store}/versions/current", GetLatest);
        api.MapGet("/{store}/versions", ListStoreVersions);

        return app;
    }

    private static async Task<IResult> RegisterStore(StoreDefinition definition, SchemaService service, CancellationToken ct)
        => (await service.RegisterAsync(definition, ct)).ToHttp(created =>
            Results.Json(created, statusCode: StatusCodes.Status201Created));

    private static async Task<IResult> ListStores(SchemaService service, CancellationToken ct)
        => Results.Ok(await service.ListAsync(null, ct));

    private static async Task<IResult> GetVersion(string store, string version, SchemaService service, CancellationToken ct)
        => (await service.GetAsync(store, version, ct)).ToHttp(Results.Ok);

    private static async Task<IResult> GetLatest(string store, SchemaService service, CancellationToken ct)
        => (await service.GetAsync(store, null, ct)).ToHttp(Results.Ok);

    private static async Task<IResult> ListStoreVersions(string store, SchemaService service, CancellationToken ct)
        => Results.Ok(await service.ListAsync(store, ct));
}

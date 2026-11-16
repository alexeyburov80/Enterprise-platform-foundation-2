using Dp.Platform.Data.Operations;
using Dp.Platform.Framework;
using Microsoft.AspNetCore.Http;
using Npgsql;
using System.Text.Json;

namespace Dp.Platform.Data;

/// <summary>
/// Резолвер операций (Mediator в один экран): схема -> провизионинг -> операция.
/// Единая точка обработки DpFaultException и unique-violation (DRY).
/// </summary>
public sealed class DataOperations(
    IServiceProvider services,
    IStoreSchemaProvider schemas,
    TableProvisioner provisioner)
{
    public async Task<IResult> InvokeAsync(
        string operation, string store, string? key, JsonElement? body, HttpContext http, CancellationToken ct)
    {
        var schemaResult = await schemas.GetAsync(store, ct);
        if (!schemaResult.Ok) return schemaResult.Failure!.ToProblem();

        var schema = await provisioner.EnsureAsync(schemaResult.Value!, ct);
        try
        {
            var handler = services.GetRequiredKeyedService<IDataOperation>(operation);
            return await handler.ExecuteAsync(
                new OperationContext(schema, key, body ?? default, http), ct);
        }
        catch (DpFaultException fault)
        {
            return fault.Error.ToProblem();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return new Error(DpCodes.DuplicateKey,
                "Record with the same key already exists.",
                new { store, key }).ToProblem();
        }
    }
}

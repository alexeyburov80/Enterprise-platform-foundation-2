using Dp.Platform.Contracts.Data;
using Dp.Platform.Framework;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace Dp.Platform.Data.Operations;

/// <summary>POST /api/v1/data/{store} — создание записи (ключ обязателен, дубликат -> 409).</summary>
public sealed class InsertOperation(IRecordRepository repository) : IDataOperation
{
    public string Name => "insert";

    public async Task<IResult> ExecuteAsync(OperationContext context, CancellationToken ct = default)
    {
        var record = RecordGuard.Parse(context.Body);
        RecordGuard.ValidateNames(context.Schema, record);
        RecordGuard.ValidateKeyPresent(context.Schema, record);

        var row = await repository.InsertAsync(context.Schema, record, ct);
        return Results.Json(row, statusCode: StatusCodes.Status201Created);
    }
}

/// <summary>POST /api/v1/data/{store}/bulk — пакетная вставка (тело: массив объектов).</summary>
public sealed class BulkInsertOperation(IRecordRepository repository) : IDataOperation
{
    public string Name => "bulk-insert";

    public async Task<IResult> ExecuteAsync(OperationContext context, CancellationToken ct = default)
    {
        if (context.Body.ValueKind is not JsonValueKind.Array)
            throw new DpFaultException(DpCodes.Validation, "Bulk body must be a JSON array of records.");

        var inserted = 0;
        foreach (var item in context.Body.EnumerateArray())
        {
            var record = RecordGuard.Parse(item);
            RecordGuard.ValidateNames(context.Schema, record);
            RecordGuard.ValidateKeyPresent(context.Schema, record);
            await repository.InsertAsync(context.Schema, record, ct);
            inserted++;
        }
        return Results.Ok(new { inserted });
    }
}

/// <summary>GET /api/v1/data/{store}/{key} — чтение записи по ключу.</summary>
public sealed class GetOperation(IRecordRepository repository) : IDataOperation
{
    public string Name => "get";

    public async Task<IResult> ExecuteAsync(OperationContext context, CancellationToken ct = default)
    {
        var key = OperationGuard.RequireKey(context);
        var row = await repository.GetAsync(context.Schema, key, ct);
        return row is null
            ? new Error(DpCodes.RecordNotFound, $"Record '{key}' not found in '{context.Schema.Store}'.", new { key }).ToProblem()
            : Results.Ok(row);
    }
}

/// <summary>POST /api/v1/data/{store}/query — выборка (Query DSL): filter/sort/limit/offset.</summary>
public sealed class QueryOperation(IRecordRepository repository) : IDataOperation
{
    public string Name => "query";

    public async Task<IResult> ExecuteAsync(OperationContext context, CancellationToken ct = default)
    {
        var query = context.Body.ValueKind is JsonValueKind.Object
            ? context.Body.Deserialize<QueryDto>()
            : new QueryDto();
        var page = await repository.QueryAsync(context.Schema, query ?? new QueryDto(), ct);
        return Results.Ok(page);
    }
}

/// <summary>PATCH /api/v1/data/{store}/{key} — частичное обновление перечисленных полей.</summary>
public sealed class PatchOperation(IRecordRepository repository) : IDataOperation
{
    public string Name => "patch";

    public async Task<IResult> ExecuteAsync(OperationContext context, CancellationToken ct = default)
    {
        var key = OperationGuard.RequireKey(context);
        var fields = RecordGuard.Parse(context.Body);
        RecordGuard.ValidateNames(context.Schema, fields);
        RecordGuard.ValidateKeyMatch(context.Schema, fields, key);

        var row = await repository.UpdateAsync(context.Schema, key, fields, replace: false, upsert: false, ct);
        return row is null
            ? new Error(DpCodes.RecordNotFound, $"Record '{key}' not found in '{context.Schema.Store}'.", new { key }).ToProblem()
            : Results.Ok(row);
    }
}

/// <summary>PUT /api/v1/data/{store}/{key} — замещение целиком (upsert: создаёт при отсутствии).</summary>
public sealed class ReplaceOperation(IRecordRepository repository) : IDataOperation
{
    public string Name => "replace";

    public async Task<IResult> ExecuteAsync(OperationContext context, CancellationToken ct = default)
    {
        var key = OperationGuard.RequireKey(context);
        var record = RecordGuard.Parse(context.Body);
        RecordGuard.ValidateNames(context.Schema, record);
        RecordGuard.ValidateKeyPresent(context.Schema, record);
        RecordGuard.ValidateKeyMatch(context.Schema, record, key);
        if (!record.ContainsKey(context.Schema.KeyField))
            record[context.Schema.KeyField] = JsonSerializer.SerializeToElement(key);

        var row = await repository.UpdateAsync(context.Schema, key, record, replace: true, upsert: true, ct);
        return Results.Ok(row);
    }
}

/// <summary>DELETE /api/v1/data/{store}/{key} — удаление записи.</summary>
public sealed class DeleteOperation(IRecordRepository repository) : IDataOperation
{
    public string Name => "delete";

    public async Task<IResult> ExecuteAsync(OperationContext context, CancellationToken ct = default)
    {
        var key = OperationGuard.RequireKey(context);
        return await repository.DeleteAsync(context.Schema, key, ct)
            ? Results.NoContent()
            : new Error(DpCodes.RecordNotFound, $"Record '{key}' not found in '{context.Schema.Store}'.", new { key }).ToProblem();
    }
}

/// <summary>GET /api/v1/data/{store}/count?filter={json} — количество записей по условию.</summary>
public sealed class CountOperation(IRecordRepository repository) : IDataOperation
{
    public string Name => "count";

    public async Task<IResult> ExecuteAsync(OperationContext context, CancellationToken ct = default)
    {
        var filterJson = context.Http?.Request.Query["filter"].ToString();
        var filter = string.IsNullOrEmpty(filterJson)
            ? null
            : JsonSerializer.Deserialize<FilterNode>(filterJson);
        var count = await repository.CountAsync(context.Schema, filter, ct);
        return Results.Ok(new { count });
    }
}

/// <summary>PATCH /api/v1/data/{store} — массовое обновление по условию (body: {filter, fields}).</summary>
public sealed class PatchAllOperation(IRecordRepository repository) : IDataOperation
{
    public string Name => "patch-all";

    public async Task<IResult> ExecuteAsync(OperationContext context, CancellationToken ct = default)
    {
        var body = context.Body.ValueKind is JsonValueKind.Object
            ? context.Body
            : throw new DpFaultException(DpCodes.Validation, "Body must be { filter, fields }.");

        var filter = body.TryGetProperty("filter", out var filterElement) && filterElement.ValueKind is JsonValueKind.Object
            ? filterElement.Deserialize<FilterNode>()
            : null;
        var fields = body.TryGetProperty("fields", out var fieldsElement)
            ? RecordGuard.Parse(fieldsElement)
            : throw new DpFaultException(DpCodes.Validation, "'fields' object is required.");

        RecordGuard.ValidateNames(context.Schema, fields);
        var updated = await repository.PatchWhereAsync(context.Schema, filter, fields, ct);
        return Results.Ok(new { updated });
    }
}

public static class OperationGuard
{
    public static string RequireKey(OperationContext context)
        => string.IsNullOrEmpty(context.Key)
            ? throw new DpFaultException(DpCodes.Validation, "Record key is required in the route.")
            : context.Key;
}

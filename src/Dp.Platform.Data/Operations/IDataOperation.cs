using Dp.Platform.Framework;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace Dp.Platform.Data;

/// <summary>
/// Контекст одной операции над logical store (иммутабельный, тестируемый).
/// </summary>
public sealed record OperationContext(StoreSchema Schema, string? Key, JsonElement Body, HttpContext? Http);

/// <summary>
/// Стратегия операции Data Access API (OCP: новая операция = новый класс,
/// регистрация одним вызовом AddKeyedSingleton, эндпоинты не меняются).
/// </summary>
public interface IDataOperation
{
    /// <summary>Имя операции для реестра DI: insert, get, query, patch, replace, delete, count, bulk-insert, patch-all.</summary>
    string Name { get; }

    Task<IResult> ExecuteAsync(OperationContext context, CancellationToken ct = default);
}

using System.Text.Json;

namespace Dp.Platform.Framework;

/// <summary>
/// Конверт события платформы (Этап A, раздел «События»). Именование полей — snake_case
/// (контракт dp.*.v1). Тип события задаёт версионность контракта.
/// </summary>
public sealed record EventEnvelope
{
    public required string EventId { get; init; }
    public required string EventType { get; init; }
    public required string Producer { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required object Payload { get; init; }

    public static EventEnvelope Create(string eventType, string producer, object payload)
        => new()
        {
            EventId = Guid.NewGuid().ToString(),
            EventType = eventType,
            Producer = producer,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = payload,
        };
}

/// <summary>Единая JSON-политика для событий: snake_case, веб-настройки (DRY).</summary>
public static class EventJson
{
    public static readonly JsonSerializerOptions SnakeCase =
        new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static byte[] Serialize(object value) => JsonSerializer.SerializeToUtf8Bytes(value, SnakeCase);
}

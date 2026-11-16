namespace Dp.Platform.Contracts.Events;

/// <summary>Операция изменения строки logical store.</summary>
public enum ChangeOperation
{
    Inserted,
    Updated,
    Deleted,
}

/// <summary>
/// Каноническое событие изменения данных dp.data.changed.v1 (Этап A).
/// Публикуется Cdc-сервисом в RabbitMQ: exchange dp.events, routing key dp.data.changed.v1.
/// </summary>
public sealed record DataChangedEventV1
{
    public const string EventType = "dp.data.changed.v1";

    public required string Store { get; init; }
    public required string Version { get; init; }
    public required string Op { get; init; }
    public required string Key { get; init; }
    public Dictionary<string, object?>? Payload { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
}

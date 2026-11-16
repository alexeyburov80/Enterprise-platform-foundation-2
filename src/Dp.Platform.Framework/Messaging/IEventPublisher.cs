namespace Dp.Platform.Framework;

/// <summary>
/// Универсальная публикация событий (DIP: сервисы зависят от интерфейса, не от RabbitMQ).
/// </summary>
public interface IEventPublisher
{
    /// <param name="routingKey">Например dp.data.changed.v1.</param>
    /// <param name="body">Сериализованный конверт (EventJson.Serialize).</param>
    Task PublishAsync(string routingKey, byte[] body, string messageId, CancellationToken ct = default);
}

/// <summary>Настройки подключения (binding из секции Rabbit).</summary>
public sealed class RabbitOptions
{
    public const string SectionName = "Rabbit";
    public string ConnectionString { get; set; } = "amqp://dp:dp@localhost:5672/%2F";
    public string Exchange { get; set; } = "dp.events";
}

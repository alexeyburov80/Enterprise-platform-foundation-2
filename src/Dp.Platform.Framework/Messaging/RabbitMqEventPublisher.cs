using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Dp.Platform.Framework;

/// <summary>
/// Публикация событий в RabbitMQ (topic-обменник, persistent-сообщения).
/// Соединение ленивое, с автоповтором; семантика at-least-once — источник
/// события всегда outbox, поэтому задержка публикации не теряет данные.
/// </summary>
public sealed class RabbitMqEventPublisher : IEventPublisher, IDisposable
{
    private readonly RabbitOptions _options;
    private readonly ILogger<RabbitMqEventPublisher> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IModel? _channel;

    public RabbitMqEventPublisher(IOptions<RabbitOptions> options, ILogger<RabbitMqEventPublisher> log)
    {
        _options = options.Value;
        _log = log;
    }

    public Task PublishAsync(string routingKey, byte[] body, string messageId, CancellationToken ct = default)
    {
        var channel = GetChannel();
        var properties = channel.CreateBasicProperties();
        properties.ContentType = "application/json";
        properties.DeliveryMode = 2; // persistent
        properties.MessageId = messageId;
        properties.Type = routingKey;

        channel.BasicPublish(_options.Exchange, routingKey, mandatory: false, properties, body);
        _log.LogDebug("Published {Type} ({Bytes} bytes)", routingKey, body.Length);
        return Task.CompletedTask;
    }

    private IModel GetChannel()
    {
        _gate.Wait();
        try
        {
            if (_channel is { IsOpen: true }) return _channel;

            _channel?.Dispose();
            _connection?.Dispose();

            var factory = new ConnectionFactory
            {
                Uri = new Uri(_options.ConnectionString),
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
            };
            _connection = factory.CreateConnection();
            _channel = _connection.CreateModel();
            _channel.ExchangeDeclare(_options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false);
            _log.LogInformation("RabbitMQ connected, exchange {Exchange}", _options.Exchange);
            return _channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        _gate.Dispose();
    }
}

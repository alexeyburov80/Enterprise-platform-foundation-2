// ============================================================================
// Outbox + Publisher (решение В6): события сохраняются в dp.outbox той же
// транзакцией, что и бизнес-данные; фоновый publisher доставляет их в
// RabbitMQ (topic-обменник dp.data, routing key dp.data.changed.<module>.<store>.v1).
// Недоступность брокера НЕ теряет данные — события остаются в outbox (обяз.).
// Envelope — dp.data.changed.v1 по листингу 5 (единый envelope Outbox/CDC).
// ============================================================================

using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using RabbitMQ.Client;

namespace Dp.Core;

public sealed class OutboxWriter
{
    private readonly NpgsqlDataSource _db;
    private readonly ILogger<OutboxWriter> _logger;

    public OutboxWriter(NpgsqlDataSource db, ILogger<OutboxWriter> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task AppendAsync(NpgsqlConnection conn, NpgsqlTransaction tx, ResolvedEntity re,
        string op, Guid keyId, JsonObject? data, JsonObject? previous, string actor)
    {
        var txId = Guid.NewGuid().ToString("N");
        await using var cmd = new NpgsqlCommand("""
                INSERT INTO dp.outbox (id, occurred_at, store_id, store_version, entity, op, key, data, previous, tx_id, source)
                VALUES (@id, now(), @store, @version, @entity, @op, @key, @data, @prev, @tx, 'provider.outbox')
                """, conn, tx);
        cmd.Parameters.AddWithValue("@id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("@store", re.Store.StoreId);
        cmd.Parameters.AddWithValue("@version", re.Store.Metadata.Version);
        cmd.Parameters.AddWithValue("@entity", re.Entity.Name);
        cmd.Parameters.AddWithValue("@op", op);
        cmd.Parameters.AddWithValue("@key", NpgsqlDbType.Jsonb,
            new JsonObject { ["id"] = keyId.ToString() }.ToJsonString());
        cmd.Parameters.AddWithValue("@data", NpgsqlDbType.Jsonb,
            data?.ToJsonString() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@prev", NpgsqlDbType.Jsonb,
            previous?.ToJsonString() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@tx", txId);
        await cmd.ExecuteNonQueryAsync();
        _logger.LogDebug("Outbox: {Op} {Store}/{Entity} id={Id}", op, re.Store.StoreId, re.Entity.Name, keyId);
    }
}

public sealed class OutboxPublisher : BackgroundService
{
    private readonly NpgsqlDataSource _db;
    private readonly IConfiguration _cfg;
    private readonly ILogger<OutboxPublisher> _logger;

    private IConnection? _conn;
    private IModel? _channel;
    private bool _wasConnected;

    public bool IsConnected { get; private set; }

    public OutboxPublisher(NpgsqlDataSource db, IConfiguration cfg, ILogger<OutboxPublisher> logger)
    {
        _db = db;
        _cfg = cfg;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Outbox publisher запущен (exchange '{Exchange}')", Exchange);
        await WaitForSchemaAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PublishPendingAsync(ct);
                SetConnected(true);
            }
            catch (Exception ex)
            {
                SetConnected(false);
                _logger.LogWarning(ex, "Outbox publisher: брокер недоступен — события остаются в outbox, повтор через 2 с");
            }
            try { await Task.Delay(2000, ct); }
            catch (TaskCanceledException) { break; }
        }
    }

    /// <summary>
    /// Схему создаёт сервис dp-api (мигратор, dp.migration_journal); publisher
    /// живёт в dp-sync, поэтому перед первым опросом спокойно ждёт таблицу
    /// dp.outbox, не заваливая лог ошибками.
    /// </summary>
    private async Task WaitForSchemaAsync(CancellationToken ct)
    {
        var logged = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = await _db.OpenConnectionAsync(ct);
                await using var cmd = new NpgsqlCommand("SELECT to_regclass('dp.outbox') IS NOT NULL", conn);
                if ((bool)(await cmd.ExecuteScalarAsync(ct))!) return;
                if (!logged)
                {
                    _logger.LogInformation("Outbox publisher: жду миграций dp-api (таблица dp.outbox ещё не создана)");
                    logged = true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (!logged)
                {
                    _logger.LogInformation("Outbox publisher: БД недоступна ({Message}) — повтор через 2 с", ex.Message);
                    logged = true;
                }
            }
            try { await Task.Delay(2000, ct); }
            catch (TaskCanceledException) { return; }
        }
    }

    private string Exchange => _cfg["Rabbit:Exchange"] ?? "dp.data";

    private async Task PublishPendingAsync(CancellationToken ct)
    {
        EnsureChannel();

        await using var conn = await _db.OpenConnectionAsync(ct);

        var rows = new List<(Guid Id, JsonObject Envelope)>();
        await using (var cmd = new NpgsqlCommand("""
                SELECT id, occurred_at, store_id, store_version, entity, op, key, data, previous, tx_id, source
                FROM dp.outbox
                WHERE published_at IS NULL
                ORDER BY occurred_at
                LIMIT 100
                """, conn))
        {
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var envelope = new JsonObject
                {
                    ["eventId"] = r.GetGuid(0).ToString(),
                    ["occurredAt"] = Rows.Iso(Rows.AsDto(r.GetValue(1))),
                    ["lsn"] = null,
                    ["tenantId"] = "00000000-0000-0000-0000-000000000000",
                    ["storeId"] = r.GetString(2),
                    ["storeVersion"] = r.GetString(3),
                    ["entity"] = r.GetString(4),
                    ["op"] = r.GetString(5),
                    ["key"] = JsonNode.Parse(r.GetString(6)),
                    ["data"] = r.IsDBNull(7) ? null : JsonNode.Parse(r.GetString(7)),
                    ["previous"] = r.IsDBNull(8) ? null : JsonNode.Parse(r.GetString(8)),
                    ["txId"] = r.GetString(9),
                    ["source"] = r.GetString(10)
                };
                rows.Add((r.GetGuid(0), envelope));
            }
        }

        if (rows.Count == 0) return;

        foreach (var (id, envelope) in rows)
        {
            var routingKey = $"dp.data.changed.{envelope["storeId"]}.v1";
            var body = Encoding.UTF8.GetBytes(envelope.ToJsonString());
            var props = _channel!.CreateBasicProperties();
            props.DeliveryMode = 2; // persistent
            props.ContentType = "application/json";
            props.MessageId = id.ToString();
            _channel.BasicPublish(Exchange, routingKey, mandatory: false, props, body);
        }

        var ids = rows.Select(x => x.Id).ToArray();
        await using (var cmd = new NpgsqlCommand(
            "UPDATE dp.outbox SET published_at = now() WHERE id = ANY(@ids)", conn))
        {
            cmd.Parameters.AddWithValue("@ids", ids);
            await cmd.ExecuteNonQueryAsync();
        }

        _logger.LogInformation("Outbox publisher: опубликовано {Count} событий (routing key dp.data.changed.<module>.<store>.v1)",
            rows.Count);
    }

    private void EnsureChannel()
    {
        if (_conn is { IsOpen: true } && _channel is { IsOpen: true }) return;

        _channel?.Dispose();
        _conn?.Dispose();

        var factory = new ConnectionFactory
        {
            HostName = _cfg["Rabbit:Host"] ?? "localhost",
            Port = _cfg.GetValue("Rabbit:Port", 5672),
            UserName = _cfg["Rabbit:User"] ?? "guest",
            Password = _cfg["Rabbit:Pass"] ?? "guest",
            AutomaticRecoveryEnabled = true
        };
        _conn = factory.CreateConnection("dp-outbox-publisher");
        _channel = _conn.CreateModel();
        _channel.ExchangeDeclare(Exchange, ExchangeType.Topic, durable: true);
    }

    private void SetConnected(bool value)
    {
        if (IsConnected != value)
            _logger.LogInformation("Outbox publisher: состояние брокера → {State}", value ? "подключён" : "отключён");
        IsConnected = value;
        _wasConnected = value;
    }
}

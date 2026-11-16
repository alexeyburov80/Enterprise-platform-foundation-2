using Dp.Platform.Contracts.Events;
using Dp.Platform.Framework;
using Microsoft.Extensions.Options;

namespace Dp.Platform.Cdc;

/// <summary>
/// Фоновый воркер CDC: outbox -> RabbitMQ (dp.data.changed.v1).
/// Замена CdcGateway на pgoutput (MVP-упрощение Этапа A): тот же контракт
/// событий, без декодера pgoutput; no dual-write — outbox в транзакции.
/// </summary>
public sealed class OutboxPoller(
    IOutboxProcessor processor,
    IEventPublisher publisher,
    VersionResolver versions,
    IOptions<CdcOptions> options,
    ILogger<OutboxPoller> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("CDC poller started, batch={Batch}", options.Value.BatchSize);
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                processed = await processor.ProcessPendingAsync(options.Value.BatchSize, PublishAsync, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Outbox processing failed, will retry");
            }

            if (processed == 0)
                await Task.Delay(options.Value.PollIntervalMs, stoppingToken);
        }
        log.LogInformation("CDC poller stopped");
    }

    private async Task PublishAsync(OutboxRow row, CancellationToken ct)
    {
        var change = ChangeEventFactory.Create(row, await versions.GetVersionAsync(row.Store, ct));
        var envelope = EventEnvelope.Create(DataChangedEventV1.EventType, "dp-cdc", change);
        await publisher.PublishAsync(
            DataChangedEventV1.EventType,
            EventJson.Serialize(envelope),
            envelope.EventId,
            ct);
    }
}

public sealed class CdcOptions
{
    public const string SectionName = "Cdc";
    public int BatchSize { get; set; } = 100;
    public int PollIntervalMs { get; set; } = 500;
    public string RegistryBaseUrl { get; set; } = "http://localhost:8081";
}

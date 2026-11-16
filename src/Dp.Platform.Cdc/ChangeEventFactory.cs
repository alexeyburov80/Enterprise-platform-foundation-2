using Dp.Platform.Contracts.Events;
using Dp.Platform.Framework;
using System.Text.Json;

namespace Dp.Platform.Cdc;

/// <summary>Сборщик события dp.data.changed.v1 из строки outbox (SRP: только маппинг).</summary>
public static class ChangeEventFactory
{
    public static DataChangedEventV1 Create(OutboxRow row, string storeVersion)
    {
        var payload = row.PayloadJson is null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(row.PayloadJson);

        return new DataChangedEventV1
        {
            Store = row.Store,
            Version = storeVersion,
            Op = row.Op.ToLowerInvariant(),
            Key = row.Key,
            Payload = payload,
            OccurredAt = DateTimeOffset.UtcNow,
        };
    }
}

using System.Text.Json.Nodes;

namespace Dp.Platform.Stub.OneC;

/// <summary>Модель заказа клиента имитатора 1С (поля как у Document_SalesOrder OData).</summary>
public static class SalesOrderSeed
{
    public static List<JsonObject> Create(int count)
    {
        var orders = new List<JsonObject>();
        for (var i = 1; i <= count; i++)
        {
            orders.Add(new JsonObject
            {
                ["Ref_Key"] = $"00000000-0000-0000-0000-{i:D12}",
                ["Number"] = $"SO-{i:D5}",
                ["Date"] = DateTime.UtcNow.AddDays(-i).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["Posted"] = i % 2 == 0,
                ["Counterparty"] = $"Контрагент {i}",
                ["Amount"] = 1000m * i,
                ["Currency"] = "RUB",
                ["Comment"] = $"Создано имитатором 1С ({count} записей)",
            });
        }
        return orders;
    }
}

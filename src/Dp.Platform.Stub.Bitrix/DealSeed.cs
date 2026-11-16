using System.Text.Json.Nodes;

namespace Dp.Platform.Stub.Bitrix;

/// <summary>Модель сделки имитатора Битрикс24 (поля как у crm.deal).</summary>
public static class DealSeed
{
    public static List<JsonObject> Create(int count)
    {
        var deals = new List<JsonObject>();
        for (var i = 1; i <= count; i++)
        {
            deals.Add(new JsonObject
            {
                ["ID"] = i.ToString(),
                ["TITLE"] = $"Сделка {i}",
                ["STAGE_ID"] = i % 3 == 0 ? "WON" : "NEW",
                ["OPPORTUNITY"] = 500m * i,
                ["CONTACT_ID"] = (100 + i).ToString(),
                ["DATE_CREATE"] = DateTime.UtcNow.AddDays(-i).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["UPDATED_TIME"] = DateTime.UtcNow.AddHours(-i).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["COMMENT"] = $"Создано имитатором Битрикс ({count} записей)",
            });
        }
        return deals;
    }
}

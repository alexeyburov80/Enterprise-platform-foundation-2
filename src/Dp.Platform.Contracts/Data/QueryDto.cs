using System.Text.Json.Serialization;

namespace Dp.Platform.Contracts.Data;

/// <summary>
/// Узел условия Query DSL (Этап A). Рекурсивная структура: либо атом
/// (field/op/value), либо логическая связка and/or/not. Универсальность:
/// новые операторы добавляются в один словарь транслятора.
/// </summary>
public sealed record FilterNode
{
    public string? Field { get; init; }
    public string? Op { get; init; }
    [JsonPropertyName("value")] public JsonElementSafe? AtomValue { get; init; }

    public IReadOnlyList<FilterNode>? And { get; init; }
    public IReadOnlyList<FilterNode>? Or { get; init; }
    public FilterNode? Not { get; init; }
}

/// <summary>Безопасная обёртка значения атома (число, строка, bool, null).</summary>
public sealed record JsonElementSafe
{
    public bool IsNull { get; init; }
    public string? StringValue { get; init; }
    public double? NumberValue { get; init; }
    public bool? BoolValue { get; init; }
    public IReadOnlyList<JsonElementSafe>? ListValue { get; init; }
}

public sealed record SortSpec(string Field, bool Descending = false);

/// <summary>Запрос выборки (Query DSL -> SQL транслятор Data-сервиса).</summary>
public sealed record QueryDto
{
    public FilterNode? Filter { get; init; }
    public IReadOnlyList<SortSpec>? Sort { get; init; }
    public int Limit { get; init; } = 50;
    public int Offset { get; init; }
    public IReadOnlyList<string>? Fields { get; init; }
}

/// <summary>Строка logical store: имя поля -> значение (или ns.field для расширений).</summary>
public sealed record RecordDto(Dictionary<string, object?> Items);

/// <summary>Страница результатов выборки.</summary>
public sealed record PageDto(IReadOnlyList<Dictionary<string, object?>> Items, long Total, int Limit, int Offset);

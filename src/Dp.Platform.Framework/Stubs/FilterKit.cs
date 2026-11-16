using System.Text.Json.Nodes;

namespace Dp.Platform.Framework.Stubs;

/// <summary>
/// Примитивный фильтр по полям сущностей (eq/ne/gt/ge/lt/le) для имитаторов.
/// Значения сравниваются как строки либо числа — достаточно для демо OData/REST.
/// </summary>
public static class FilterKit
{
    public static Func<JsonNode, bool> Compile(string field, string op, JsonNode? value)
    {
        var expected = value is null ? null : value.ToJsonString().Trim('"');
        var expectedNumber = double.TryParse(expected, out var number) ? number : (double?)null;

        return entity =>
        {
            var actual = entity?[field]?.ToJsonString().Trim('"');
            if (op == "isNull") return actual is null;
            if (actual is null) return false;

            var actualNumber = double.TryParse(actual, out var actualNum) ? actualNum : (double?)null;
            return op switch
            {
                "eq" => string.Equals(actual, expected, StringComparison.Ordinal),
                "ne" => !string.Equals(actual, expected, StringComparison.Ordinal),
                "gt" => Compare(actual, actualNumber, expected, expectedNumber) > 0,
                "ge" => Compare(actual, actualNumber, expected, expectedNumber) >= 0,
                "lt" => Compare(actual, actualNumber, expected, expectedNumber) < 0,
                "le" => Compare(actual, actualNumber, expected, expectedNumber) <= 0,
                _ => true, // неизвестный оператор не отсеивает — имитатор прощающий (tolerant reader)
            };
        };
    }

    private static int Compare(string? actual, double? actualNumber, string? expected, double? expectedNumber)
    {
        if (actualNumber is not null && expectedNumber is not null)
            return actualNumber.Value.CompareTo(expectedNumber.Value);
        return string.CompareOrdinal(actual, expected);
    }
}

// ============================================================================
// Валидация канонических типов и семантика missing / явный null / значение
// (таблицы 2 и 6 спецификации; решение A-4). Все ошибки — коды DP-1001…DP-1005.
// ============================================================================

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dp.Core;

/// <summary>
/// Разобранная полезная нагрузка: канонические поля (уже в типах C#) +
/// изменения ext (namespace.key → значение | null) + терпимое хранение «чужих» ключей.
/// </summary>
public sealed class ParsedPayload
{
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);
    /// <summary>Ключ вида "namespace.field" (или "__stray.field").</summary>
    public Dictionary<string, JsonNode?> ExtChanges { get; } = new(StringComparer.Ordinal);
}

public static class PayloadParser
{
    public static ParsedPayload Parse(SdEntity entity, JsonObject payload, SdRegistry sd, string storeId)
    {
        var result = new ParsedPayload();
        var fieldMap = entity.Fields.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, node) in payload)
        {
            if (string.Equals(key, "ext", StringComparison.Ordinal))
            {
                if (node is not JsonObject extObj)
                    throw new DpException(DpCodes.TypeMismatch, 400, "Поле 'ext' должно быть объектом namespace'ов");
                foreach (var (ns, nsNode) in extObj)
                {
                    if (nsNode is not JsonObject nsObj)
                        throw new DpException(DpCodes.TypeMismatch, 400, $"ext.{ns}: namespace должен быть объектом");
                    foreach (var (k, v) in nsObj)
                        result.ExtChanges[$"{ns}.{k}"] = v;
                }
                continue;
            }

            if (fieldMap.TryGetValue(key, out var field))
            {
                result.Values[field.Name] = node is null ? null : FieldValidator.ValidateValue(field, node, entity.Name);
                continue;
            }

            // Tolerant reader (обяз. спецификации): неизвестный ключ верхнего уровня
            // не отвергается, а сохраняется в неймспейс __stray.
            result.ExtChanges[$"__stray.{key}"] = node;
        }

        return result;
    }
}

public static class FieldValidator
{
    private static readonly Regex OffsetTail = new(@"(?:Z|[+-]\d{2}:?\d{2})$", RegexOptions.Compiled);

    public static object? ValidateValue(SdField f, JsonNode node, string entityName)
    {
        var where = $"{entityName}.{f.Name}";
        if (node is not JsonValue v)
            throw new DpException(DpCodes.TypeMismatch, 400, $"{where}: ожидался скалярный JSON-тип по каноническому типу '{f.Type}'");

        switch (f.Canon)
        {
            case CanonType.Uuid or CanonType.Ref or CanonType.FileRef:
            {
                var s = (string?)node ?? throw Err(where, f.Type, "строка формата uuid");
                if (!Guid.TryParse(s, out var g)) throw Err(where, f.Type, $"значение '{s}' не является Guid");
                return g;
            }
            case CanonType.String or CanonType.Text:
            {
                var s = (string?)node ?? throw Err(where, f.Type, "строка");
                if (f.Canon == CanonType.String && f.Length is int len && s.Length > len)
                    throw new DpException(DpCodes.LengthExceeded, 400, $"{where}: длина {s.Length} превышает length={len}");
                return s;
            }
            case CanonType.Boolean:
            {
                var b = (bool?)node ?? throw Err(where, f.Type, "boolean");
                return b;
            }
            case CanonType.Int32:
            {
                var i = (int?)node ?? throw Err(where, f.Type, "целое в диапазоне int32");
                return i;
            }
            case CanonType.Int64:
            {
                var l = (long?)node ?? throw Err(where, f.Type, "целое в диапазоне int64");
                return l;
            }
            case CanonType.Decimal or CanonType.Money:
            {
                decimal d;
                if ((decimal?)node is { } num) d = num;
                else if ((string?)node is { } str && decimal.TryParse(str, System.Globalization.CultureInfo.InvariantCulture, out var parsed)) d = parsed;
                else throw Err(where, f.Type, "десятичное число");
                var scale = f.Canon == CanonType.Money ? 4 : f.Scale ?? 0;
                var actualScale = System.Decimal.GetBits(d)[3] >> 16 & 0xFF;
                if (actualScale > scale)
                    throw new DpException(DpCodes.LengthExceeded, 400, $"{where}: scale {actualScale} превышает допустимый {scale}");
                return d;
            }
            case CanonType.Date:
            {
                var s = (string?)node ?? throw Err(where, f.Type, "строка YYYY-MM-DD");
                if (!DateOnly.TryParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
                    throw Err(where, f.Type, $"'{s}' не соответствует формату YYYY-MM-DD");
                return d;
            }
            case CanonType.Time:
            {
                var s = (string?)node ?? throw Err(where, f.Type, "строка HH:MM:SS");
                if (!TimeOnly.TryParseExact(s, "HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t)
                    && !TimeOnly.TryParseExact(s, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out t))
                    throw Err(where, f.Type, $"'{s}' не соответствует формату HH:MM[:SS]");
                return t;
            }
            case CanonType.Timestamp:
            {
                var s = (string?)node ?? throw Err(where, f.Type, "строка ISO-8601 с таймзоной");
                if (!OffsetTail.IsMatch(s))
                    throw new DpException(DpCodes.TypeMismatch, 400, $"{where}: наивные timestamp запрещены — требуется смещение UTC (Z или ±hh:mm)");
                if (!DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var ts))
                    throw Err(where, f.Type, $"'{s}' не является ISO-8601 timestamp");
                return ts;
            }
            case CanonType.Json:
            {
                if (node is not JsonObject obj) throw Err(where, f.Type, "JSON-объект");
                return obj.ToJsonString();
            }
            case CanonType.Binary:
            {
                var s = (string?)node ?? throw Err(where, f.Type, "строка base64");
                byte[] bytes;
                try { bytes = Convert.FromBase64String(s); }
                catch { throw Err(where, f.Type, "строка не является base64"); }
                var max = f.Length ?? 1024 * 1024;
                if (bytes.Length > max)
                    throw new DpException(DpCodes.LengthExceeded, 400, $"{where}: размер {bytes.Length} байт превышает лимит {max}");
                return bytes;
            }
            case CanonType.Enum:
            {
                var s = (string?)node ?? throw Err(where, f.Type, $"строка из набора [{string.Join(", ", f.Values)}]");
                if (!f.Values.Contains(s, StringComparer.Ordinal))
                    throw new DpException(DpCodes.EnumValueUnknown, 400, $"{where}: значение '{s}' вне набора enum [{string.Join(", ", f.Values)}]");
                return s;
            }
            default:
                throw Err(where, f.Type, "значение соответствующего канонического типа");
        }
    }

    private static DpException Err(string where, string type, string expected) =>
        new(DpCodes.TypeMismatch, 400, $"{where}: значение не соответствует каноническому типу '{type}' (ожидалось {expected})");

    /// <summary>
    /// Применение default (Insert / replace-Update). Правило missing ≠ null (табл. 6):
    /// missing → default; required без default → DP-1001.
    /// </summary>
    public static object? ApplyDefault(SdField f, string entityName)
    {
        if (string.IsNullOrWhiteSpace(f.Default))
        {
            if (f.Required)
                throw new DpException(DpCodes.MissingField, 400,
                    $"{entityName}.{f.Name}: required-поле отсутствует и не имеет default");
            return null;
        }
        var d = f.Default.Trim();
        return d switch
        {
            "gen_random_uuid()" => Guid.NewGuid(),
            "now()" or "current_timestamp" => DateTimeOffset.UtcNow,
            _ => LiteralOf(f, entityName, d)
        };
    }

    private static object? LiteralOf(SdField f, string entityName, string raw)
    {
        try
        {
            switch (f.Canon)
            {
                case CanonType.Boolean:
                    if (raw is "true" or "false") return raw == "true";
                    throw new DpException(DpCodes.TypeMismatch, 400, $"{entityName}.{f.Name}: boolean-default должен быть true|false");
                case CanonType.Json:
                {
                    var jn = JsonNode.Parse(raw) ?? throw new DpException(DpCodes.TypeMismatch, 400, $"{entityName}.{f.Name}: json-default '{raw}' не разобран");
                    if (jn is not JsonObject obj) throw new DpException(DpCodes.TypeMismatch, 400, $"{entityName}.{f.Name}: json-default должен быть объектом");
                    return obj.ToJsonString();
                }
                case CanonType.Int32 or CanonType.Int64 or CanonType.Decimal or CanonType.Money:
                    return FieldValidator.ValidateValue(f, JsonNode.Parse(raw) ?? throw new DpException(DpCodes.TypeMismatch, 400, $"{entityName}.{f.Name}: default '{raw}' не разобран"), entityName);
                default: // string, text, enum, uuid, date, time, timestamp — строковый литерал
                    return FieldValidator.ValidateValue(f, JsonValue.Create(raw), entityName);
            }
        }
        catch (DpException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DpException(DpCodes.TypeMismatch, 400, $"{entityName}.{f.Name}: default '{raw}' вне whitelist/типа ({ex.Message})");
        }
    }
}

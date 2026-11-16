using Dp.Platform.Contracts.Schema;

namespace Dp.Platform.Registry;

/// <summary>
/// Матрица совместимости изменений SD (Этап A, MVP-подмножество правил).
/// Данные, а не код: добавление правила = одна строка словаря (DRY/OCP).
/// </summary>
public static class CompatibilityRules
{
    public enum ChangeKind
    {
        Unchanged,
        AddOptionalField,
        AddRequiredField,
        RemoveField,
        RenameField,
        TypeChange,
        NullabilityTighten,
        EnumExtend,
        EnumNarrow,
        PrimaryKeyChange,
    }

    /// <summary>true = ломающее изменение (требует bump MAJOR).</summary>
    public static readonly IReadOnlyDictionary<ChangeKind, bool> Breaking =
        new Dictionary<ChangeKind, bool>
        {
            [ChangeKind.Unchanged] = false,
            [ChangeKind.AddOptionalField] = false,
            [ChangeKind.AddRequiredField] = true,
            [ChangeKind.RemoveField] = true,
            [ChangeKind.RenameField] = true,
            [ChangeKind.TypeChange] = true,
            [ChangeKind.NullabilityTighten] = true,
            [ChangeKind.EnumExtend] = false,
            [ChangeKind.EnumNarrow] = true,
            [ChangeKind.PrimaryKeyChange] = true,
        };

    public sealed record DetectedChange(ChangeKind Kind, string Subject, bool Breaking);

    /// <summary>Сравнивает старое и новое определение одного logical store.</summary>
    public static IReadOnlyList<DetectedChange> Diff(StoreDefinition old, StoreDefinition current)
    {
        var changes = new List<DetectedChange>();
        var oldFields = old.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var newFields = current.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);

        foreach (var (name, field) in newFields)
        {
            if (!oldFields.TryGetValue(name, out var previous))
            {
                changes.Add(new(field.Required ? ChangeKind.AddRequiredField : ChangeKind.AddOptionalField, name,
                    field.Required));
                continue;
            }
            if (!string.Equals(previous.Type, field.Type, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new(ChangeKind.TypeChange, name, true));
                continue;
            }
            if (previous.Required && !field.Required)
                changes.Add(new(ChangeKind.NullabilityTighten, name, true));
            if (previous.EnumValues is not null && field.EnumValues is not null)
            {
                var removed = previous.EnumValues.Except(field.EnumValues).Any();
                changes.Add(new(removed ? ChangeKind.EnumNarrow : ChangeKind.EnumExtend, name, removed));
            }
        }

        foreach (var name in oldFields.Keys.Except(newFields.Keys))
            changes.Add(new(ChangeKind.RemoveField, name, true));

        if (!string.Equals(old.PrimaryKey, current.PrimaryKey, StringComparison.Ordinal))
            changes.Add(new(ChangeKind.PrimaryKeyChange, old.PrimaryKey, true));

        // Расширения: только добавление целого namespace считается безопасным.
        var oldExt = old.Extensions?.Select(e => e.Namespace).ToHashSet() ?? [];
        foreach (var extension in current.Extensions ?? [])
        {
            if (!oldExt.Contains(extension.Namespace))
                changes.Add(new(ChangeKind.AddOptionalField, $"ext:{extension.Namespace}", false));
        }

        return changes;
    }
}

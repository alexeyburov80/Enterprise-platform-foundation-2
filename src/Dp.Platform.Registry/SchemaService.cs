using System.Collections.Immutable;
using Dp.Platform.Contracts.Schema;
using Dp.Platform.Framework;

namespace Dp.Platform.Registry;

/// <summary>
/// Бизнес-правила реестра схем (SRP): валидация SD DSL v1, семантика версий,
/// матрица совместимости. Не знает ни про HTTP, ни про детали PostgreSQL.
/// </summary>
public sealed class SchemaService(IStoreRepository repository)
{
    public async Task<Result<StoreDefinition>> RegisterAsync(
        StoreDefinition definition, CancellationToken ct = default)
    {
        var validation = Validate(definition);
        if (validation is not null) return Result<StoreDefinition>.Fail(validation);

        var latest = await repository.GetLatestAsync(definition.Store, ct);
        if (latest is not null)
        {
            var comparison = CompareVersions(definition.Version, latest.Version);
            if (comparison == 0)
                return Result<StoreDefinition>.Fail(DpCodes.DuplicateVersion,
                    $"Version {definition.Version} of '{definition.Store}' already exists.");

            var changes = CompatibilityRules.Diff(latest, definition);
            var breaking = changes.Where(c => c.Breaking).ToImmutableArray();
            var requiresMajor = breaking.Length > 0;

            if (requiresMajor && MajorOf(definition.Version) <= MajorOf(latest.Version))
                return Result<StoreDefinition>.Fail(DpCodes.BreakingChange,
                    "Breaking changes require a MAJOR version bump.",
                    new
                    {
                        latestVersion = latest.Version,
                        breakingChanges = breaking.Select(b => new { kind = b.Kind.ToString(), subject = b.Subject }),
                    });

            if (comparison < 0)
                return Result<StoreDefinition>.Fail(DpCodes.Validation,
                    $"Version must be greater than latest {latest.Version} (forward-only evolution).");
        }

        await repository.UpsertStoreAsync(definition.Store, ct);
        await repository.InsertVersionAsync(definition, ct);
        return definition;
    }

    public async Task<Result<StoreDefinition>> GetAsync(string store, string? version, CancellationToken ct = default)
    {
        var definition = version is null
            ? await repository.GetLatestAsync(store, ct)
            : await repository.GetVersionAsync(store, version, ct);
        return definition is not null
            ? definition
            : Result<StoreDefinition>.Fail(DpCodes.VersionNotFound,
                $"Store '{store}' has no version {(version ?? "(latest)")}.", new { store, version });
    }

    public Task<IReadOnlyList<StoreVersionInfo>> ListAsync(string? store, CancellationToken ct = default)
        => repository.ListVersionsAsync(store, ct);

    private static Error? Validate(StoreDefinition definition)
    {
        if (!StoreName.IsValid(definition.Store))
            return new Error(DpCodes.InvalidDsl,
                "Store name must match [a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)*, e.g. 'sales.orders'.");
        if (!VersionInfo.TryParse(definition.Version, out _))
            return new Error(DpCodes.InvalidDsl, "Version must be semver, e.g. '1.0.0'.");
        if (definition.Fields.Count == 0)
            return new Error(DpCodes.InvalidDsl, "At least one field is required.");
        if (definition.Fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != definition.Fields.Count)
            return new Error(DpCodes.InvalidDsl, "Field names must be unique.");
        if (definition.Fields.FirstOrDefault(f => f.Name == definition.PrimaryKey) is null)
            return new Error(DpCodes.InvalidDsl, $"PrimaryKey '{definition.PrimaryKey}' must reference an existing field.");

        foreach (var field in definition.Fields)
        {
            if (!FieldName.IsValid(field.Name))
                return new Error(DpCodes.InvalidDsl, $"Field '{field.Name}' must match [a-z][a-z0-9_]*.");
            if (!FieldKindMapper.TryParse(field.Type, out _))
                return new Error(DpCodes.UnsupportedType,
                    $"Field '{field.Name}': unknown type '{field.Type}'.",
                    new { allowed = FieldKindMapper.Names.Keys.OrderBy(k => k) });
        }

        foreach (var extension in definition.Extensions ?? [])
        {
            if (!ExtensionNamespace.IsValid(extension.Namespace))
                return new Error(DpCodes.InvalidDsl,
                    $"Extension namespace '{extension.Namespace}' must match [a-z][a-z0-9_]*.");
            if (extension.Fields.Any(f => !FieldName.IsValid(f.Name)))
                return new Error(DpCodes.InvalidDsl,
                    $"Extension '{extension.Namespace}' contains invalid field names.");
        }
        return null;
    }

    public static int CompareVersions(string left, string right)
    {
        VersionInfo.TryParse(left, out var l);
        VersionInfo.TryParse(right, out var r);
        return (l.Major, l.Minor, l.Patch).CompareTo((r.Major, r.Minor, r.Patch));
    }

    private static int MajorOf(string version)
    {
        VersionInfo.TryParse(version, out var info);
        return info.Major;
    }
}

/// <summary>Семвер (MVP: только Major.Minor.Patch).</summary>
public readonly record struct VersionInfo(int Major, int Minor, int Patch)
{
    public static bool TryParse(string? text, out VersionInfo info)
    {
        info = default;
        var parts = (text ?? "").Split('.');
        if (parts.Length != 3 || parts.Any(p => !int.TryParse(p, out var n) || n < 0)) return false;
        info = new VersionInfo(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
        return true;
    }
}

/// <summary>Безопасные идентификаторы (в SQL попадают только через валидацию).</summary>
public static class StoreName
{
    public static bool IsValid(string name)
        => !string.IsNullOrEmpty(name)
           && !name.StartsWith('.') && !name.EndsWith('.') && !name.Contains("..")
           && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.');
}

public static class FieldName
{
    public static bool IsValid(string name)
        => !string.IsNullOrEmpty(name)
           && char.IsAsciiLetterLower(name[0])
           && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');
}

public static class ExtensionNamespace
{
    public static bool IsValid(string ns) => FieldName.IsValid(ns);
}

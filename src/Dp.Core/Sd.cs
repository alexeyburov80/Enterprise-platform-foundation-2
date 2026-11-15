// ============================================================================
// SD DSL v1 — модель, загрузка YAML и валидация (Schema Registry MVP).
// Канон: apiVersion sd.platform/v1; kind LogicalStore | ExtensionSchema.
// 16 канонических типов по таблице 2 спецификации (решение A-3).
// ============================================================================

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Dp.Core;

public enum CanonType
{
    Uuid, String, Text, Boolean, Int32, Int64, Decimal, Money,
    Date, Time, Timestamp, Json, Binary, Enum, Ref, FileRef
}

// ---------------------------------------------------------------- модель YAML

public sealed class SdDocument
{
    public string ApiVersion { get; set; } = "";
    public string Kind { get; set; } = "";
    public SdMetadata Metadata { get; set; } = new();
    public SdSpec Spec { get; set; } = new();

    public string StoreId => $"{Metadata.Module}.{Metadata.Store}";
}

public sealed class SdMetadata
{
    public string Module { get; set; } = "";
    public string Store { get; set; } = "";
    public string Version { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Origin { get; set; } = "canonical";
}

public sealed class SdSpec
{
    public string Owner { get; set; } = "";
    public SdTenancy Tenancy { get; set; } = new();
    public List<SdEntity> Entities { get; set; } = new();
}

public sealed class SdTenancy
{
    public string Mode { get; set; } = "single";
}

public sealed class SdEntity
{
    public string Name { get; set; } = "";
    public string Table { get; set; } = "";
    public List<string> PrimaryKey { get; set; } = new();
    public SdLifecycle Lifecycle { get; set; } = new();
    public List<SdField> Fields { get; set; } = new();
    public List<SdConstraint> Constraints { get; set; } = new();
    public List<SdIndex> Indexes { get; set; } = new();
    public List<SdRelation> Relations { get; set; } = new();
}

public sealed class SdLifecycle
{
    public bool Audited { get; set; } = true;
    public bool SoftDelete { get; set; }
}

public sealed class SdField
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Required { get; set; }
    public string? Default { get; set; }
    public int? Length { get; set; }
    public int? Precision { get; set; }
    public int? Scale { get; set; }
    public List<string> Values { get; set; } = new();
    public string? RefEntity { get; set; }
    public string? Description { get; set; }

    public CanonType Canon => SdRegistry.ParseType(Type);
}

public sealed class SdConstraint
{
    public string Type { get; set; } = "";       // unique | check | fk
    public string Name { get; set; } = "";
    public List<string> Fields { get; set; } = new();
    public string? Expr { get; set; }
    public string? RefEntity { get; set; }
    public List<string> RefFields { get; set; } = new();
    public string OnDelete { get; set; } = "restrict";
}

public sealed class SdIndex
{
    public string Name { get; set; } = "";
    public List<string> Fields { get; set; } = new();   // "field ASC|DESC"
    public string Method { get; set; } = "btree";
}

public sealed class SdRelation
{
    public string Name { get; set; } = "";
    public string Entity { get; set; } = "";
    public string Kind { get; set; } = "one-to-many";
    public string LocalKey { get; set; } = "id";
    public string ForeignKey { get; set; } = "";
}

/// <summary>ExtensionSchema — расширение сущности чужим модулем (jsonb + namespace).</summary>
public sealed class SdExtensionDocument
{
    public string ApiVersion { get; set; } = "";
    public string Kind { get; set; } = "";
    public SdExtensionMetadata Metadata { get; set; } = new();
    public SdExtensionSpec Spec { get; set; } = new();

    public string StoreId => $"{Metadata.Module}.{Metadata.Store}";
}

public sealed class SdExtensionMetadata
{
    public string Module { get; set; } = "";
    public string Store { get; set; } = "";
    public string Namespace { get; set; } = "";
    public string Version { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public sealed class SdExtensionSpec
{
    public SdExtensionAppliesTo AppliesTo { get; set; } = new();
    public List<SdField> Fields { get; set; } = new();
}

public sealed class SdExtensionAppliesTo
{
    public string Store { get; set; } = "";   // "module.store"
    public string Entity { get; set; } = "";
}

// ------------------------------------------------------------------- реестр

public sealed class SdRegistry
{
    private static readonly string[] CanonTypes =
    {
        "uuid", "string", "text", "boolean", "int32", "int64", "decimal", "money",
        "date", "time", "timestamp", "json", "binary", "enum", "ref", "fileRef"
    };

    private readonly ILogger<SdRegistry> _logger;
    private readonly Dictionary<string, SdDocument> _stores = new(StringComparer.OrdinalIgnoreCase);
    // "store/entity" -> namespace -> поля расширения
    private readonly Dictionary<string, Dictionary<string, List<SdField>>> _extensions = new(StringComparer.OrdinalIgnoreCase);

    public string SdPath { get; }

    public SdRegistry(ILogger<SdRegistry> logger, string sdPath)
    {
        _logger = logger;
        SdPath = sdPath;
        Load();
    }

    public IReadOnlyCollection<SdDocument> Stores => _stores.Values;

    public SdDocument FindStore(string storeId) =>
        _stores.TryGetValue(storeId, out var s) ? s
        : throw new DpException(DpCodes.StoreNotRegistered, 404,
            $"Store '{storeId}' не зарегистрирован в Schema Registry (Sd:Path={SdPath})");

    public SdEntity GetEntity(SdDocument store, string entityName) =>
        store.Spec.Entities.FirstOrDefault(e => string.Equals(e.Name, entityName, StringComparison.OrdinalIgnoreCase))
        ?? throw new DpException(DpCodes.SchemaNotFound, 404,
            $"Сущность '{entityName}' отсутствует в каноне store'а '{store.StoreId}'");

    public IReadOnlyDictionary<string, List<SdField>> ExtensionsFor(string storeId, string entityName) =>
        _extensions.TryGetValue($"{storeId}/{entityName}", out var ns)
            ? ns
            : new Dictionary<string, List<SdField>>();

    public static CanonType ParseType(string type)
    {
        var t = type switch
        {
            "string(n)" => "string",
            "binary(n)" => "binary",
            "decimal(p,s)" => "decimal",
            _ => type
        };
        return t.ToLowerInvariant() switch
        {
            "uuid" => CanonType.Uuid,
            "string" => CanonType.String,
            "text" => CanonType.Text,
            "boolean" => CanonType.Boolean,
            "int32" => CanonType.Int32,
            "int64" => CanonType.Int64,
            "decimal" => CanonType.Decimal,
            "money" => CanonType.Money,
            "date" => CanonType.Date,
            "time" => CanonType.Time,
            "timestamp" => CanonType.Timestamp,
            "json" => CanonType.Json,
            "binary" => CanonType.Binary,
            "enum" => CanonType.Enum,
            "ref" => CanonType.Ref,
            "fileref" => CanonType.FileRef,
            _ => throw new DpException(DpCodes.SchemaNotFound, 500, $"Неизвестный канонический тип '{type}' (ожидаются: {string.Join(", ", CanonTypes)})")
        };
    }

    /// <summary>Загрузка (перезагрузка) всех SD-документов каталога Sd:Path.</summary>
    public void Load()
    {
        if (!Directory.Exists(SdPath))
            throw new InvalidOperationException($"Каталог SD не найден: {SdPath}");

        var errors = new List<string>();
        var stores = new Dictionary<string, SdDocument>(StringComparer.OrdinalIgnoreCase);
        var extensions = new Dictionary<string, Dictionary<string, List<SdField>>>(StringComparer.OrdinalIgnoreCase);
        // двухпроходная загрузка: сначала все LogicalStore (расширения ссылаются на store'ы)
        var extensionDocs = new List<SdExtensionDocument>();

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        foreach (var file in Directory.EnumerateFiles(SdPath, "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                              || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file);
            SdDocument? doc;
            try
            {
                doc = deserializer.Deserialize<SdDocument>(text);
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(file)}: не удалось разобрать YAML ({ex.Message})");
                continue;
            }
            if (doc is null || string.IsNullOrEmpty(doc.Kind))
            {
                errors.Add($"{Path.GetFileName(file)}: пустой или некорректный SD-документ");
                continue;
            }

            switch (doc.Kind)
            {
                case "LogicalStore":
                    ValidateStore(doc, errors);
                    if (stores.ContainsKey(doc.StoreId))
                        errors.Add($"{Path.GetFileName(file)}: дубликат store'а {doc.StoreId}");
                    else
                        stores[doc.StoreId] = doc;
                    break;

                case "ExtensionSchema":
                {
                    // повторная десериализация в тип расширения (другая форма документа)
                    SdExtensionDocument? ext;
                    try
                    {
                        ext = deserializer.Deserialize<SdExtensionDocument>(text);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{Path.GetFileName(file)}: не удалось разобрать ExtensionSchema ({ex.Message})");
                        continue;
                    }
                    if (ext is null) continue;
                    extensionDocs.Add(ext);
                    break;
                }

                default:
                    errors.Add($"{Path.GetFileName(file)}: неизвестный kind '{doc.Kind}' (ожидаются LogicalStore | ExtensionSchema)");
                    break;
            }
        }

        // второй проход: расширения — после того как все store'ы собраны
        foreach (var ext in extensionDocs)
        {
            ValidateExtension(ext, errors, stores);
            var key = $"{ext.StoreId}/{ext.Spec.AppliesTo.Entity}";
            if (!extensions.TryGetValue(key, out var nsMap))
                extensions[key] = nsMap = new Dictionary<string, List<SdField>>(StringComparer.OrdinalIgnoreCase);
            if (nsMap.ContainsKey(ext.Metadata.Namespace))
                errors.Add($"ext:{ext.Metadata.Namespace}: дубликат namespace для {key}");
            else
                nsMap[ext.Metadata.Namespace] = ext.Spec.Fields;
        }

        if (errors.Count > 0)
            throw new InvalidOperationException(
                "Валидация SD-каталога не пройдена:\n  - " + string.Join("\n  - ", errors));

        _stores.Clear();
        foreach (var (id, s) in stores) _stores[id] = s;
        _extensions.Clear();
        foreach (var (k, v) in extensions) _extensions[k] = v;

        _logger.LogInformation("SD Registry: загружено {Stores} store'ов, {Ext} расширений из {Path}",
            _stores.Count, extensions.Count, SdPath);
    }

    // ------------------------------------------------------------- валидация

    private static void ValidateStore(SdDocument doc, List<string> errors)
    {
        var file = doc.StoreId;
        if (doc.ApiVersion != "sd.platform/v1") errors.Add($"{file}: apiVersion должен быть sd.platform/v1");
        if (!RegexLikeVersion(doc.Metadata.Version)) errors.Add($"{file}: версия '{doc.Metadata.Version}' не соответствует semver X.Y.Z");

        // все сущности store'а известны заранее — ref/refEntity/relation против полного набора
        var seenEntities = doc.Spec.Entities.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var e in doc.Spec.Entities)
        {
            if (!e.PrimaryKey.Any()) errors.Add($"{file}/{e.Name}: primaryKey обязателен явно");
            var pkSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pk in e.PrimaryKey)
            {
                if (!pkSeen.Add(pk)) errors.Add($"{file}/{e.Name}: дубликат в primaryKey ('{pk}')");
                if (e.Fields.All(f => !string.Equals(f.Name, pk, StringComparison.OrdinalIgnoreCase)))
                    errors.Add($"{file}/{e.Name}: primaryKey ссылается на неизвестное поле '{pk}'");
            }

            var fieldNames = e.Fields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var dup in e.Fields.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                errors.Add($"{file}/{e.Name}: дубликат поля '{dup.Key}'");
            foreach (var f in e.Fields)
            {
                try { ParseType(f.Type); }
                catch (DpException ex) { errors.Add($"{file}/{e.Name}/{f.Name}: {ex.Detail}"); }
                if (f.Type == "string" && f.Length is < 1 or > 4096)
                    errors.Add($"{file}/{e.Name}/{f.Name}: string требует length 1..4096");
                if (f.Type == "decimal")
                {
                    if (f.Precision is < 1 or > 38) errors.Add($"{file}/{e.Name}/{f.Name}: decimal требует precision 1..38");
                    if (f.Scale is > 38 or < 0 || f.Scale > f.Precision) errors.Add($"{file}/{e.Name}/{f.Name}: scale должен быть 0..precision");
                }
                if (f.Type == "enum" && f.Values.Count == 0) errors.Add($"{file}/{e.Name}/{f.Name}: enum требует values [...]");
                if (f.Type == "ref" && !string.IsNullOrEmpty(f.RefEntity) && !seenEntities.Contains(f.RefEntity))
                    errors.Add($"{file}/{e.Name}/{f.Name}: refEntity '{f.RefEntity}' отсутствует в store'е (FK внутри store'а)");
            }

            foreach (var c in e.Constraints)
            {
                foreach (var cf in c.Fields)
                    if (!fieldNames.Contains(cf)) errors.Add($"{file}/{e.Name}: constraint '{c.Name}' ссылается на неизвестное поле '{cf}'");
                if (c.Type == "fk" && !string.IsNullOrEmpty(c.RefEntity) && !seenEntities.Contains(c.RefEntity))
                    errors.Add($"{file}/{e.Name}: fk '{c.Name}' ссылается на неизвестную сущность '{c.RefEntity}'");
            }
            foreach (var ix in e.Indexes)
                foreach (var ifield in ix.Fields.Select(f => f.Split(' ')[0]))
                    if (!fieldNames.Contains(ifield)) errors.Add($"{file}/{e.Name}: index '{ix.Name}' ссылается на неизвестное поле '{ifield}'");
            foreach (var r in e.Relations)
                if (!seenEntities.Contains(r.Entity)) errors.Add($"{file}/{e.Name}: relation '{r.Name}' ссылается на неизвестную сущность '{r.Entity}'");
        }
    }

    private static void ValidateExtension(SdExtensionDocument doc, List<string> errors,
        Dictionary<string, SdDocument> stores)
    {
        var file = $"{doc.StoreId}/ext:{doc.Metadata.Namespace}";
        if (doc.ApiVersion != "sd.platform/v1") errors.Add($"{file}: apiVersion должен быть sd.platform/v1");
        if (string.IsNullOrWhiteSpace(doc.Metadata.Namespace))
            errors.Add($"{file}: namespace расширения обязателен");
        if (!stores.TryGetValue(doc.StoreId, out var store))
        {
            errors.Add($"{file}: store '{doc.StoreId}' не найден среди LogicalStore-документов");
            return;
        }
        if (store.Spec.Entities.All(e => !string.Equals(e.Name, doc.Spec.AppliesTo.Entity, StringComparison.OrdinalIgnoreCase)))
            errors.Add($"{file}: сущность '{doc.Spec.AppliesTo.Entity}' не найдена в store'е '{doc.StoreId}'");
        foreach (var f in doc.Spec.Fields)
            try { ParseType(f.Type); }
            catch (DpException ex) { errors.Add($"{file}/{f.Name}: {ex.Detail}"); }
    }

    private static bool RegexLikeVersion(string v)
    {
        var parts = v.Split('.');
        return parts.Length == 3 && parts.All(p => p.Length > 0 && p.All(char.IsDigit));
    }
}

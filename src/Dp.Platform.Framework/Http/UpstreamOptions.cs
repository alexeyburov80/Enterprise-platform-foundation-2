namespace Dp.Platform.Framework;

using Microsoft.Extensions.Options;

/// <summary>
/// Режим работы адаптера интеграции (требование ревью: замещение без изменения кода).
/// real — только внешняя система; stub — только наш имитатор;
/// auto — внешняя система, при её недоступности автоматически имитатор (замещение).
/// </summary>
public enum UpstreamMode
{
    Real,
    Stub,
    Auto,
}

/// <summary>Настройки upstream интеграционного адаптера (binding из секции Upstream).</summary>
public sealed class UpstreamOptions
{
    public const string SectionName = "Upstream";

    private UpstreamMode _mode = UpstreamMode.Auto;

    public string Mode
    {
        get => _mode.ToString().ToLowerInvariant();
        set
        {
            if (!Enum.TryParse<UpstreamMode>(value, ignoreCase: true, out var parsed))
                throw new OptionsValidationException(
                    SectionName, typeof(UpstreamMode), [$"Unknown upstream mode '{value}'. Use real|stub|auto."]);
            _mode = parsed;
        }
    }

    /// <summary>Базовый URL внешней системы (например, реальной 1С).</summary>
    public string PrimaryUrl { get; set; } = "http://localhost:8090";

    /// <summary>Базовый URL нашего имитатора, замещающего систему (например, stub-1c).</summary>
    public string FallbackUrl { get; set; } = "http://localhost:8091";

    /// <summary>Путь проверки живости внешней системы.</summary>
    public string ProbePath { get; set; } = "/health";

    public int ProbeIntervalSeconds { get; set; } = 5;

    public UpstreamMode ParsedMode => _mode;
    public Uri Primary => new(PrimaryUrl);
    public Uri Fallback => new(FallbackUrl);
}

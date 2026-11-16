using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dp.Platform.Framework;

/// <summary>Состояние upstream для чтения обработчиками (DIP: только факты, без потоков).</summary>
public interface IUpstreamState
{
    UpstreamOptions Options { get; }
    bool PrimaryHealthy { get; }
    /// <summary>Какой источник обслужит следующий запрос: "real" или "stub".</summary>
    string ActiveName { get; }
    Uri Active { get; }
}

/// <summary>
/// Фоновый монитор живости внешней системы + выбор активного upstream.
/// Реализует требование замещения: если 1С/Битрикс недоступны, трафик
/// автоматически уходит на наш имитатор — без изменения кода (только конфиг).
/// </summary>
public sealed class UpstreamSwitcher : BackgroundService, IUpstreamState
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<UpstreamSwitcher> _log;
    private volatile bool _primaryHealthy = true;

    public UpstreamSwitcher(IOptions<UpstreamOptions> options, IHttpClientFactory http, ILogger<UpstreamSwitcher> log)
    {
        Options = options.Value;
        _http = http;
        _log = log;
    }

    public UpstreamOptions Options { get; }
    public bool PrimaryHealthy => _primaryHealthy;

    public string ActiveName => Options.ParsedMode switch
    {
        UpstreamMode.Real => "real",
        UpstreamMode.Stub => "stub",
        _ => _primaryHealthy ? "real" : "stub",
    };

    public Uri Active => Options.ParsedMode switch
    {
        UpstreamMode.Real => Options.Primary,
        UpstreamMode.Stub => Options.Fallback,
        _ => _primaryHealthy ? Options.Primary : Options.Fallback,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, Options.ProbeIntervalSeconds)));
        do
        {
            await ProbeAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ProbeAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var client = _http.CreateClient("dp-upstream-probe");
            var probeUrl = Options.Primary.AbsoluteUri.TrimEnd('/') + Options.ProbePath;
            using var response = await client.GetAsync(probeUrl, timeout.Token);
            SetHealthy(response.IsSuccessStatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   or InvalidOperationException or UriFormatException)
        {
            SetHealthy(false);
        }
    }

    private void SetHealthy(bool healthy)
    {
        if (_primaryHealthy != healthy)
            _log.LogWarning("Upstream {Primary} is now {State}", Options.Primary, healthy ? "HEALTHY" : "DOWN");
        _primaryHealthy = healthy;
    }
}

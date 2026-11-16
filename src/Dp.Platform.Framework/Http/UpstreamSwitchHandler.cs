namespace Dp.Platform.Framework;

/// <summary>
/// Делегирующий обработчик: запрос всегда строится против Primary (как у реальной
/// системы), а уходит в Active (real|stub). В режиме auto первый сбой соединения
/// или 502/503/504 немедленно ретраится на имитатор — переключение прозрачно
/// для вызывающего кода (SRP: только маршрутизация, без бизнес-логики).
/// </summary>
public sealed class UpstreamSwitchHandler(IUpstreamState state) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var original = request.RequestUri ?? throw new InvalidOperationException("Request URI required.");
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(ct);
        var contentType = request.Content?.Headers.ContentType?.ToString();

        var attempts = state.Options.ParsedMode == UpstreamMode.Auto ? 2 : 1;
        for (var attempt = 0; ; attempt++)
        {
            request.RequestUri = Rewrite(original, state.Active);
            request.Content = body is null ? null : ToBufferedContent(body, contentType);

            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, ct);
            }
            catch (HttpRequestException) when (attempt < attempts - 1)
            {
                continue; // соединение с primary упало — сразу пробуем замещение
            }

            if (attempt < attempts - 1 && IsUpstreamFailure(response.StatusCode))
            {
                response.Dispose();
                continue;
            }
            return response;
        }
    }

    private static bool IsUpstreamFailure(System.Net.HttpStatusCode statusCode)
        => statusCode is System.Net.HttpStatusCode.BadGateway
            or System.Net.HttpStatusCode.ServiceUnavailable
            or System.Net.HttpStatusCode.GatewayTimeout;

    /// <summary>Пересобирает адрес против target, сохраняя остаток пути за базой primary.</summary>
    private Uri Rewrite(Uri original, Uri target)
    {
        var primary = state.Options.Primary;
        if (target == primary) return original;

        var path = original.PathAndQuery;
        var primaryBase = primary.PathAndQuery.TrimEnd('/');
        if (primaryBase.Length > 0 && path.StartsWith(primaryBase, StringComparison.Ordinal))
            path = path[primaryBase.Length..];

        var targetRoot = new Uri(target.AbsoluteUri.TrimEnd('/') + "/");
        return new Uri(targetRoot, path.TrimStart('/'));
    }

    private static HttpContent ToBufferedContent(byte[] body, string? contentType)
    {
        var content = new ByteArrayContent(body);
        if (contentType is not null) content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        return content;
    }
}

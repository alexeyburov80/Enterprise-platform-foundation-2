namespace Dp.Platform.Gateway;

/// <summary>
/// Маршруты шлюза: префикс пути -> базовый URL сервиса (binding из секции Routes).
/// Конфиг, а не код: новый сервис = одна строка конфигурации (OCP).
/// </summary>
public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>Префикс -> upstream, например /api/v1/data -> http://data:8082.</summary>
    public Dictionary<string, string> Routes { get; set; } = [];

    /// <summary>Имя -> базовый URL для агрегированного /health/services.</summary>
    public Dictionary<string, string> Services { get; set; } = [];

    /// <summary>Токен API (MVP: offline-заглушка вместо OIDC, Этап B заменит на Keycloak).</summary>
    public string ApiToken { get; set; } = "dp-dev-token";
}

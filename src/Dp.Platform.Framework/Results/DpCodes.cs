namespace Dp.Platform.Framework;

using Microsoft.AspNetCore.Http;

/// <summary>
/// Реестр кодов ошибок Data Platform (Этап A, реестр DP-XXXX).
/// Категории: 10xx валидация/DSL, 11xx реестр, 12xx данные, 13xx запросы,
/// 14xx файлы, 15xx интеграции, 16xx события, 19xx внутренние.
/// </summary>
public static class DpCodes
{
    // Валидация и DSL
    public const string Validation = "DP-1000";
    public const string InvalidDsl = "DP-1001";
    public const string UnsupportedType = "DP-1002";
    public const string DuplicateVersion = "DP-1003";
    public const string BreakingChange = "DP-1004";

    // Реестр схем
    public const string StoreNotFound = "DP-1010";
    public const string VersionNotFound = "DP-1011";

    // Данные
    public const string RecordNotFound = "DP-1020";
    public const string DuplicateKey = "DP-1021";

    // Запросы (Query DSL)
    public const string InvalidQuery = "DP-1030";

    // Файлы
    public const string FileNotFound = "DP-1040";
    public const string FileTooLarge = "DP-1041";
    public const string FileCorrupted = "DP-1042";

    // Интеграции (адаптеры/замещение)
    public const string UpstreamUnavailable = "DP-1050";
    public const string UpstreamBadResponse = "DP-1051";
    public const string AdapterModeInvalid = "DP-1052";

    // События
    public const string EventPublishFailed = "DP-1060";

    // Внутренние
    public const string Internal = "DP-1099";

    /// <summary>HTTP-статус по умолчанию для кода (маппинг в одном месте, DRY).</summary>
    public static int HttpStatusOf(string code) => code switch
    {
        StoreNotFound or VersionNotFound or RecordNotFound or FileNotFound
            => StatusCodes.Status404NotFound,
        DuplicateVersion or BreakingChange or DuplicateKey
            => StatusCodes.Status409Conflict,
        UpstreamUnavailable or UpstreamBadResponse
            => StatusCodes.Status502BadGateway,
        Internal or EventPublishFailed
            => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status400BadRequest,
    };
}

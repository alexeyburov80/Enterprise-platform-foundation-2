using Dp.Platform.Framework;

namespace Dp.Platform.Data;

/// <summary>
/// Исключение-носитель кода ошибки платформы: операции бросают, эндпоинт
/// превращает в RFC 9457. Единый путь всех доменных ошибок (DRY).
/// </summary>
public sealed class DpFaultException(string code, string message, object? details = null)
    : Exception(message)
{
    public Error Error { get; } = new(code, message, details);
}

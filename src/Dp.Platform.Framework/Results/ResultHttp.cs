using Microsoft.AspNetCore.Http;

namespace Dp.Platform.Framework;

/// <summary>
/// Мост Result -> HTTP в формате RFC 9457 (Problem Details).
/// DRY: единственная точка преобразования ошибок платформы в HTTP-ответы.
/// </summary>
public static class ResultHttp
{
    public static IResult ToProblem(this Error error)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code };
        if (error.Details is not null) extensions["details"] = error.Details;

        return Results.Problem(
            title: error.Code,
            detail: error.Message,
            statusCode: DpCodes.HttpStatusOf(error.Code),
            type: $"urn:dp:errors:{error.Code}",
            extensions: extensions);
    }

    public static IResult ToProblem(this Result result)
        => result.Ok
            ? throw new InvalidOperationException("Success result has no problem to return.")
            : result.Failure!.ToProblem();

    public static IResult ToHttp<T>(this Result<T> result, Func<T, IResult> onSuccess)
        => result.Ok ? onSuccess(result.Value!) : result.Failure!.ToProblem();
}

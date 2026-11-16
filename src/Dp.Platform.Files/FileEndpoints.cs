using Dp.Platform.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Dp.Platform.Files;

/// <summary>HTTP-контракт файлов: upload (multipart), download, meta, tombstone-delete.</summary>
public static class FileEndpoints
{
    public static WebApplication MapFileEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1/files");

        // DisableAntiforgery: это API-сервис, а не браузерная форма (.NET 8
        // автоматически добавляет anti-forgery metadata к IFormFile-эндпоинтам).
        api.MapPost("/", Upload).DisableAntiforgery();
        api.MapGet("/{id}/meta", GetMeta);
        api.MapGet("/{id}", Download);
        api.MapDelete("/{id}", Delete);

        return app;
    }

    private static async Task<IResult> Upload(IFormFile file, FileService service, CancellationToken ct)
        => (await service.UploadAsync(file.OpenReadStream(), file.ContentType, file.FileName, ct))
            .ToHttp(meta => Results.Json(meta, statusCode: StatusCodes.Status201Created));

    private static async Task<IResult> GetMeta(string id, FileService service, CancellationToken ct)
        => (await service.GetMetaAsync(id, ct)).ToHttp(Results.Ok);

    private static async Task<IResult> Download(string id, FileService service, CancellationToken ct)
        => (await service.DownloadAsync(id, ct)).ToHttp(pair =>
        {
            var (meta, content) = pair;
            return Results.Stream(content, meta.ContentType, fileDownloadName: meta.OriginalName);
        });

    private static async Task<IResult> Delete(string id, FileService service, CancellationToken ct)
        => (await service.DeleteAsync(id, ct)).ToHttp(Results.Ok);
}

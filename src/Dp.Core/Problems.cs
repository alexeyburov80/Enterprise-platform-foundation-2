// ============================================================================
// Ошибки Data Platform: RFC 9457 Problem Details + реестр кодов DP-XXXX
// (таблица 15 спецификации). MVP-локальные дополнения (кандидаты в реестр
// при ревью): DP-6201 MIGRATION_PACKAGE_MISMATCH,
// DP-4002 SYNC_WORKER_UNAVAILABLE (dp-api не может достучаться до dp-sync).
// ============================================================================

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dp.Core;

public sealed class DpException : Exception
{
    public string? Code { get; }
    public int Status { get; }
    public string Detail { get; }

    public DpException(string code, int status, string detail) : base(detail)
    {
        Code = code;
        Status = status;
        Detail = detail;
    }

    /// <summary>404 без DP-кода (запись не найдена — RFC 9457 позволяет).</summary>
    public static DpException RecordNotFound(string detail) => new(null!, 404, detail);
}

public static class DpCodes
{
    public const string MissingField          = "DP-1001 MISSING_FIELD";
    public const string NullNotAllowed        = "DP-1002 NULL_NOT_ALLOWED";
    public const string TypeMismatch          = "DP-1003 TYPE_MISMATCH";
    public const string EnumValueUnknown      = "DP-1004 ENUM_VALUE_UNKNOWN";
    public const string LengthExceeded        = "DP-1005 LENGTH_EXCEEDED";
    public const string ConstraintViolated    = "DP-1006 CONSTRAINT_VIOLATED";
    public const string DuplicateKey          = "DP-1007 DUPLICATE_KEY";
    public const string DslSyntaxInvalid      = "DP-1008 DSL_SYNTAX_INVALID";
    public const string DslOperatorUnsupported = "DP-1009 DSL_OPERATOR_UNSUPPORTED";
    public const string PayloadTooLarge       = "DP-1010 PAYLOAD_TOO_LARGE";
    public const string SchemaNotFound        = "DP-2001 SCHEMA_NOT_FOUND";
    public const string SchemaVersionNotDeployed = "DP-2002 SCHEMA_VERSION_NOT_DEPLOYED";
    public const string StoreNotRegistered    = "DP-2003 STORE_NOT_REGISTERED";
    public const string StoreVersionDeprecated = "DP-2004 STORE_VERSION_DEPRECATED";
    public const string StoreAccessDenied     = "DP-3001 STORE_ACCESS_DENIED";
    public const string AuthTokenInvalid      = "DP-3003 AUTH_TOKEN_INVALID";
    public const string ProviderUnavailable   = "DP-4001 PROVIDER_UNAVAILABLE";
    public const string SyncWorkerUnavailable = "DP-4002 SYNC_WORKER_UNAVAILABLE";
    public const string DbTimeout             = "DP-4003 DB_TIMEOUT";
    public const string SerializationFailure  = "DP-4004 SERIALIZATION_FAILURE";
    public const string IdempotencyKeyConflict = "DP-5001 IDEMPOTENCY_KEY_CONFLICT";
    public const string ConcurrentUpdate      = "DP-5002 CONCURRENT_UPDATE";
    public const string MigrationPackageMismatch = "DP-6201 MIGRATION_PACKAGE_MISMATCH";
    public const string Internal              = "DP-0000 INTERNAL";
}

public static class Problems
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task WriteAsync(HttpContext ctx, int status, string? code, string? detail)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/problem+json";
        var problem = new JsonObject
        {
            ["type"] = code is null ? "about:blank" : $"https://dp.local/errors/{code.Split(' ')[0]}",
            ["title"] = TitleOf(status),
            ["status"] = status,
            ["detail"] = detail ?? "",
            ["traceId"] = ctx.TraceIdentifier
        };
        if (code is not null) problem["code"] = code;
        await ctx.Response.WriteAsync(problem.ToJsonString(Options));
    }

    public static DpException FromPostgres(Npgsql.PostgresException pg) => pg.SqlState switch
    {
        "23505" => new DpException(DpCodes.DuplicateKey, 409, $"Нарушена уникальность ({pg.ConstraintName ?? pg.Detail ?? pg.MessageText})"),
        "23514" => new DpException(DpCodes.ConstraintViolated, 400, $"Нарушен check-констрейнт ({pg.ConstraintName ?? pg.Detail ?? pg.MessageText})"),
        "23503" => new DpException(DpCodes.ConstraintViolated, 400, $"Нарушена ссылочная целостность FK ({pg.ConstraintName ?? pg.Detail ?? pg.MessageText})"),
        "23502" => new DpException(DpCodes.NullNotAllowed, 400, $"Явный NULL для NOT NULL-колонки ({pg.ColumnName ?? pg.Detail ?? pg.MessageText})"),
        "40001" => new DpException(DpCodes.SerializationFailure, 409, "Конфликт транзакций; операцию можно повторить"),
        "57014" => new DpException(DpCodes.DbTimeout, 504, "Тайм-аут СУБД"),
        _ => new DpException(DpCodes.Internal, 500, $"Ошибка PostgreSQL {pg.SqlState}: {pg.MessageText}")
    };

    private static string TitleOf(int status) => status switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        410 => "Gone",
        413 => "Payload Too Large",
        422 => "Unprocessable Content",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => "Error"
    };
}

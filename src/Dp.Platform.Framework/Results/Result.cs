namespace Dp.Platform.Framework;

/// <summary>Единый контракт ошибки: код DP-XXXX + человекочитаемое сообщение.</summary>
public sealed record Error(string Code, string Message, object? Details = null);

/// <summary>Result без значения (SRP: сигнализирует только успех/неуспех).</summary>
public sealed record Result
{
    public bool Ok { get; private init; }
    public Error? Failure { get; private init; }

    public static Result Success() => new() { Ok = true };
    public static Result Fail(Error error) => new() { Ok = false, Failure = error };
    public static Result Fail(string code, string message, object? details = null)
        => Fail(new Error(code, message, details));

    public static implicit operator Result(Error error) => Fail(error);
}

/// <summary>Result со значением (OCP: новые типы операций не меняют контракт).</summary>
public sealed record Result<T>
{
    public bool Ok { get; private init; }
    public T? Value { get; private init; }
    public Error? Failure { get; private init; }

    public static Result<T> Success(T value) => new() { Ok = true, Value = value };
    public static Result<T> Fail(Error error) => new() { Ok = false, Failure = error };
    public static Result<T> Fail(string code, string message, object? details = null)
        => Fail(new Error(code, message, details));

    public static implicit operator Result<T>(T value) => Success(value);
}

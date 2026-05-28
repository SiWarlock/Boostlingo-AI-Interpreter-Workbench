using System.Text.Json.Serialization;

namespace AiInterpreter.Api.Common;

/// <summary>Minimal success/failure result for void operations (e.g. best-effort persistence in B.7).</summary>
public sealed class Result
{
    private Result(bool isSuccess, string? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    // Control-flow type, never a serialized contract — ignore so an accidental serialization
    // can't leak internal error text to a client (ARCH-018/019).
    [JsonIgnore]
    public string? Error { get; }

    public static Result Success() => new(true, null);

    public static Result Failure(string error) => new(false, error);
}

/// <summary>
/// Minimal success/failure result carrying a value. <see cref="Value"/> throws on a failed result
/// so a failure can never be silently read as a value (consumed by later seams — cascade,
/// providers, persistence).
/// </summary>
public sealed class Result<T>
{
    private readonly T _value;

    private Result(bool isSuccess, T value, string? error)
    {
        IsSuccess = isSuccess;
        _value = value;
        Error = error;
    }

    public bool IsSuccess { get; }

    [JsonIgnore]
    public string? Error { get; }

    [JsonIgnore]
    public T Value => IsSuccess
        ? _value
        : throw new InvalidOperationException("Cannot access Value on a failed Result.");

    public static Result<T> Success(T value) => new(true, value, null);

    // default! is never read: Value throws on a failed result before _value is accessed.
    public static Result<T> Failure(string error) => new(false, default!, error);
}

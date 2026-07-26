namespace AiKnowledgeAssistant.Domain.Common;

/// <summary>
/// Outcome of a business flow: either a value or an error. Used instead of exceptions for
/// expected failures (unreadable PDF, embedding provider down, batch over the file cap),
/// as required by the <c>dotnet-backend-patterns</c> skill.
/// </summary>
/// <typeparam name="T">Type carried on success.</typeparam>
public sealed class Result<T>
{
    private Result(bool isSuccess, T? value, string? error, string? errorCode)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
        ErrorCode = errorCode;
    }

    /// <summary>True when the flow succeeded and <see cref="Value"/> is populated.</summary>
    public bool IsSuccess { get; }

    /// <summary>The produced value, or <c>default</c> on failure.</summary>
    public T? Value { get; }

    /// <summary>Human-readable failure description, or <c>null</c> on success.</summary>
    public string? Error { get; }

    /// <summary>Machine-readable failure code (e.g. <c>TooManyFiles</c>), or <c>null</c>.</summary>
    public string? ErrorCode { get; }

    public static Result<T> Success(T value) => new(true, value, null, null);

    public static Result<T> Failure(string error, string? code = null) => new(false, default, error, code);

    /// <summary>Projects the value on success; propagates the error untouched on failure.</summary>
    public Result<TNew> Map<TNew>(Func<T, TNew> mapper) =>
        IsSuccess ? Result<TNew>.Success(mapper(Value!)) : Result<TNew>.Failure(Error!, ErrorCode);

    /// <summary>Asynchronous counterpart of <see cref="Map{TNew}"/>.</summary>
    public async Task<Result<TNew>> MapAsync<TNew>(Func<T, Task<TNew>> mapper) =>
        IsSuccess ? Result<TNew>.Success(await mapper(Value!)) : Result<TNew>.Failure(Error!, ErrorCode);
}

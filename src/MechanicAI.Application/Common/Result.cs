using System.Diagnostics.CodeAnalysis;

namespace MechanicAI.Application.Common;

public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Timeout,
    Unavailable,
    RateLimited,
    Unauthorized,
    InvalidResponse,
    NotConfigured,
    Offline,
    Cancelled,
    Unexpected,
}

/// <summary>A user-presentable error. <see cref="Message"/> is safe to show; <see cref="Detail"/> is for logs.</summary>
public sealed record Error(ErrorKind Kind, string Message, string? Detail = null)
{
    public static Error Validation(string message) => new(ErrorKind.Validation, message);

    public static Error NotFound(string what) => new(ErrorKind.NotFound, $"{what} was not found.");

    public static Error NotConfigured(string message) => new(ErrorKind.NotConfigured, message);

    public static Error Offline(string feature) =>
        new(ErrorKind.Offline, $"{feature} requires an internet connection. You appear to be offline.");

    public static Error Cancelled() => new(ErrorKind.Cancelled, "The operation was cancelled.");

    public static Error FromException(Exception ex) => ex switch
    {
        ExternalServiceException ese => new Error(ese.Kind, ese.UserMessage, ese.ToString()),
        OperationCanceledException => Cancelled(),
        ArgumentException ae => new Error(ErrorKind.Validation, ae.Message),
        _ => new Error(ErrorKind.Unexpected, "Something went wrong. Details were written to the log.", ex.ToString()),
    };
}

public class Result
{
    protected Result(bool isSuccess, Error? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error? Error { get; }

    public static Result Success() => new(true, null);

    public static Result Failure(Error error) => new(false, error);

    public static Result<T> Success<T>(T value) => Result<T>.Success(value);

    public static Result<T> Failure<T>(Error error) => Result<T>.Failure(error);

    public static implicit operator Result(Error error) => Failure(error);
}

public sealed class Result<T> : Result
{
    private Result(bool isSuccess, T? value, Error? error)
        : base(isSuccess, error)
    {
        Value = value;
    }

    public T? Value { get; }

    [MemberNotNullWhen(true, nameof(Value))]
    public bool HasValue => IsSuccess && Value is not null;

    public static Result<T> Success(T value) => new(true, value, null);

    public static new Result<T> Failure(Error error) => new(false, default, error);

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);
}

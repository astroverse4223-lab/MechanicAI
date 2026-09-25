using MechanicAI.Application.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace MechanicAI.Server.Http;

/// <summary>Maps application <see cref="Result"/>/<see cref="Error"/> values onto HTTP responses (RFC 9457 problem details).</summary>
public static class ResultMapping
{
    public const int ClientClosedRequest = 499;

    public static int StatusCodeFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorKind.RateLimited => StatusCodes.Status429TooManyRequests,
        ErrorKind.NotConfigured or ErrorKind.Unavailable or ErrorKind.Offline => StatusCodes.Status503ServiceUnavailable,
        ErrorKind.InvalidResponse => StatusCodes.Status502BadGateway,
        ErrorKind.Timeout => StatusCodes.Status504GatewayTimeout,
        ErrorKind.Cancelled => ClientClosedRequest,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static string TitleFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "Invalid request",
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not found",
        StatusCodes.Status409Conflict => "Conflict",
        StatusCodes.Status413PayloadTooLarge => "Payload too large",
        StatusCodes.Status429TooManyRequests => "Too many requests",
        ClientClosedRequest => "Client closed request",
        StatusCodes.Status502BadGateway => "Upstream service error",
        StatusCodes.Status503ServiceUnavailable => "Service unavailable",
        StatusCodes.Status504GatewayTimeout => "Upstream timeout",
        _ => "Unexpected error",
    };

    /// <summary>
    /// Problem response for an error. <see cref="Error.Message"/> is user-safe and is returned;
    /// <see cref="Error.Detail"/> (stack traces, upstream payloads) is logged, never returned.
    /// </summary>
    public static ProblemHttpResult ToProblem(this Error error, ILogger? logger = null)
    {
        var status = StatusCodeFor(error.Kind);
        if (status >= 500 && error.Detail is not null) logger?.LogError("Request failed ({Kind}): {Detail}", error.Kind, error.Detail);
        var message = error.Kind == ErrorKind.Unexpected ? "An unexpected error occurred." : error.Message;
        return TypedResults.Problem(new ProblemDetails
        {
            Status = status,
            Title = TitleFor(status),
            Detail = message,
            Extensions = { ["errorKind"] = error.Kind.ToString() },
        });
    }

    public static IResult ToHttp(this Result result) =>
        result.IsSuccess ? TypedResults.NoContent() : result.Error!.ToProblem();

    public static IResult ToHttp(this Result result, Func<IResult> onSuccess) =>
        result.IsSuccess ? onSuccess() : result.Error!.ToProblem();

    public static IResult ToHttp<T>(this Result<T> result, Func<T, IResult>? onSuccess = null) =>
        result.IsSuccess ? onSuccess?.Invoke(result.Value!) ?? TypedResults.Ok(result.Value) : result.Error!.ToProblem();

    public static IResult NotFound(string what) => Error.NotFound(what).ToProblem();

    public static IResult OkOrNotFound<T>(T? value, string what) where T : class =>
        value is null ? NotFound(what) : TypedResults.Ok(value);
}

/// <summary>
/// Last-chance handler: unhandled exceptions become problem details without internal details.
/// Aborted requests map to 499, oversized bodies to 413.
/// </summary>
public sealed class ServerExceptionHandler(IProblemDetailsService problemDetails, ILogger<ServerExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        int status;
        string detail;
        switch (exception)
        {
            case OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested:
                status = ResultMapping.ClientClosedRequest;
                detail = "The request was cancelled.";
                break;
            case BadHttpRequestException bad:
                status = bad.StatusCode;
                detail = bad.StatusCode == StatusCodes.Status413PayloadTooLarge ? "The request body is too large." : "The request could not be read.";
                break;
            case ExternalServiceException ese:
                status = ResultMapping.StatusCodeFor(ese.Kind);
                detail = ese.UserMessage;
                logger.LogWarning(exception, "External service failure");
                break;
            default:
                status = StatusCodes.Status500InternalServerError;
                detail = "An unexpected error occurred.";
                logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
                break;
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails { Status = status, Title = ResultMapping.TitleFor(status), Detail = detail },
        });
    }
}

namespace MechanicAI.Application.Common;

/// <summary>
/// Raised by infrastructure adapters when an external service call fails. Carries a
/// categorized <see cref="ErrorKind"/> and a message that is safe to show to the user
/// (it never contains credentials or raw response bodies).
/// </summary>
public sealed class ExternalServiceException : Exception
{
    public ExternalServiceException(string service, ErrorKind kind, string userMessage, Exception? inner = null)
        : base($"{service}: {userMessage}", inner)
    {
        Service = service;
        Kind = kind;
        UserMessage = userMessage;
    }

    public ExternalServiceException()
        : this("External service", ErrorKind.Unexpected, "The external service failed.")
    {
    }

    public ExternalServiceException(string message)
        : this("External service", ErrorKind.Unexpected, message)
    {
    }

    public ExternalServiceException(string message, Exception inner)
        : this("External service", ErrorKind.Unexpected, message, inner)
    {
    }

    public string Service { get; }

    public ErrorKind Kind { get; }

    public string UserMessage { get; }

    /// <summary>Seconds the caller should wait before retrying (from Retry-After), when known.</summary>
    public int? RetryAfterSeconds { get; init; }
}

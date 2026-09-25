using System.Net;
using System.Text.Json;
using MechanicAI.Application.Common;

namespace MechanicAI.Infrastructure.Http;

/// <summary>
/// Sends HTTP requests and converts every failure mode (timeout, unreachable service,
/// rate limiting, authentication failure, server errors, malformed payloads) into an
/// <see cref="ExternalServiceException"/> with a message that is safe to show the user.
/// Response bodies and credentials are never included in messages.
/// </summary>
internal static class HttpCall
{
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        string service,
        CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, completion, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ExternalServiceException(service, ErrorKind.Timeout, $"{service} did not respond in time. Try again in a moment.", ex);
        }
        catch (Polly.Timeout.TimeoutRejectedException ex)
        {
            throw new ExternalServiceException(service, ErrorKind.Timeout, $"{service} did not respond in time. Try again in a moment.", ex);
        }
        catch (Polly.CircuitBreaker.BrokenCircuitException ex)
        {
            throw new ExternalServiceException(service, ErrorKind.Unavailable, $"{service} is failing repeatedly; requests are paused briefly.", ex);
        }
        catch (Polly.RateLimiting.RateLimiterRejectedException ex)
        {
            throw new ExternalServiceException(service, ErrorKind.RateLimited, $"Too many requests to {service}. Wait a moment and try again.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ExternalServiceException(service, ErrorKind.Unavailable,
                $"Could not reach {service}. Check your internet connection or the service address.", ex);
        }

        if (response.IsSuccessStatusCode) return response;

        var status = response.StatusCode;
        int? retryAfter = response.Headers.RetryAfter?.Delta is { } delta ? (int)delta.TotalSeconds : null;
        response.Dispose();
        throw status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ExternalServiceException(service, ErrorKind.Unauthorized,
                $"{service} rejected the credentials. Check the API key in Settings → Security."),
            HttpStatusCode.TooManyRequests => new ExternalServiceException(service, ErrorKind.RateLimited,
                retryAfter is { } s ? $"{service} rate limit reached. Try again in {s} seconds." : $"{service} rate limit reached. Try again shortly.")
            {
                RetryAfterSeconds = retryAfter,
            },
            HttpStatusCode.NotFound => new ExternalServiceException(service, ErrorKind.NotFound, $"{service} has no data for this request."),
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => new ExternalServiceException(service, ErrorKind.Timeout,
                $"{service} timed out."),
            >= HttpStatusCode.InternalServerError => new ExternalServiceException(service, ErrorKind.Unavailable,
                $"{service} is having problems (HTTP {(int)status}). Try again later."),
            _ => new ExternalServiceException(service, ErrorKind.InvalidResponse, $"{service} returned an unexpected response (HTTP {(int)status})."),
        };
    }

    public static async Task<string> GetStringAsync(HttpClient client, string url, string service, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(client, request, service, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    public static T ParseJson<T>(string json, string service)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json.Lenient)
                   ?? throw new ExternalServiceException(service, ErrorKind.InvalidResponse, $"{service} returned an empty response.");
        }
        catch (JsonException ex)
        {
            throw new ExternalServiceException(service, ErrorKind.InvalidResponse, $"{service} returned malformed data.", ex);
        }
    }

    public static JsonDocument ParseDocument(string json, string service)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ExternalServiceException(service, ErrorKind.InvalidResponse, $"{service} returned malformed data.", ex);
        }
    }
}

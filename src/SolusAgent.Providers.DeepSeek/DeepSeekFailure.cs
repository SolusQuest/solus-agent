using System.Net.Http.Headers;
using SolusAgent.Runtime.Api.Providers;

namespace SolusAgent.Providers.DeepSeek;

internal static class DeepSeekFailure
{
    // Classify typed evidence before discarding every raw message, inner exception and endpoint.
    internal static ProviderFailureException Transport(Exception exception) => new(
        exception is HttpRequestException { StatusCode: null, HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded }
            ? new(ProviderRetryKind.Transient) : null);

    internal static ProviderRetry? Status(HttpResponseMessage response)
    {
        ProviderRetryKind? kind = (int)response.StatusCode switch
        {
            429 => ProviderRetryKind.Throttled,
            408 or 500 or 502 or 503 or 504 => ProviderRetryKind.Transient,
            _ => null,
        };
        return kind is { } classified ? new(classified, Hint(response)) : null;
    }

    private static TimeSpan? Hint(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values)) return null;
        var hints = values.Take(2).ToArray();
        if (hints.Length != 1 || hints[0].Length > 128 || !RetryConditionHeaderValue.TryParse(hints[0], out var parsed)) return null;
        // The receiver's clock is authoritative for interpretation; provider Date cannot enlarge the hint.
        var delay = parsed.Delta ?? (parsed.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null);
        return delay is { } bounded && bounded >= TimeSpan.Zero && bounded <= ProviderRetry.MaximumDelay ? bounded : null;
    }
}

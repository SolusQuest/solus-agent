using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Solus.Logging.Supabase;

/// <summary>Explicit operator configuration; never discovers or refreshes credentials.</summary>
public sealed class SupabaseTelemetryOptions
{
    internal Uri RpcEndpoint { get; }
    internal string PublishableKey { get; }
    internal string AccessToken { get; }

    /// <summary>Requires an HTTPS origin, sb_publishable_ API key and syntactically bounded access JWT.</summary>
    /// <remarks>The server verifies JWT identity/expiry. Supply a dedicated authenticated ingest user's token; never a service-role token.</remarks>
    public SupabaseTelemetryOptions(Uri endpoint, string publishableKey, string accessToken)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || endpoint.AbsolutePath != "/") throw new ArgumentException("An HTTPS project origin is required.");
        if (publishableKey is null || publishableKey.Length is < 16 or > 1024
            || !publishableKey.StartsWith("sb_publishable_", StringComparison.Ordinal)
            || publishableKey.Any(c => !Base64Url(c))) throw new ArgumentException("A bounded publishable API key is required.");
        if (accessToken is null || accessToken.Length is < 5 or > 8192 || accessToken.Split('.').Length != 3
            || accessToken.Split('.').Any(s => s.Length == 0 || s.Any(c => !Base64Url(c))))
            throw new ArgumentException("A bounded access JWT is required.");
        RpcEndpoint = new(endpoint, "rest/v1/rpc/ingest_telemetry"); PublishableKey = publishableKey; AccessToken = accessToken;
    }
    private static bool Base64Url(char value) => char.IsAsciiLetterOrDigit(value) || value is '-' or '_';
}

/// <summary>A fixed single-attempt Supabase Data API/RPC sink, with bounded responses and no automatic redirects or delivery retries.</summary>
public sealed class SupabaseTelemetrySink : ITelemetrySink, IDisposable
{
    private const int MaximumResponseBytes = 4096;
    private readonly SupabaseTelemetryOptions options;
    private readonly HttpClient client;
    /// <summary>Creates an owned transport that rejects redirects and ignores ambient proxy/cookie/credential state.</summary>
    public SupabaseTelemetrySink(SupabaseTelemetryOptions options) : this(options, CreateHandler()) { }
    internal SupabaseTelemetrySink(SupabaseTelemetryOptions options, HttpMessageHandler handler)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None, ActivityHeadersPropagator = null,
        MaxResponseHeadersLength = 16, ConnectTimeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>POSTs the entire admitted event to ingest_telemetry(event jsonb); a 30 second attempt cap also covers body reads.</summary>
    public async ValueTask<TelemetrySinkStatus> WriteAsync(TelemetryPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var token = linked.Token;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, options.RpcEndpoint)
            { Content = new StringContent("{\"event\":" + payload.Json + "}", Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessToken);
            request.Headers.Add("apikey", options.PublishableKey);
            request.Headers.Accept.Add(new("application/json"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 401 or 403) return TelemetrySinkStatus.AuthenticationFailed;
            if (status == 409) return TelemetrySinkStatus.IdentityConflict;
            if (status is 400 or 404) return TelemetrySinkStatus.SchemaRejected;
            if (status != 200 && status != 429 && status is not (>= 500 and <= 599)) return TelemetrySinkStatus.Rejected;
            var bytes = await ReadBoundedAsync(response.Content, token).ConfigureAwait(false);
            if (bytes is null) return status == 200
                ? TelemetrySinkStatus.ProtocolFailure : TelemetrySinkStatus.TransientFailure;
            if (status == 200)
            {
                if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)) return TelemetrySinkStatus.ProtocolFailure;
                try
                {
                    using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 4 });
                    if (document.RootElement.ValueKind != JsonValueKind.String) return TelemetrySinkStatus.ProtocolFailure;
                    return document.RootElement.GetString() switch
                    {
                        "stored" => TelemetrySinkStatus.Stored,
                        "duplicate" => TelemetrySinkStatus.Duplicate,
                        _ => TelemetrySinkStatus.ProtocolFailure,
                    };
                }
                catch (JsonException) { return TelemetrySinkStatus.ProtocolFailure; }
            }
            // Inspect only bounded machine codes. Raw messages/details are never returned.
            var code = ErrorCode(bytes);
            if (code is "42501" or "28000" or "28P01" || code?.StartsWith("PGRST3", StringComparison.Ordinal) == true)
                return TelemetrySinkStatus.AuthenticationFailed;
            if (code is "42P01" or "42883" or "42703" || code?.StartsWith("PGRST2", StringComparison.Ordinal) == true)
                return TelemetrySinkStatus.SchemaRejected;
            if (code == "23505") return TelemetrySinkStatus.IdentityConflict;
            return TelemetrySinkStatus.TransientFailure;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return TelemetrySinkStatus.TransientFailure; }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException)
        { return TelemetrySinkStatus.TransientFailure; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { return TelemetrySinkStatus.ProtocolFailure; }
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes) return null;
        using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[MaximumResponseBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), token).ConfigureAwait(false);
            if (count == 0) return buffer[..read];
            read += count;
        }
        return null;
    }
    private static string? ErrorCode(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 8 });
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
            {
                var text = code.GetString(); return text?.Length <= 16 ? text : null;
            }
        }
        catch (JsonException) { }
        return null;
    }
    /// <summary>Disposes the owned transport and connections.</summary>
    public void Dispose() => client.Dispose();
}

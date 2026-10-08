using System.Net;
using System.Net.Http.Headers;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Providers;

namespace SolusAgent.Providers.DeepSeek;

/// <summary>One guarded DeepSeek attempt with restricted per-turn replay, bounded wire processing and no automatic retry.</summary>
public sealed class DeepSeekProvider : ModelProvider, IDisposable
{
    private readonly DeepSeekOptions options;
    private readonly HttpMessageInvoker? injectedInvoker;
    private int disposed;
    /// <summary>Creates an adapter using its owned safe production handler policy.</summary>
    public DeepSeekProvider(DeepSeekOptions options) : base(new("deepseek", DeepSeekOptions.Model),
        ProviderCapabilities.ToolCalls | ProviderCapabilities.Continuation | ProviderCapabilities.UsageReporting)
        => this.options = options ?? throw new ArgumentNullException(nameof(options));
    /// <summary>Creates an adapter with an owned Host-supplied terminal handler. Arbitrary executable handlers are trusted capabilities, must not retry or disclose requests, and are not sandboxed.</summary>
    public DeepSeekProvider(DeepSeekOptions options, HttpMessageHandler handler) : this(options)
    {
        ArgumentNullException.ThrowIfNull(handler);
        // Middleware can introduce hidden sends. Known network handlers must retain our transport policy.
        if (handler is DelegatingHandler or HttpClientHandler) throw new ArgumentException("Unsupported handler policy.", nameof(handler));
        if (handler is SocketsHttpHandler socket && (socket.AllowAutoRedirect || socket.UseProxy || socket.UseCookies
            || socket.AutomaticDecompression != DecompressionMethods.None || socket.Credentials is not null
            || socket.PreAuthenticate || socket.ActivityHeadersPropagator is not null))
            throw new ArgumentException("Unsupported handler policy.", nameof(handler));
        injectedInvoker = new(new SafeHandler(handler));
    }
    /// <inheritdoc />
    protected override async ValueTask<ProviderResponse> ExchangeCoreAsync(ProviderRequest request, ProviderObservation observation, CancellationToken cancellationToken)
    {
        var sendStarted = false;
        try
        {
            if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(DeepSeekProvider));
            var body = DeepSeekRequestWriter.Write(request, options);
            using var message = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
            {
                Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new SingleSendContent(body),
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Credential);
            message.Headers.Accept.Add(new("application/json"));
            message.Headers.ExpectContinue = false; message.Headers.ConnectionClose = true;
            message.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
            using var deadline = new CancellationTokenSource(options.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            using var ownedInvoker = injectedInvoker is null ? new HttpMessageInvoker(new SafeHandler(CreateProductionHandler())) : null;
            cancellationToken.ThrowIfCancellationRequested();
            linked.Token.ThrowIfCancellationRequested();
            // Also verifies an externally sealed observation channel before entering the transport.
            observation.ObserveDispatch(DispatchExposure.Unknown);
            sendStarted = true;
            using var response = await (injectedInvoker ?? ownedInvoker!).SendAsync(message, linked.Token).ConfigureAwait(false);
            observation.ObserveDispatch(DispatchExposure.Dispatched);
            var bytes = await ReadBodyAsync(response.Content, options.MaximumResponseBodyBytes, linked.Token).ConfigureAwait(false);
            var candidate = DeepSeekResponseParser.Parse(bytes, response, request, observation);
            // Usage has already been captured if the complete body could be validated.
            cancellationToken.ThrowIfCancellationRequested(); linked.Token.ThrowIfCancellationRequested();
            return candidate;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!sendStarted) observation.ObserveDispatch(DispatchExposure.NotDispatched);
            throw new OperationCanceledException("The provider call was cancelled.", null, cancellationToken);
        }
        catch
        {
            if (!sendStarted) observation.ObserveDispatch(DispatchExposure.NotDispatched);
            throw;
        }
    }
    internal static SocketsHttpHandler CreateProductionHandler() => new()
    {
        ActivityHeadersPropagator = null, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None,
        Credentials = null, PreAuthenticate = false, UseCookies = false, UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(15), MaxResponseHeadersLength = 16,
        MaxResponseDrainSize = 0, ResponseDrainTimeout = TimeSpan.Zero,
    };
    private static async Task<byte[]> ReadBodyAsync(HttpContent content, int cap, CancellationToken token)
    {
        if (content.Headers.ContentLength is long length && length > cap) throw new ProviderContractException(ProviderError.LimitExceeded);
        using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(cap, 4096));
        var buffer = new byte[Math.Min(cap + 1, 4096)];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, cap - output.Length + 1)), token).ConfigureAwait(false);
            if (count == 0) break;
            if (count > cap - output.Length) throw new ProviderContractException(ProviderError.LimitExceeded);
            output.Write(buffer, 0, count);
        }
        if (content.Headers.ContentLength is long expected && output.Length != expected) throw new ProviderContractException(ProviderError.InvalidResponse);
        return output.ToArray();
    }
    /// <summary>Closes owned resources; disposal does not assert remote stop or rollback of in-flight work.</summary>
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) injectedInvoker?.Dispose(); }
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(DeepSeekProvider);

    private sealed class SafeHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            try { return await base.SendAsync(request, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { throw new OperationCanceledException("The provider transport was cancelled.", null, token); }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            { throw new SafeTransportException(); }
        }
    }
    private sealed class SafeTransportException() : Exception("The provider transport failed.");
    private sealed class SingleSendContent(byte[] body) : HttpContent
    {
        private int writes;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteAsync(stream, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => WriteAsync(stream, token);
        private async Task WriteAsync(Stream stream, CancellationToken token)
        {
            if (Interlocked.Increment(ref writes) != 1) throw new SafeTransportException();
            await stream.WriteAsync(body, token).ConfigureAwait(false);
        }
        protected override bool TryComputeLength(out long length) { length = body.Length; return true; }
    }
}

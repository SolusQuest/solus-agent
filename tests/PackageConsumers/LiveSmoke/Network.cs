using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SolusAgent.Providers.DeepSeek;

namespace PackageConsumers.LiveSmoke;

internal static class Network
{
    internal static Send Send(string credential) => async (body, _, token) =>
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
        attempt.CancelAfter(TimeSpan.FromSeconds(120)); token = attempt.Token;
        using var socket = new SocketsHttpHandler
        {
            ActivityHeadersPropagator = null, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None,
            Credentials = null, PreAuthenticate = false, UseCookies = false, UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(15), MaxResponseHeadersLength = 16, MaxResponseDrainSize = 0,
            ResponseDrainTimeout = TimeSpan.Zero,
        };
        using var transport = new HttpMessageInvoker(socket, disposeHandler: false);
        using var message = new HttpRequestMessage(HttpMethod.Post, DeepSeekOptions.OfficialEndpoint)
        {
            Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new OneShotContent(Encoding.UTF8.GetBytes(body)),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        message.Headers.Accept.Add(new("application/json"));
        message.Headers.ExpectContinue = false; message.Headers.ConnectionClose = true;
        message.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        using var response = await transport.SendAsync(message, token);
        Check.That(response.Content.Headers.ContentEncoding.Count == 0
            && response.Content.Headers.ContentType?.MediaType == "application/json", Failure.UnexpectedReply);
        Check.That(response.Content.Headers.ContentLength is null or <= 65536, Failure.Capacity);
        using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, 65537 - output.Length)), token)) != 0)
        {
            Check.That(output.Length + count <= 65536, Failure.Capacity); output.Write(buffer, 0, count);
        }
        Check.That(response.Content.Headers.ContentLength is not { } length || length == output.Length, Failure.UnexpectedReply);
        return new((int)response.StatusCode, new UTF8Encoding(false, true).GetString(output.ToArray()));
    };

    // Mirrors the adapter's narrow owned-transport precedent: content cannot be replayed by HTTP middleware.
    private sealed class OneShotContent(byte[] bytes) : HttpContent
    {
        private int writes;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteAsync(stream, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => WriteAsync(stream, token);
        private async Task WriteAsync(Stream stream, CancellationToken token)
        {
            Check.That(Interlocked.Increment(ref writes) == 1, Failure.Transport);
            await stream.WriteAsync(bytes, token);
        }
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
    }
}

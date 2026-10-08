using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using SolusAgent.Providers.DeepSeek;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.Adapter;

[CollectionDefinition("DeepSeek standard HTTP instrumentation", DisableParallelization = true)]
public sealed class HttpInstrumentationCollection;

[Collection("DeepSeek standard HTTP instrumentation")]
public sealed class TransportDiagnosticsTests
{
    [Theory]
    [InlineData("http")]
    [InlineData("unrelated-cancel")]
    [InlineData("none-cancel")]
    [InlineData("matching-cancel")]
    public async Task SanitizationOccursInsideInvokerBeforeEnabledStandardTelemetry(string mode)
    {
        using var telemetry = new StandardHttpTelemetry();
        using var caller = new CancellationTokenSource();
        using var handler = new FakeHandler((_, token) =>
        {
            if (mode == "http") throw new HttpRequestException("synthetic-exception-canary", new IOException(AdapterFixture.Credential));
            caller.Cancel();
            throw new OperationCanceledException("synthetic-exception-canary", new IOException(AdapterFixture.Credential),
                mode == "matching-cancel" ? token : mode == "unrelated-cancel" ? new CancellationToken(true) : CancellationToken.None);
        });
        using var provider = AdapterFixture.Provider(handler); var result = await provider.ExchangeAsync(AdapterFixture.Request(), caller.Token);
        Assert.Equal(mode == "matching-cancel" ? ProviderOutcome.Cancelled : ProviderOutcome.Failed, result.Outcome); Assert.Equal(1, handler.Sends);
        Assert.Contains("RequestFailed", telemetry.Text);
        foreach (var secret in new[] { "synthetic-exception-canary", AdapterFixture.Credential, AdapterFixture.Replay }) Assert.DoesNotContain(secret, telemetry.Text);
    }
    [Theory]
    [InlineData("success", ProviderOutcome.Succeeded)]
    [InlineData("eof", ProviderOutcome.Failed)]
    [InlineData("error", ProviderOutcome.Failed)]
    [InlineData("redirect", ProviderOutcome.Failed)]
    public async Task ActualProductionHandlerPolicyMakesOnePhysicalRequestAndPublishesNoCredentialOrBody(string mode, ProviderOutcome expected)
    {
        using var telemetry = new StandardHttpTelemetry();
        using var rsa = RSA.Create(2048);
        var certRequest = new CertificateRequest("CN=api.deepseek.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("api.deepseek.com"); certRequest.CertificateExtensions.Add(names.Build());
        using var generated = certRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        // Schannel cannot serve an ephemeral CNG key. Reload in memory; never install a trust certificate.
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.DefaultKeySet);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connections = 0; var physicalRequests = 0; var observed = new ConcurrentQueue<string>();
        var server = Task.Run(async () =>
        {
            while (!deadline.IsCancellationRequested)
            {
                TcpClient peer;
                try { peer = await listener.AcceptTcpClientAsync(deadline.Token); }
                catch (OperationCanceledException) { break; }
                using (peer)
                using (var ssl = new SslStream(peer.GetStream()))
                {
                    Interlocked.Increment(ref connections);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, deadline.Token);
                    var header = new List<byte>(); var one = new byte[1];
                    while (header.Count < 16384)
                    {
                        if (await ssl.ReadAsync(one, deadline.Token) == 0) break;
                        header.Add(one[0]);
                        if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                    }
                    var text = Encoding.ASCII.GetString(header.ToArray());
                    var length = int.Parse(text.Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                    var body = new byte[length]; await ssl.ReadExactlyAsync(body, deadline.Token);
                    Interlocked.Increment(ref physicalRequests); observed.Enqueue(text); observed.Enqueue(Encoding.UTF8.GetString(body));
                    if (mode != "eof")
                    {
                        var payload = Encoding.UTF8.GetBytes(AdapterFixture.Response("synthetic-body-canary"));
                        var status = mode == "success" ? "200 OK" : mode == "error" ? "500 Synthetic" : "307 Synthetic";
                        var location = mode == "redirect" ? "Location: https://synthetic-redirect.invalid/\r\n" : "";
                        await ssl.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{location}Content-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"), deadline.Token);
                        await ssl.WriteAsync(payload, deadline.Token);
                    }
                }
            }
        });
        var handler = DeepSeekProvider.CreateProductionHandler();
        Assert.Null(handler.ActivityHeadersPropagator); Assert.False(handler.AllowAutoRedirect); Assert.False(handler.UseProxy); Assert.False(handler.UseCookies);
        handler.ConnectCallback = async (_, token) =>
        {
            var tcp = new TcpClient();
            try { await tcp.ConnectAsync((IPEndPoint)listener.LocalEndpoint, token); return tcp.GetStream(); }
            catch { tcp.Dispose(); throw; }
        };
        // Only this test capability routes to loopback and admits its exact synthetic certificate.
        handler.SslOptions.RemoteCertificateValidationCallback = (_, actual, _, _) => actual is not null && actual.GetRawCertData().AsSpan().SequenceEqual(certificate.RawData);
        using var provider = AdapterFixture.Provider(handler);
        ProviderExchangeResult result;
        try { result = await provider.ExchangeAsync(AdapterFixture.Request([ProviderInput.Data("synthetic-request-body-canary")]), deadline.Token); }
        finally { deadline.Cancel(); await server.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(expected, result.Outcome); Assert.Equal(1, connections); Assert.Equal(1, physicalRequests);
        Assert.Contains(observed, s => s.Contains("Authorization: Bearer " + AdapterFixture.Credential, StringComparison.Ordinal));
        Assert.Contains(observed, s => s.Contains("synthetic-request-body-canary", StringComparison.Ordinal));
        Assert.Contains("RequestStart", telemetry.Text);
        foreach (var secret in new[] { AdapterFixture.Credential, AdapterFixture.Replay, "synthetic-request-body-canary", "synthetic-body-canary" }) Assert.DoesNotContain(secret, telemetry.Text);
    }

    private sealed class StandardHttpTelemetry : EventListener, IObserver<DiagnosticListener>
    {
        private readonly ConcurrentQueue<string> values = new();
        private readonly List<IDisposable> subscriptions = [];
        private readonly IDisposable allListeners;
        private readonly ActivityListener activity;
        internal StandardHttpTelemetry()
        {
            allListeners = DiagnosticListener.AllListeners.Subscribe(this);
            activity = new()
            {
                ShouldListenTo = source => source.Name.StartsWith("System.Net", StringComparison.Ordinal),
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = eventActivity =>
                { values.Enqueue(eventActivity.DisplayName); foreach (var tag in eventActivity.TagObjects) values.Enqueue(tag.Key + "=" + tag.Value); },
            };
            ActivitySource.AddActivityListener(activity);
            foreach (var source in EventSource.GetSources()) Enable(source);
        }
        internal string Text => string.Join('\n', values);
        protected override void OnEventSourceCreated(EventSource source) { if (values is not null) Enable(source); }
        private void Enable(EventSource source) { if (source.Name.StartsWith("System.Net.", StringComparison.Ordinal)) EnableEvents(source, EventLevel.Verbose, EventKeywords.All); }
        protected override void OnEventWritten(EventWrittenEventArgs data)
        { values.Enqueue(data.EventName ?? ""); if (data.Payload is not null) foreach (var value in data.Payload) values.Enqueue(value?.ToString() ?? ""); }
        public void OnNext(DiagnosticListener listener)
        { if (listener.Name.Contains("Http", StringComparison.OrdinalIgnoreCase)) subscriptions.Add(listener.Subscribe(new EventObserver(values))); }
        public void OnError(Exception error) { } public void OnCompleted() { }
        public override void Dispose() { foreach (var subscription in subscriptions) subscription.Dispose(); allListeners.Dispose(); activity.Dispose(); base.Dispose(); }
        private sealed class EventObserver(ConcurrentQueue<string> values) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnNext(KeyValuePair<string, object?> entry)
            {
                values.Enqueue(entry.Key);
                if (entry.Value is null) return;
                foreach (var property in entry.Value.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0))
                    values.Enqueue(property.GetValue(entry.Value)?.ToString() ?? "");
            }
            public void OnError(Exception error) { } public void OnCompleted() { }
        }
    }
}

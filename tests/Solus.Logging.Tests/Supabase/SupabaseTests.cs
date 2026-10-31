using System.Net;
using System.Text;
using System.Text.Json;
using Solus.Logging.Supabase;
using Solus.Logging.Tests.Contracts;
using Solus.Telemetry.Contracts;
using Xunit;

namespace Solus.Logging.Tests.Supabase;

public sealed class SupabaseTests
{
    private const string Key = "sb_publishable_synthetic_key_canary";
    private const string Jwt = "synthetic.access.jwt_canary";
    private static SupabaseTelemetryOptions Options() => new(new Uri("https://synthetic.supabase.co"), Key, Jwt);
    private static TelemetryDeliveryOptions Fast() => new(3, TimeSpan.FromSeconds(5), TimeSpan.Zero);
    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return send(request, cancellationToken); }
    }
    private static HttpResponseMessage Response(int status = 200, string body = "\"stored\"", string type = "application/json") =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, type) };
    private static string Root
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Solus.Logging.slnx"))) return dir.FullName;
            throw new InvalidOperationException("Repository not found.");
        }
    }
    private static TelemetryRecord Fixture() => SyntheticTelemetry.Consume(File.ReadAllText(Path.Combine(Root, "tests/Solus.Logging.Tests/Supabase/telemetry-v1.json")));

    [Fact]
    public async Task FixedRpcRequestUsesExplicitHeadersAndCompleteAdmittedJsonWithoutCoercion()
    {
        var record = Fixture();
        var wire = SyntheticTelemetry.Wire(record);
        var handler = new Handler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://synthetic.supabase.co/rest/v1/rpc/ingest_telemetry", request.RequestUri!.AbsoluteUri);
            Assert.Equal(Key, Assert.Single(request.Headers.GetValues("apikey")));
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal(Jwt, request.Headers.Authorization.Parameter);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
            var actual = await request.Content.ReadAsStringAsync(token);
            Assert.Equal("{\"event\":" + wire + "}", actual);
            using var json = JsonDocument.Parse(actual);
            var root = json.RootElement;
            Assert.Equal("event", Assert.Single(root.EnumerateObject()).Name);
            var run = root.GetProperty("event").GetProperty("usage").GetProperty("runs")[0];
            Assert.Equal("9223372036854775807", run.GetProperty("inputTokens").GetProperty("value").GetString());
            Assert.Equal(JsonValueKind.Null, run.GetProperty("outputTokens").GetProperty("value").ValueKind);
            Assert.Equal("overflow", run.GetProperty("providerCounters")[0].GetProperty("observation").GetProperty("coverage").GetString());
            Assert.Equal("你好😀", root.GetProperty("event").GetProperty("extensions").GetProperty("scribe").GetProperty("nested")[3].GetProperty("text").GetString());
            return Response();
        });
        using var sink = new SupabaseTelemetrySink(Options(), handler);
        var result = await new TelemetryDelivery(sink, Fast()).DeliverAsync(record);
        Assert.Equal(TelemetryDeliveryStatus.Stored, result.Status); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task JsonMediaTypeComparisonIsCaseInsensitive()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(type: "Application/JSON")));
        using var sink = new SupabaseTelemetrySink(Options(), handler);
        Assert.Equal(TelemetryDeliveryStatus.Stored, (await new TelemetryDelivery(sink, Fast()).DeliverAsync(Fixture())).Status);
    }

    [Fact]
    public async Task LostSuccessResponseRetriesTheIdenticalBytesAndReportsDuplicate()
    {
        var bodies = new List<string>();
        var handler = new Handler(async (request, token) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync(token));
            if (bodies.Count == 1) throw new HttpRequestException("synthetic-server-secret-canary");
            return Response(body: "\"duplicate\"");
        });
        using var sink = new SupabaseTelemetrySink(Options(), handler);
        var result = await new TelemetryDelivery(sink, Fast()).DeliverAsync(Fixture());
        Assert.Equal(TelemetryDeliveryStatus.Duplicate, result.Status); Assert.Equal(2, result.Attempts);
        Assert.Equal(2, bodies.Count); Assert.Equal(bodies[0], bodies[1]);
        Assert.DoesNotContain("canary", result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task TransientHttpFailuresStopAtFiniteAttemptBudget(int code)
    {
        var handler = new Handler((_, _) =>
        {
            var response = Response(code, "synthetic-server-secret-canary");
            response.Headers.TryAddWithoutValidation("Retry-After", "999999999999");
            return Task.FromResult(response);
        });
        using var sink = new SupabaseTelemetrySink(Options(), handler);
        var result = await new TelemetryDelivery(sink, Fast()).DeliverAsync(Fixture());
        Assert.Equal(TelemetryDeliveryDiagnostic.AttemptsExhausted, result.Diagnostic); Assert.Equal(3, handler.Calls);
        Assert.DoesNotContain("canary", result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(400, TelemetryDeliveryDiagnostic.SchemaRejected)]
    [InlineData(401, TelemetryDeliveryDiagnostic.AuthenticationFailed)]
    [InlineData(403, TelemetryDeliveryDiagnostic.AuthenticationFailed)]
    [InlineData(404, TelemetryDeliveryDiagnostic.SchemaRejected)]
    [InlineData(409, TelemetryDeliveryDiagnostic.IdentityConflict)]
    [InlineData(302, TelemetryDeliveryDiagnostic.Rejected)]
    [InlineData(307, TelemetryDeliveryDiagnostic.Rejected)]
    public async Task PermanentStatusAndRedirectsDoNotRetryOrReturnServerBody(int code, TelemetryDeliveryDiagnostic diagnostic)
    {
        var handler = new Handler((_, _) =>
        {
            var response = Response(code, Key + Jwt + "synthetic-payload-canary");
            response.Headers.Location = new Uri("https://untrusted.invalid/collect");
            return Task.FromResult(response);
        });
        using var sink = new SupabaseTelemetrySink(Options(), handler);
        var result = await new TelemetryDelivery(sink, Fast()).DeliverAsync(Fixture());
        Assert.Equal(diagnostic, result.Diagnostic); Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("canary", result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PGRST301", TelemetryDeliveryDiagnostic.AuthenticationFailed)]
    [InlineData("42501", TelemetryDeliveryDiagnostic.AuthenticationFailed)]
    [InlineData("PGRST202", TelemetryDeliveryDiagnostic.SchemaRejected)]
    [InlineData("42P01", TelemetryDeliveryDiagnostic.SchemaRejected)]
    [InlineData("23505", TelemetryDeliveryDiagnostic.IdentityConflict)]
    public async Task StructuredPermanentCodesOverrideBroadTransientStatus(string code, TelemetryDeliveryDiagnostic diagnostic)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(503, "{\"code\":\"" + code + "\",\"message\":\"synthetic-secret-canary\"}")));
        using var sink = new SupabaseTelemetrySink(Options(), handler);
        var result = await new TelemetryDelivery(sink, Fast()).DeliverAsync(Fixture());
        Assert.Equal(diagnostic, result.Diagnostic); Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("canary", result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[\"stored\"]")]
    [InlineData("\"unknown\"")]
    [InlineData("\"stored\" trailing")]
    [InlineData("\"stored")]
    public async Task InvalidSuccessNeverBecomesStoredOrRetries(string body)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(body: body)));
        using var sink = new SupabaseTelemetrySink(Options(), handler);
        var result = await new TelemetryDelivery(sink, Fast()).DeliverAsync(Fixture());
        Assert.Equal(TelemetryDeliveryDiagnostic.ProtocolFailure, result.Diagnostic); Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResponseSizeAndMediaTypeAreBounded(bool oversized)
    {
        var handler = new Handler((_, _) => Task.FromResult(oversized ? Response(body: new string('a', 4097)) : Response(type: "text/plain")));
        using var sink = new SupabaseTelemetrySink(Options(), handler);
        Assert.Equal(TelemetryDeliveryDiagnostic.ProtocolFailure, (await new TelemetryDelivery(sink, Fast()).DeliverAsync(Fixture())).Diagnostic);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes));
    }

    [Fact]
    public async Task UnknownContentLengthStillEnforcesTheActualStreamingByteBound()
    {
        var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(Encoding.UTF8.GetBytes(new string(' ', 4090) + "\"stored\"")) };
            response.Content.Headers.ContentType = new("application/json");
            return Task.FromResult(response);
        });
        using var sink = new SupabaseTelemetrySink(Options(), handler);
        var result = await new TelemetryDelivery(sink, Fast()).DeliverAsync(Fixture());
        Assert.Equal(TelemetryDeliveryDiagnostic.ProtocolFailure, result.Diagnostic); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CancellationDuringSendStopsBeforeAnyRetry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (_, token) =>
        {
            entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Response();
        });
        using var cancel = new CancellationTokenSource();
        using var sink = new SupabaseTelemetrySink(Options(), handler);
        var pending = new TelemetryDelivery(sink, Fast()).DeliverAsync(Fixture(), cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
        Assert.Equal(TelemetryDeliveryDiagnostic.Cancelled, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Diagnostic);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class HeldStream(Task gate) : Stream
    {
        public bool Disposed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await gate; cancellationToken.ThrowIfCancellationRequested(); return 0; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallerCutBoundsNoncooperativeResponseBodyAfterHeaders(bool cancel)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var headers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new HeldStream(gate.Task);
        var handler = new Handler((_, _) =>
        {
            headers.TrySetResult();
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentType = new("application/json"); return Task.FromResult(response);
        });
        using var source = new CancellationTokenSource();
        using var sink = new SupabaseTelemetrySink(Options(), handler);
        try
        {
            var resultTask = new TelemetryDelivery(sink, new(3, cancel ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(250), TimeSpan.Zero)).DeliverAsync(Fixture(), source.Token);
            await headers.Task.WaitAsync(TimeSpan.FromSeconds(5)); if (cancel) source.Cancel();
            var result = await resultTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(cancel ? TelemetryDeliveryDiagnostic.Cancelled : TelemetryDeliveryDiagnostic.DeadlineExceeded, result.Diagnostic);
            Assert.Equal(1, handler.Calls);
        }
        finally { gate.TrySetResult(); }
        // A cut does not claim rollback; resource disposal happens when the held body cooperates.
        for (var i = 0; i < 100 && !stream.Disposed; i++) await Task.Delay(10);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public void PublicTransportDisablesCredentialRedirectCookieProxyAndAmbientTraceState()
    {
        using var handler = SupabaseTelemetrySink.CreateHandler();
        Assert.False(handler.AllowAutoRedirect); Assert.False(handler.UseCookies); Assert.False(handler.UseProxy);
        Assert.Null(handler.Credentials); Assert.Null(handler.ActivityHeadersPropagator);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.DoesNotContain(typeof(SupabaseTelemetrySink).GetConstructors(), c => c.GetParameters().Any(p => p.ParameterType == typeof(HttpMessageHandler)));
    }

    [Theory]
    [InlineData("http://synthetic.supabase.co")]
    [InlineData("https://user:password@synthetic.supabase.co")]
    [InlineData("https://synthetic.supabase.co/other")]
    [InlineData("https://synthetic.supabase.co/?key=secret")]
    [InlineData("https://synthetic.supabase.co/#fragment")]
    public void EndpointValidationRejectsUnsafeOrDynamicTargets(string uri) =>
        Assert.Throws<ArgumentException>(() => new SupabaseTelemetryOptions(new Uri(uri), Key, Jwt));

    [Theory]
    [InlineData("sb_secret_synthetic_secret", Jwt)]
    [InlineData("sb_publishable_synthetic\r\nInjected", Jwt)]
    [InlineData(Key, "sb_publishable_is_not_an_access_jwt")]
    [InlineData(Key, "synthetic.access.jwt\ncanary")]
    [InlineData(Key, "a..b")]
    public void InvalidCredentialsRejectWithFixedMessages(string key, string jwt)
    {
        var error = Assert.Throws<ArgumentException>(() => new SupabaseTelemetryOptions(new Uri("https://synthetic.supabase.co"), key, jwt));
        Assert.DoesNotContain("canary", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaximumAdmittedRecordHasCompleteRpcEnvelopeWithoutTruncation()
    {
        var runs = new List<TelemetryRunSummary>();
        TelemetryRecord source; string json;
        do
        {
            runs.Add(SyntheticTelemetry.Run(rich: true));
            source = SyntheticTelemetry.Produce(new(TelemetryInventoryCoverage.Complete, runs), SyntheticTelemetry.Json("{\"scribe\":{\"padding\":\"\"}}"));
            json = SyntheticTelemetry.Wire(source);
        } while (Encoding.UTF8.GetByteCount(json) < TelemetryLimits.MaximumRecordBytes - TelemetryLimits.MaximumExtensionBytes + 1000);
        var padding = TelemetryLimits.MaximumRecordBytes - Encoding.UTF8.GetByteCount(json);
        source = SyntheticTelemetry.Produce(source.Usage, SyntheticTelemetry.Json("{\"scribe\":{\"padding\":\"" + new string('a', padding) + "\"}}"));
        var wire = SyntheticTelemetry.Wire(source);
        Assert.Equal(TelemetryLimits.MaximumRecordBytes, Encoding.UTF8.GetByteCount(wire));
        var handler = new Handler(async (request, token) =>
        {
            var body = await request.Content!.ReadAsStringAsync(token);
            Assert.Equal("{\"event\":" + wire + "}", body);
            Assert.Equal(TelemetryLimits.MaximumRecordBytes + 10, Encoding.UTF8.GetByteCount(body)); return Response();
        });
        using var sink = new SupabaseTelemetrySink(Options(), handler);
        Assert.Equal(TelemetryDeliveryStatus.Stored, (await new TelemetryDelivery(sink, Fast()).DeliverAsync(source)).Status);
    }
}

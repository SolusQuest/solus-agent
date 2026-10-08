using System.Net;
using System.Text;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.Adapter;

public sealed class TransportBoundaryTests
{
    [Fact]
    public async Task PreCancellationAndDisposedProviderAreNotDispatched()
    {
        var handler = FakeHandler.Reply(AdapterFixture.Response()); var provider = AdapterFixture.Provider(handler);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var result = await provider.ExchangeAsync(AdapterFixture.Request(), cancel.Token);
        Assert.Equal(ProviderOutcome.Cancelled, result.Outcome); Assert.Equal(DispatchExposure.NotDispatched, result.Observation.Exposure);
        provider.Dispose(); Assert.True(handler.Disposed);
        var disposed = await provider.ExchangeAsync(AdapterFixture.Request()); Assert.Equal(ProviderOutcome.Failed, disposed.Outcome);
        Assert.Equal(DispatchExposure.NotDispatched, disposed.Observation.Exposure); Assert.Equal(0, handler.Sends);
    }
    [Theory]
    [InlineData("throw")]
    [InlineData("unrelated-cancel")]
    [InlineData("timeout")]
    public async Task FailedSendHasUnknownExposureAndNeverRetries(string mode)
    {
        using var handler = new FakeHandler(async (_, token) =>
        {
            if (mode == "unrelated-cancel") throw new OperationCanceledException("synthetic-exception-canary", new CancellationToken(true));
            if (mode == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new HttpRequestException("synthetic-exception-canary");
        });
        using var provider = AdapterFixture.Provider(handler, timeout: mode == "timeout" ? TimeSpan.FromMilliseconds(30) : null);
        var result = await provider.ExchangeAsync(AdapterFixture.Request()); Assert.Equal(ProviderOutcome.Failed, result.Outcome);
        Assert.Equal(ProviderError.ProviderFailed, result.Error); Assert.Equal(DispatchExposure.Unknown, result.Observation.Exposure);
        Assert.Equal(UsageCompleteness.Unavailable, result.Observation.Usage.Completeness); Assert.Null(result.Response); Assert.Equal(1, handler.Sends);
    }
    [Fact]
    public async Task CallerCancellationWhileSendingKeepsUnknownExposureAndSafeCancellationIdentity()
    {
        using var cancellation = new CancellationTokenSource(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FakeHandler(async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return AdapterFixture.Http(""); });
        using var provider = AdapterFixture.Provider(handler);
        var pending = provider.ExchangeAsync(AdapterFixture.Request(), cancellation.Token).AsTask(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel(); var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ProviderOutcome.Cancelled, result.Outcome); Assert.Equal(ProviderError.Cancelled, result.Error);
        Assert.Equal(DispatchExposure.Unknown, result.Observation.Exposure); Assert.Equal(1, handler.Sends); Assert.Null(result.Response);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BodyReadCancellationOrFailureKnowsDispatchButNotUnvalidatedUsage(bool cancel)
    {
        using var cancellation = new CancellationTokenSource(); var stream = new FaultStream(cancel ? cancellation : null);
        var response = AdapterFixture.Http(""); response.Content = new StreamContent(stream); response.Content.Headers.ContentType = new("application/json");
        using var handler = new FakeHandler((_, _) => Task.FromResult(response)); using var provider = AdapterFixture.Provider(handler);
        var result = await provider.ExchangeAsync(AdapterFixture.Request(), cancellation.Token);
        Assert.Equal(cancel ? ProviderOutcome.Cancelled : ProviderOutcome.Failed, result.Outcome);
        Assert.Equal(DispatchExposure.Dispatched, result.Observation.Exposure); Assert.Equal(UsageCompleteness.Unavailable, result.Observation.Usage.Completeness);
        Assert.True(stream.Disposed); Assert.Null(result.Response); Assert.Equal(1, handler.Sends);
    }
    [Fact]
    public async Task CancellationAfterCompleteBodyReadRetainsUsageBeforeAcceptanceCut()
    {
        using var cancellation = new CancellationTokenSource(); var stream = new CompletionStream(Encoding.UTF8.GetBytes(AdapterFixture.Response()), cancellation);
        var response = AdapterFixture.Http(""); response.Content = new StreamContent(stream); response.Content.Headers.ContentType = new("application/json");
        using var handler = new FakeHandler((_, _) => Task.FromResult(response)); using var provider = AdapterFixture.Provider(handler);
        var result = await provider.ExchangeAsync(AdapterFixture.Request(), cancellation.Token);
        Assert.Equal(ProviderOutcome.Cancelled, result.Outcome); Assert.Null(result.Response);
        Assert.Equal(10, result.Observation.Usage.InputTokens); Assert.Equal(5, result.Observation.Usage.OutputTokens); Assert.True(stream.Disposed);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeclaredAndUndeclaredBodyCapsStopBeforeAdmission(bool declared)
    {
        var body = Encoding.UTF8.GetBytes(AdapterFixture.Response());
        var stream = new CountedStream(body); var response = AdapterFixture.Http(""); response.Content = new StreamContent(stream);
        response.Content.Headers.ContentType = new("application/json"); if (declared) response.Content.Headers.ContentLength = body.Length;
        using var handler = new FakeHandler((_, _) => Task.FromResult(response)); using var provider = AdapterFixture.Provider(handler, responseCap: body.Length - 1);
        var result = await provider.ExchangeAsync(AdapterFixture.Request()); Assert.Equal(ProviderError.LimitExceeded, result.Error); Assert.Null(result.Response);
        Assert.Equal(UsageCompleteness.Unavailable, result.Observation.Usage.Completeness);
        Assert.Equal(declared ? 0 : body.Length, stream.BytesRead); Assert.True(stream.Disposed); Assert.Equal(1, handler.Sends);
    }
    [Fact]
    public async Task ExactBodyCapAcceptsAndLyingLengthOrMimeRejects()
    {
        var body = AdapterFixture.Response(); var bytes = Encoding.UTF8.GetBytes(body);
        using var exactHandler = FakeHandler.Reply(body); using var exact = AdapterFixture.Provider(exactHandler, responseCap: bytes.Length);
        Assert.Equal(ProviderOutcome.Succeeded, (await exact.ExchangeAsync(AdapterFixture.Request())).Outcome);
        foreach (var defect in new[] { "length", "mime", "encoding", "charset" })
        {
            using var handler = new FakeHandler((_, _) =>
            {
                var response = AdapterFixture.Http(body);
                if (defect == "length") response.Content.Headers.ContentLength = bytes.Length + 1;
                if (defect == "mime") response.Content.Headers.ContentType = new("text/html");
                if (defect == "encoding") response.Content.Headers.ContentEncoding.Add("gzip");
                if (defect == "charset") response.Content.Headers.ContentType!.CharSet = "utf-16";
                return Task.FromResult(response);
            });
            using var provider = AdapterFixture.Provider(handler); var result = await provider.ExchangeAsync(AdapterFixture.Request());
            Assert.Equal(ProviderOutcome.Rejected, result.Outcome); Assert.Null(result.Response); Assert.Equal(1, handler.Sends);
        }
    }
    [Fact]
    public async Task SameProviderConcurrentCallsHaveIndependentBodiesObservationsAndAssociations()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var handler = new FakeHandler(async (message, token) =>
        {
            if (Interlocked.Increment(ref calls) == 2) entered.SetResult(); await release.Task.WaitAsync(token);
            var body = await message.Content!.ReadAsStringAsync(token); var first = body.Contains("first-call", StringComparison.Ordinal);
            return AdapterFixture.Http(AdapterFixture.Response(first ? "first-answer" : "second-answer", usage: new { prompt_tokens = first ? 3 : 8 }));
        });
        using var provider = AdapterFixture.Provider(handler);
        var aRequest = AdapterFixture.Request([ProviderInput.Data("first-call")]); var bRequest = AdapterFixture.Request([ProviderInput.Data("second-call")]);
        var a = provider.ExchangeAsync(aRequest).AsTask(); var b = provider.ExchangeAsync(bRequest).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); release.SetResult(); var results = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("first-answer", results[0].Response!.Text); Assert.Equal("second-answer", results[1].Response!.Text);
        Assert.True(results[0].Response!.Attempt.Matches(aRequest.Attempt)); Assert.True(results[1].Response!.Attempt.Matches(bRequest.Attempt));
        Assert.Equal(3, results[0].Observation.Usage.InputTokens); Assert.Equal(8, results[1].Observation.Usage.InputTokens); Assert.Equal(2, handler.Sends);
    }
    [Fact]
    public async Task ASecondBodySerializationIsRefusedBeforeAnySecondBodyBytes()
    {
        var first = new MemoryStream(); var second = new MemoryStream();
        using var handler = new FakeHandler(async (message, token) =>
        { await message.Content!.CopyToAsync(first, token); await message.Content.CopyToAsync(second, token); return AdapterFixture.Http(AdapterFixture.Response()); });
        using var provider = AdapterFixture.Provider(handler); var result = await provider.ExchangeAsync(AdapterFixture.Request());
        Assert.Equal(ProviderOutcome.Failed, result.Outcome); Assert.True(first.Length > 0); Assert.Equal(0, second.Length); Assert.Equal(1, handler.Sends);
    }
    private class CountedStream(byte[] body) : Stream
    {
        private int position;
        internal int BytesRead => position;
        internal bool Disposed { get; private set; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) { var count = Math.Min(buffer.Length, body.Length - position); body.AsSpan(position, count).CopyTo(buffer); position += count; return count; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => ValueTask.FromResult(Read(buffer.Span));
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class CompletionStream(byte[] body, CancellationTokenSource cancel) : CountedStream(body)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { var read = Read(buffer.Span); if (read == 0) cancel.Cancel(); return ValueTask.FromResult(read); }
    }
    private sealed class FaultStream(CancellationTokenSource? cancel) : CountedStream([])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { if (cancel is not null) { cancel.Cancel(); throw new OperationCanceledException("synthetic-body-canary", token); } throw new IOException("synthetic-body-canary"); }
    }
}

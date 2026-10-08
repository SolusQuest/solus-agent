using System.Text;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.Adapter;

public sealed class ObservationIntegrationTests
{
    [Fact]
    public async Task PresealedRequestNeverEntersTheActualAdapterTransport()
    {
        var request = AdapterFixture.Request(); var frozen = request.Observation.Seal();
        using var handler = FakeHandler.Reply(AdapterFixture.Response()); using var provider = AdapterFixture.Provider(handler);
        var result = await provider.ExchangeAsync(request);
        Assert.Equal(ProviderError.ObservationClosed, result.Error); Assert.Null(result.Response); Assert.Equal(0, handler.Sends);
        Assert.Same(frozen, result.Observation); Assert.Same(frozen, request.Observation.Snapshot());
    }

    [Fact]
    public async Task ARequestAlreadyUsedByTheAdapterCannotDispatchAgainOrErasePriorUsage()
    {
        var request = AdapterFixture.Request();
        using var handler = FakeHandler.Reply(AdapterFixture.Response()); using var provider = AdapterFixture.Provider(handler);
        var accepted = await provider.ExchangeAsync(request); Assert.Equal(ProviderOutcome.Succeeded, accepted.Outcome);
        var repeated = await provider.ExchangeAsync(request);
        Assert.Equal(ProviderError.ObservationClosed, repeated.Error); Assert.Null(repeated.Response); Assert.Equal(1, handler.Sends);
        Assert.Same(accepted.Observation, repeated.Observation); Assert.True(request.Observation.HasCapturedUsage);
        Assert.Equal(10, repeated.Observation.Usage.InputTokens); Assert.Equal(DispatchExposure.Dispatched, repeated.Observation.Exposure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalSealDuringHeldSendOrBodyFreezesFactsAndExcludesLatePayload(bool holdBody)
    {
        var request = AdapterFixture.Request();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new HeldStream(Encoding.UTF8.GetBytes(AdapterFixture.Response()), holdBody ? entered : null, release);
        using var handler = new FakeHandler(async (_, _) =>
        {
            if (!holdBody) { entered.SetResult(); await release.Task; }
            var response = AdapterFixture.Http(""); response.Content = new StreamContent(stream);
            response.Content.Headers.ContentType = new("application/json"); return response;
        });
        using var provider = AdapterFixture.Provider(handler);
        var pending = provider.ExchangeAsync(request).AsTask(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var before = request.Observation.Snapshot(); var frozen = request.Observation.Seal();
        release.SetResult(); var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ProviderOutcome.Rejected, result.Outcome); Assert.Equal(ProviderError.ObservationClosed, result.Error);
        Assert.Null(result.Response); Assert.Same(frozen, result.Observation); Assert.Same(frozen, request.Observation.Seal());
        Assert.Equal(before.Exposure, frozen.Exposure); Assert.Equal(holdBody ? DispatchExposure.Dispatched : DispatchExposure.Unknown, frozen.Exposure);
        Assert.Equal(UsageCompleteness.Unavailable, frozen.Usage.Completeness); Assert.False(request.Observation.HasCapturedUsage);
        Assert.Equal(holdBody ? stream.BodyLength : 0, stream.BytesRead); Assert.True(stream.Disposed); Assert.Equal(1, handler.Sends);
        Assert.Equal(ProviderError.ObservationClosed, Assert.Throws<ProviderContractException>(() => request.Observation.CaptureUsage(new(10, 5))).Error);
    }

    [Fact]
    public async Task ValidForwardedCaptureSurvivesSealAndLateResponseRejection()
    {
        var request = AdapterFixture.Request(); UsageAttemptObservation? frozen = null;
        using var handler = new FakeHandler((_, _) =>
        {
            var response = AdapterFixture.Http(AdapterFixture.Response(usage: new { prompt_tokens = 7, completion_tokens = 3 }));
            // A trusted forwarding transport reports its already obtained numeric facts, then the local owner cuts admission.
            request.Observation.ObserveDispatch(DispatchExposure.Dispatched);
            request.Observation.CaptureUsage(new(7, 3)); frozen = request.Observation.Seal(); return Task.FromResult(response);
        });
        using var provider = AdapterFixture.Provider(handler); var result = await provider.ExchangeAsync(request);
        Assert.Equal(ProviderError.ObservationClosed, result.Error); Assert.Null(result.Response); Assert.Same(frozen, result.Observation);
        Assert.Equal(7, result.Observation.Usage.InputTokens); Assert.Equal(3, result.Observation.Usage.OutputTokens);
        Assert.True(request.Observation.HasCapturedUsage); Assert.Equal(1, handler.Sends);
    }

    private sealed class HeldStream(byte[] body, TaskCompletionSource? entered, TaskCompletionSource release) : Stream
    {
        private int position;
        public int BodyLength => body.Length;
        public int BytesRead => position;
        public bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (position == 0 && entered is not null) { entered.TrySetResult(); await release.Task; }
            var count = Math.Min(buffer.Length, body.Length - position); body.AsMemory(position, count).CopyTo(buffer); position += count; return count;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

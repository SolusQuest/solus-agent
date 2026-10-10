using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Context;

public sealed class OrdinaryCapturePublicationTests
{
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public async Task EncodingCapacityAndSinkFailurePublishOnlyActuallyPreparedCheckpoint(int capacityDelta, bool sinkFails)
    {
        async Task<(ContextExecutionResult Result, RestrictedContextHost Sink, PersistentProvider Provider)> Run(int capacity, bool failSink)
        {
            var provider = new PersistentProvider(); var retained = new RestrictedContextHost();
            var agent = (IContextAgent)RuntimeAgentFactory.Create(new(provider, [], new RuntimeHooks(),
                bounds: new ProviderExchangeBounds(maximumResponseBytes: 256)),
                new(maximumRetainedBytes: capacity, requireContinuation: true, timeProvider: new ControlledTimeProvider()));
            var observed = await ContextConsumer.RunAsync(agent, new(OrdinaryFixture.Request(), ContextExecutionIntent.Fresh),
                failSink ? new StoreThenThrow(retained) : retained);
            return (observed.Result, retained, provider);
        }
        var baseline = await Run(196608, false);
        var encodedBytes = baseline.Sink.CopyRestrictedContext().PayloadByteCount;
        Assert.True(encodedBytes > baseline.Provider.LastRequest!.PayloadByteCount + 256);
        var current = await Run(encodedBytes + capacityDelta, sinkFails);
        Assert.Equal(AgentTerminationReason.Completed, current.Result.Outcome!.Reason);
        Assert.Equal(1, current.Result.Outcome.CompletedWorkUnits);
        Assert.Equal(1, current.Provider.Effects);
        Assert.Equal(3, current.Result.Outcome.Usage!.InputTokens.ObservedTokens);
        if (capacityDelta < 0)
        {
            Assert.Equal(ContextCaptureStatus.Unavailable, current.Result.CaptureStatus);
            Assert.Null(current.Result.Checkpoint);
            Assert.Equal(0, current.Sink.CaptureCount);
            Assert.Throws<InvalidOperationException>(() => current.Sink.CopyRestrictedContext());
        }
        else
        {
            Assert.Equal(sinkFails ? ContextCaptureStatus.Failed : ContextCaptureStatus.Delivered, current.Result.CaptureStatus);
            Assert.NotNull(current.Result.Checkpoint);
            Assert.Equal(1, current.Sink.CaptureCount);
            Assert.Equal(encodedBytes, current.Sink.CopyRestrictedContext().PayloadByteCount);
            Assert.Equal(current.Result.ExecutionId, current.Result.Checkpoint.ExecutionId);
        }
    }

    private sealed class StoreThenThrow(RestrictedContextHost retained) : IRestrictedContextSink
    {
        public void Capture(AgentContextEnvelope context) { retained.Capture(context); throw new InvalidOperationException("SYNTHETIC_SINK_FAILURE"); }
    }
}

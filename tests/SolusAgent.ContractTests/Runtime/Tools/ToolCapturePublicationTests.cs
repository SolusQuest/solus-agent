using System.Text.Json.Nodes;
using CustomTools;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Tools;

public sealed class ToolCapturePublicationTests
{
    [Fact]
    public async Task ToolMetadataEncodingFailurePublishesNeitherEnvelopeNorCheckpoint()
    {
        var original = await ToolContextFixture.Capture(); var sink = new RestrictedContextHost();
        var counter = new CounterCapability(); var transform = new TransformCapability();
        var provider = new PersistentProvider(tools: true) { Respond = r => ToolContextFixture.Calls(r) };
        var result = await ToolContextFixture.Agent(provider, ToolContextFixture.Bindings(counter, transform),
            options: new(maximumRetainedBytes: original.Envelope.PayloadByteCount / 2, requireContinuation: true))
            .ExecuteWithContextAsync(new(ToolContextFixture.Request(), ContextExecutionIntent.Fresh), sink);
        Assert.Equal(1, counter.Effects); Assert.Equal(1, transform.Effects);
        Assert.Equal(AgentTerminationReason.ResourceLimit, result.Outcome!.Reason);
        Assert.Equal(ContextCaptureStatus.Unavailable, result.CaptureStatus); Assert.Null(result.Checkpoint);
        Assert.Equal(2, result.Outcome.Usage!.ToolInvocations!.Invoked);
    }
    [Fact]
    public async Task SinkStoreThenFailRetainsValidToolBytesWithoutChangingWorkOutcome()
    {
        var counter = new CounterCapability(); var transform = new TransformCapability(); var tools = ToolContextFixture.Bindings(counter, transform);
        var provider = new PersistentProvider(tools: true) { Respond = r => ToolContextFixture.Calls(r) }; var sink = new StoreThenFail();
        var result = await ToolContextFixture.Agent(provider, tools).ExecuteWithContextAsync(new(ToolContextFixture.Request(), ContextExecutionIntent.Fresh), sink);
        Assert.Equal(ContextCaptureStatus.Failed, result.CaptureStatus); Assert.NotNull(result.Checkpoint); Assert.NotNull(sink.Envelope);
        Assert.Equal(AgentTerminationReason.ResourceLimit, result.Outcome!.Reason); Assert.Equal(2, result.Outcome.Usage!.ToolInvocations!.Invoked);
        Assert.NotNull(JsonNode.Parse(sink.Envelope.CopyRestrictedPayload())!["ToolCursor"]);
        var next = ToolContextFixture.Request(); var nextProvider = new PersistentProvider(tools: true);
        var restored = await ToolContextFixture.Agent(nextProvider, tools, new SelectedAuthority(sink.Envelope, result.Checkpoint))
            .ExecuteWithContextAsync(new(next, ContextExecutionIntent.ContinueRun, sink.Envelope, OrdinaryFixture.Grant(result, next.ExecutionId)));
        Assert.Equal(ContextAdmission.Supplied, restored.Admission); Assert.Equal(AgentTerminationReason.Completed, restored.Outcome!.Reason);
        Assert.Equal(1, counter.Effects); Assert.Equal(1, transform.Effects);
    }
    private sealed class StoreThenFail : IRestrictedContextSink
    {
        internal AgentContextEnvelope? Envelope { get; private set; }
        public void Capture(AgentContextEnvelope envelope) { Envelope = envelope; throw new InvalidOperationException("PRIVATE_SINK_CANARY"); }
    }
}

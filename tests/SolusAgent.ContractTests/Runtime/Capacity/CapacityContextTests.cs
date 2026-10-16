using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CustomTools;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Capacity;

public sealed class CapacityContextTests
{
    [Theory]
    [InlineData(1, 1048576)]
    [InlineData(65, 32)]
    public async Task CapturableLargeContentAndExpandedInventoryRestoreWithoutRepeatedEffects(int turns, int artifactBytes)
    {
        var json = CapacityFixture.Envelope(CapacityFixture.Artifact(artifactBytes)); var capability = new TransformCapability();
        RuntimeToolRegistration[] tools = [new(CapacityFixture.Tool(json), capability)];
        var provider = new PersistentProvider(tools: true);
        provider.Respond = r => new(r.Scope, r.Attempt, ProviderFinish.ToolCalls, null,
            [CapacityFixture.Call("history_" + provider.Effects)], new(r.Scope, r.Attempt, [1, 2, 3]));
        var sink = new RestrictedContextHost(); var request = CapacityFixture.Request(turns);
        var source = await ToolContextFixture.Agent(provider, tools, options: CapacityFixture.Options(true), bounds: CapacityFixture.Bounds)
            .ExecuteWithContextAsync(new(request, ContextExecutionIntent.Fresh), sink);
        Assert.Equal(ContextCaptureStatus.Delivered, source.CaptureStatus); Assert.Equal(turns, capability.Effects);
        Assert.Equal(AgentTerminationReason.ResourceLimit, source.Outcome!.Reason); Assert.Equal(turns, source.Outcome.CompletedWorkUnits);
        var envelope = sink.CopyRestrictedContext(); Assert.True(envelope.PayloadByteCount <= 2 * CapacityFixture.MiB);
        using (var saved = JsonDocument.Parse(envelope.CopyRestrictedPayload())) Assert.Equal(1 + 2 * turns, saved.RootElement.GetProperty("Records").GetArrayLength());
        var nextProvider = new PersistentProvider(tools: true) { Respond = r => new(r.Scope, r.Attempt, ProviderFinish.Final, "complete", []) };
        nextProvider.Inspect = r =>
        {
            Assert.Equal(turns, r.History!.AcceptedOrigins.Count);
            var results = r.Inputs.Where(i => i.ToolResult is not null).Select(i => i.ToolResult!).ToArray(); Assert.Equal(turns, results.Length);
            Assert.All(results, t => { Assert.True(t.IsHistorical); Assert.True(t.InvocationStarted); CapacityFixture.Same(json, t.Json!); });
            Assert.Equal(new byte[] { 1, 2, 3 }, r.Continuation!.CopyReplayBytes());
        };
        var current = CapacityFixture.Request(1); var authority = new SelectedAuthority(envelope, source.Checkpoint!);
        var result = await ToolContextFixture.Agent(nextProvider, tools, authority, CapacityFixture.Options(true), bounds: CapacityFixture.Bounds)
            .ExecuteWithContextAsync(new(current, ContextExecutionIntent.ContinueRun, envelope, OrdinaryFixture.Grant(source, current.ExecutionId)));
        Assert.Equal(ContextAdmission.Supplied, result.Admission); Assert.Equal(AgentTerminationReason.Completed, result.Outcome!.Reason);
        Assert.Equal(1, authority.Claims); Assert.Equal(1, nextProvider.Imports); Assert.Equal(1, nextProvider.Effects);
        Assert.Equal(turns, capability.Effects); Assert.Equal(0, result.Outcome.Usage!.ToolInvocations!.Invoked);
        Assert.Equal(turns, Assert.Single(result.History).Usage.ToolInvocations!.Invoked);
    }

    [Fact]
    public async Task MissingNewBoundFieldRejectsStrictSavedShapeBeforeClaim()
    {
        var provider = new PersistentProvider { Respond = r => new(r.Scope, r.Attempt, ProviderFinish.Final, "complete", []) };
        var sink = new RestrictedContextHost(); var request = CapacityFixture.Request(1);
        var sourceAgent = (IContextAgent)CapacityFixture.Agent(provider);
        var source = await sourceAgent.ExecuteWithContextAsync(new(request, ContextExecutionIntent.Fresh), sink);
        Assert.Equal(ContextCaptureStatus.Delivered, source.CaptureStatus);
        Assert.Equal(AgentTerminationReason.Completed, source.Outcome!.Reason);
        var original = sink.CopyRestrictedContext(); var node = JsonNode.Parse(original.CopyRestrictedPayload())!;
        node["OriginalBounds"]!.AsObject().Remove("MaximumTextBytes");
        var changed = new AgentContextEnvelope(original.ImplementationId, original.FormatVersion, original.CompatibilityVersion, Encoding.UTF8.GetBytes(node.ToJsonString()));
        var current = CapacityFixture.Request(1); var authority = new SelectedAuthority(changed, source.Checkpoint!); var nextProvider = new PersistentProvider();
        var nextAgent = (IContextAgent)RuntimeAgentFactory.Create(new(nextProvider, [], new RuntimeHooks(), CapacityFixture.Bounds,
            contextAuthority: authority), CapacityFixture.Options());
        var result = await nextAgent.ExecuteWithContextAsync(new(current, ContextExecutionIntent.NewRunFromContext, changed, OrdinaryFixture.Grant(source, current.ExecutionId)));
        Assert.Equal(ContextAdmission.Rejected, result.Admission); Assert.Equal(0, authority.Claims); Assert.Equal(0, nextProvider.Imports); Assert.Equal(0, nextProvider.Effects);
    }

    [Fact]
    public async Task DeferredToolRestorationChecksCurrentTextCapacityBeforeClaim()
    {
        var json = CapacityFixture.Envelope(CapacityFixture.Artifact(32768)); var capability = new TransformCapability();
        RuntimeToolRegistration[] tools = [new(CapacityFixture.Tool(json), capability)];
        var bounds = new ProviderExchangeBounds(maximumRequestBytes: 128 * CapacityFixture.MiB, maximumResponseBytes: 8 * CapacityFixture.MiB);
        var provider = new PersistentProvider(tools: true);
        provider.Respond = r => provider.Effects == 1
            ? new(r.Scope, r.Attempt, ProviderFinish.ToolCalls, null, [CapacityFixture.Call()], new(r.Scope, r.Attempt, [1]))
            : new(r.Scope, r.Attempt, ProviderFinish.Final, "complete", [], new(r.Scope, r.Attempt, [2]));
        var sink = new RestrictedContextHost(); var source = await ToolContextFixture.Agent(provider, tools, options: CapacityFixture.Options(true), bounds: bounds)
            .ExecuteWithContextAsync(new(CapacityFixture.Request(), ContextExecutionIntent.Fresh), sink);
        Assert.Equal(AgentTerminationReason.Completed, source.Outcome!.Reason); Assert.Equal(ContextCaptureStatus.Delivered, source.CaptureStatus);
        var envelope = sink.CopyRestrictedContext(); var authority = new SelectedAuthority(envelope, source.Checkpoint!); var nextProvider = new PersistentProvider(tools: true);
        var current = new AgentRequest(Guid.NewGuid(), new string('x', 16385), [], new(1, TimeSpan.FromMinutes(1)), SolusAgent.Api.Capabilities.AgentCapability.None);
        var result = await ToolContextFixture.Agent(nextProvider, tools, authority, CapacityFixture.Options(true), bounds: bounds).ExecuteWithContextAsync(
            new(current, ContextExecutionIntent.NewRunFromContext, envelope, OrdinaryFixture.Grant(source, current.ExecutionId)));
        Assert.Equal(ContextAdmission.Rejected, result.Admission); Assert.Equal(0, authority.Claims); Assert.Equal(0, nextProvider.Imports);
        Assert.Equal(0, nextProvider.Effects); Assert.Equal(1, capability.Effects);
    }

    [Fact]
    public async Task LargeRecoveryReceiptUsesOriginalDescriptorAndQueryWithoutAnotherBusinessEffect()
    {
        var json = CapacityFixture.Envelope(CapacityFixture.Artifact(CapacityFixture.MiB));
        var target = new RecoveryCounterCapability("capacity_target", ToolRecoveryGuarantee.QueryOutcome, fixedResultJson: json) { ThrowAfterEffect = true };
        var descriptor = new RecoverableCounterTool(maximumResultBytes: 8 * CapacityFixture.MiB, resultSchema: ToolSchema.Parse(TransformTool.Schema));
        RuntimeToolRegistration[] tools = [new(descriptor, target, new("keyed_recovery", target.TargetSnapshot, target.Guarantee))];
        var provider = new PersistentProvider(tools: true) { Respond = r => new(r.Scope, r.Attempt, ProviderFinish.ToolCalls, null,
            [new("recover_large", "recover", "{\"amount\":1}")], new(r.Scope, r.Attempt, [1])) };
        var sink = new RestrictedContextHost(); var source = await ToolContextFixture.Agent(provider, tools, options: CapacityFixture.Options(true), bounds: CapacityFixture.Bounds)
            .ExecuteWithContextAsync(new(CapacityFixture.Request(2, usage: new(maximumToolInvocations: 1)), ContextExecutionIntent.Fresh), sink);
        Assert.Equal(ContextCaptureStatus.Delivered, source.CaptureStatus); Assert.Equal(1, target.Effects); Assert.Equal(1, target.Invocations);
        var envelope = sink.CopyRestrictedContext(); var nextProvider = new PersistentProvider(tools: true) { Respond = r => new(r.Scope, r.Attempt, ProviderFinish.Final, "complete", []) };
        nextProvider.Inspect = r => { var result = Assert.Single(r.Inputs, i => i.ToolResult is not null).ToolResult!;
            Assert.True(result.IsHistorical); CapacityFixture.Same(json, result.Json!); Assert.Equal("recover_large", result.Call.CallId); };
        var current = CapacityFixture.Request(1, usage: new(maximumToolInvocations: 1)); var nextSink = new RestrictedContextHost();
        var result = await ToolContextFixture.Agent(nextProvider, tools, new SelectedAuthority(envelope, source.Checkpoint!), CapacityFixture.Options(true), bounds: CapacityFixture.Bounds)
            .ExecuteWithContextAsync(new(current, ContextExecutionIntent.ContinueRun, envelope, OrdinaryFixture.Grant(source, current.ExecutionId)), nextSink);
        Assert.Equal(ContextAdmission.Supplied, result.Admission); Assert.Equal(AgentTerminationReason.Completed, result.Outcome!.Reason);
        Assert.Equal(1, target.Queries); Assert.Equal(1, target.Invocations); Assert.Equal(1, target.Effects);
        Assert.Equal(1, result.Outcome.Usage!.ToolInvocations!.Invoked); Assert.Equal(ContextCaptureStatus.Delivered, result.CaptureStatus);
    }

    [Fact]
    public async Task EncodedCaptureExactAndOneOverPreserveOutcomeAndPublishNoPartialState()
    {
        async Task<(ContextExecutionResult Result, RestrictedContextHost Sink)> Capture(int padding)
        {
            var provider = new PersistentProvider { Respond = r => new(r.Scope, r.Attempt, ProviderFinish.Final, "complete", []) };
            var sink = new RestrictedContextHost(); var clock = new ControlledTimeProvider();
            var agent = (IContextAgent)RuntimeAgentFactory.Create(new(provider, [], new RuntimeHooks(), CapacityFixture.Bounds), CapacityFixture.Options(clock: clock));
            var result = await agent.ExecuteWithContextAsync(new(CapacityFixture.Request(1, [new(AgentInputSource.Repository, new string('x', padding))]),
                ContextExecutionIntent.Fresh), sink);
            Assert.Equal(AgentTerminationReason.Completed, result.Outcome!.Reason); Assert.Equal(1, provider.Effects);
            return (result, sink);
        }
        var baseline = await Capture(1); var padding = 2 * CapacityFixture.MiB - baseline.Sink.CopyRestrictedContext().PayloadByteCount + 1;
        var exact = await Capture(padding);
        Assert.Equal(ContextCaptureStatus.Delivered, exact.Result.CaptureStatus); Assert.Equal(2 * CapacityFixture.MiB, exact.Sink.CopyRestrictedContext().PayloadByteCount);
        var over = await Capture(padding + 1);
        Assert.Equal(ContextCaptureStatus.Unavailable, over.Result.CaptureStatus); Assert.Null(over.Result.Checkpoint); Assert.Equal(0, over.Sink.CaptureCount);
        Assert.Equal(3, over.Result.Outcome!.Usage!.InputTokens.ObservedTokens);
    }
}

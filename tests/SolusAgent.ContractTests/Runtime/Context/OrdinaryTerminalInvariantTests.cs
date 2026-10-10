using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CustomTools;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Execution;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Context;

public sealed class OrdinaryTerminalInvariantTests
{
    [Theory]
    [InlineData("response-ceiling")]
    [InlineData("input-profile")]
    public async Task OriginalEffectiveBoundsMustBelongToFrozenConfigurationProfile(string mutation)
    {
        IContextAgent Agent(PersistentProvider p, IRuntimeContextAuthority? authority = null) => (IContextAgent)RuntimeAgentFactory.Create(
            new(p, [], new RuntimeHooks(), new(maximumResponseBytes: 1024), contextAuthority: authority), new(requireContinuation: true));
        var sink = new RestrictedContextHost(); var source = await Agent(new()).ExecuteWithContextAsync(new(OrdinaryFixture.Request(), ContextExecutionIntent.Fresh), sink);
        var bytes = sink.CopyRestrictedContext(); var node = JsonNode.Parse(bytes.CopyRestrictedPayload())!;
        foreach (var bounds in new[] { node["OriginalBounds"]!, node["Rounds"]![0]!["Facts"]![0]!["Bounds"]!, node["Records"]![1]!["Final"]!["Bounds"]! })
            bounds[mutation == "response-ceiling" ? "MaximumResponseBytes" : "MaximumInputs"] = mutation == "response-ceiling" ? 65536 : 1;
        bytes = Envelope(bytes, node); var authority = new SelectedAuthority(bytes, source.Checkpoint!); var provider = new PersistentProvider(); var next = OrdinaryFixture.Request();
        var result = await Agent(provider, authority).ExecuteWithContextAsync(new(next, ContextExecutionIntent.NewRunFromContext, bytes, OrdinaryFixture.Grant(source, next.ExecutionId)));
        Assert.Equal(ContextRejectionCode.InvalidContext, result.RejectionCode); Assert.Equal(0, authority.Claims); Assert.Equal(0, provider.Imports); Assert.Equal(0, provider.Effects);
    }
    [Theory]
    [InlineData("resource-host")]
    [InlineData("completed-cancel")]
    [InlineData("completed-deadline")]
    [InlineData("completed-unknown-closure")]
    [InlineData("wrong-cut-cause")]
    [InlineData("wrong-operation-stop")]
    [InlineData("wrong-settlement-stop")]
    [InlineData("wrong-outcome-error")]
    public async Task TrustedMetamorphicTerminalContradictionsRejectBeforeEffects(string mutation)
    {
        var final = mutation.StartsWith("completed", StringComparison.Ordinal) || mutation == "wrong-cut-cause";
        var (source, envelope) = await OrdinaryFixture.Capture(!final); var node = JsonNode.Parse(envelope.CopyRestrictedPayload())!;
        var round = node["Rounds"]![0]!; var fact = round["Facts"]![0]!;
        switch (mutation)
        {
            case "resource-host": round["Stop"] = (int)RuntimeStop.HostStopped; break;
            case "completed-cancel": fact["Stop"] = (int)RuntimeStop.Cancelled; break;
            case "completed-deadline": fact["Stop"] = (int)RuntimeStop.DurationLimit; break;
            case "completed-unknown-closure": fact["SettlementStop"] = (int)RuntimeStop.SettlementUnknown; fact["ClosureAcknowledged"] = false; node["Final"] = false; break;
            case "wrong-cut-cause": fact["Stop"] = (int)RuntimeStop.DurationLimit; round["Reason"] = (int)AgentTerminationReason.Cancelled; round["Stop"] = (int)RuntimeStop.Cancelled; break;
            case "wrong-operation-stop": fact["Stop"] = (int)RuntimeStop.SettlementUnknown; fact["Retry"] = null; node["Pending"] = null; node["RetryEligible"] = false; break;
            case "wrong-settlement-stop": fact["SettlementStop"] = (int)RuntimeStop.ExposureDenied; fact["ClosureAcknowledged"] = false; fact["Retry"] = null; node["Pending"] = null; node["RetryEligible"] = false; break;
            case "wrong-outcome-error": fact["Error"] = (int)ProviderError.None; break;
        }
        await RejectTrusted(source.Checkpoint!, envelope, node, final ? ContextExecutionIntent.NewRunFromContext : ContextExecutionIntent.ContinueRun);
    }

    [Fact]
    public async Task UnclassifiedIntermediateFailureCannotBecomeAcceptedHistory()
    {
        var provider = new ScriptedProvider([ScriptedProvider.Failure(new(ProviderRetryKind.Transient)), ScriptedProvider.Final]);
        var sink = new RestrictedContextHost();
        var request = OrdinaryFixture.Request(limits: new(maximumPhysicalDispatches: 2, retryPolicy: new(2)));
        var agent = (IContextAgent)RuntimeFixture.Agent(provider);
        var source = await agent.ExecuteWithContextAsync(new(request, ContextExecutionIntent.Fresh), sink);
        Assert.Equal(AgentTerminationReason.Completed, source.Outcome!.Reason);
        var bytes = sink.CopyRestrictedContext(); var node = JsonNode.Parse(bytes.CopyRestrictedPayload())!;
        node["Rounds"]![0]!["Facts"]![0]!["Retry"] = null;
        // Use the exact source provider scope/config so the terminal validator, not a binding mismatch, rejects.
        var bad = Envelope(bytes, node); var authority = new SelectedAuthority(bad, source.Checkpoint!);
        var nextProvider = new ScriptedProvider([ScriptedProvider.Final]); var hooks = new RuntimeHooks();
        var next = (IContextAgent)RuntimeAgentFactory.Create(new(nextProvider, [], hooks, contextAuthority: authority));
        var nextRequest = OrdinaryFixture.Request();
        var result = await next.ExecuteWithContextAsync(new(nextRequest, ContextExecutionIntent.NewRunFromContext, bad, OrdinaryFixture.Grant(source, nextRequest.ExecutionId)));
        Assert.Equal(ContextRejectionCode.InvalidContext, result.RejectionCode); Assert.Equal(0, authority.Claims); Assert.Empty(hooks.Exposures); Assert.Equal(0, nextProvider.Effects);
    }

    [Theory]
    [InlineData("cancel-progress")]
    [InlineData("deadline-progress")]
    [InlineData("observer-failure")]
    [InlineData("cancel-closure")]
    [InlineData("host-stop")]
    public async Task ActualAcceptedFinalUnderLaterStopOrObserverFailureRemainsValid(string phase)
    {
        var clock = new ControlledTimeProvider(); using var cancellation = new CancellationTokenSource(); var hooks = new RuntimeHooks();
        if (phase == "cancel-closure") hooks.After = (s, _) => { cancellation.Cancel(); return ValueTask.FromResult<SettlementAcknowledgement?>(RuntimeHooks.Continue(s)); };
        if (phase == "host-stop") hooks.After = (s, _) => ValueTask.FromResult<SettlementAcknowledgement?>(new(s.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Stop, s.Accounting));
        var progress = new InlineProgress(_ =>
        {
            if (phase == "cancel-progress") cancellation.Cancel();
            if (phase == "deadline-progress") clock.Advance(TimeSpan.FromSeconds(30));
            if (phase == "observer-failure") throw new InvalidOperationException("SYNTHETIC_OBSERVER");
        });
        var sink = new RestrictedContextHost();
        var source = await OrdinaryFixture.Agent(new(), hooks: hooks, options: new(timeProvider: clock, requireContinuation: true))
            .ExecuteWithContextAsync(new(OrdinaryFixture.Request(), ContextExecutionIntent.Fresh), sink, progress, cancellation.Token);
        Assert.Equal(1, source.Outcome!.CompletedWorkUnits); Assert.Equal(3, source.Outcome.Usage!.InputTokens.ObservedTokens);
        Assert.Equal(phase switch { "cancel-progress" or "cancel-closure" => AgentTerminationReason.Cancelled,
            "deadline-progress" => AgentTerminationReason.ResourceLimit, "observer-failure" => AgentTerminationReason.Failed, _ => AgentTerminationReason.Completed }, source.Outcome.Reason);
        var bytes = sink.CopyRestrictedContext(); var provider = new PersistentProvider(); var nextRequest = OrdinaryFixture.Request();
        var result = await OrdinaryFixture.Agent(provider, new SelectedAuthority(bytes, source.Checkpoint!)).ExecuteWithContextAsync(
            new(nextRequest, ContextExecutionIntent.NewRunFromContext, bytes, OrdinaryFixture.Grant(source, nextRequest.ExecutionId)));
        Assert.Equal(ContextAdmission.Supplied, result.Admission); Assert.Equal(AgentTerminationReason.Completed, result.Outcome!.Reason); Assert.Equal(1, provider.Effects);
    }

    [Fact]
    public async Task RetryMetadataWithoutOldRetryPolicyRemainsAvailableToNewAuthorizedPolicy()
    {
        var (source, bytes) = await OrdinaryFixture.Capture(request: OrdinaryFixture.Request(limits: new(maximumPhysicalDispatches: 1)));
        Assert.Equal(AgentTerminationReason.Failed, source.Outcome!.Reason);
        var next = OrdinaryFixture.Request(); var provider = new PersistentProvider();
        var result = await OrdinaryFixture.Agent(provider, new SelectedAuthority(bytes, source.Checkpoint!)).ExecuteWithContextAsync(
            new(next, ContextExecutionIntent.ContinueRun, bytes, OrdinaryFixture.Grant(source, next.ExecutionId)));
        Assert.Equal(ContextAdmission.Supplied, result.Admission); Assert.Equal(AgentTerminationReason.Completed, result.Outcome!.Reason);
    }

    [Fact]
    public async Task ExportTimeCannotRewriteFrozenCompletedRound()
    {
        var clock = new ControlledTimeProvider(); var provider = new PersistentProvider { BeforeExport = () => clock.Advance(TimeSpan.FromSeconds(40)) };
        var (source, bytes) = await OrdinaryFixture.Capture(false, options: new(timeProvider: clock, requireContinuation: true), provider: provider);
        Assert.Equal(AgentTerminationReason.Completed, source.Outcome!.Reason); Assert.Equal(TimeSpan.FromSeconds(40).Ticks, clock.GetTimestamp());
        var node = JsonNode.Parse(bytes.CopyRestrictedPayload())!; Assert.Equal((int)RuntimeStop.HostStopped, (int)node["Rounds"]![0]!["Stop"]!);
        Assert.Equal(TimeSpan.Zero, TimeSpan.Parse((string)node["Elapsed"]!));
        var next = OrdinaryFixture.Request();
        var result = await OrdinaryFixture.Agent(new(), new SelectedAuthority(bytes, source.Checkpoint!)).ExecuteWithContextAsync(
            new(next, ContextExecutionIntent.NewRunFromContext, bytes, OrdinaryFixture.Grant(source, next.ExecutionId)));
        Assert.Equal(AgentTerminationReason.Completed, result.Outcome!.Reason);
    }

    [Fact]
    public async Task ReservedAttemptCutBeforeExposureDoesNotInventClosureAcknowledgement()
    {
        var provider = new PersistentProvider(); var hooks = new RuntimeHooks(); using var cancellation = new CancellationTokenSource();
        var configuration = new SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration(provider, [], hooks); var options = new RuntimeOptions(requireContinuation: true);
        var request = OrdinaryFixture.Request(); using var cut = new RunCut(TimeProvider.System, request.Bounds.MaximumDuration, cancellation.Token);
        var state = new RunState(request, configuration, options, cut); state.Initialize(); var admitted = state.AdmitTurn()!;
        cancellation.Cancel(); var attempt = await ProviderAttemptOperation.ExecuteAsync(state, admitted);
        Assert.False(attempt.ClosureAcknowledged); Assert.Empty(hooks.Exposures); Assert.Empty(hooks.Settlements); Assert.Equal(0, provider.Effects);
        var outcome = state.Outcome(AgentTerminationReason.Cancelled); var bytes = state.CaptureOrdinary(outcome, out var checkpoint)!;
        Assert.NotNull(bytes); var node = JsonNode.Parse(bytes.CopyRestrictedPayload())!;
        Assert.False((bool)node["Rounds"]![0]!["Facts"]![0]!["ClosureAcknowledged"]!);
        Assert.Equal(DispatchExposure.NotDispatched, Assert.Single(outcome.Usage!.Attempts).Exposure);
        var nextRequest = OrdinaryFixture.Request(); var authority = new SelectedAuthority(bytes, checkpoint!); var nextProvider = new PersistentProvider();
        var next = await OrdinaryFixture.Agent(nextProvider, authority).ExecuteWithContextAsync(new(nextRequest, ContextExecutionIntent.ContinueRun, bytes,
            new(Guid.NewGuid(), checkpoint!, nextRequest.ExecutionId, Guid.NewGuid())));
        Assert.Equal(ContextAdmission.Rejected, next.Admission); Assert.Equal(0, authority.Claims); Assert.Equal(0, nextProvider.Imports);
    }

    [Theory]
    [InlineData("top")]
    [InlineData("all-last")]
    [InlineData("old-final")]
    public async Task FinalMustSatisfyItsOwnOriginalEffectiveBounds(string mutation)
    {
        var (source, bytes) = await OrdinaryFixture.Capture(false);
        if (mutation == "old-final")
        {
            var sink = new RestrictedContextHost(); var second = OrdinaryFixture.Request();
            source = await OrdinaryFixture.Agent(new(), new SelectedAuthority(bytes, source.Checkpoint!)).ExecuteWithContextAsync(
                new(second, ContextExecutionIntent.NewRunFromContext, bytes, OrdinaryFixture.Grant(source, second.ExecutionId)), sink);
            bytes = sink.CopyRestrictedContext();
        }
        var node = JsonNode.Parse(bytes.CopyRestrictedPayload())!;
        if (mutation != "old-final") node["OriginalBounds"]!["MaximumResponseBytes"] = 1;
        if (mutation is "all-last" or "old-final")
        {
            node["Records"]![1]!["Final"]!["Bounds"]!["MaximumResponseBytes"] = 1;
            node["Rounds"]![0]!["Facts"]![0]!["Bounds"]!["MaximumResponseBytes"] = 1;
        }
        await RejectTrusted(source.Checkpoint!, bytes, node, ContextExecutionIntent.NewRunFromContext);
    }

    [Fact]
    public async Task DifferentHistoricalFinalBoundsRoundtripIndependently()
    {
        var (first, bytes) = await OrdinaryFixture.Capture(false); var secondRequest = OrdinaryFixture.Request(); var secondProvider = new PersistentProvider();
        var capacity = bytes.PayloadByteCount + Encoding.UTF8.GetByteCount(secondRequest.Instructions) + 20000;
        var sink = new RestrictedContextHost();
        var second = await OrdinaryFixture.Agent(secondProvider, new SelectedAuthority(bytes, first.Checkpoint!), options: new(maximumRetainedBytes: capacity, requireContinuation: true))
            .ExecuteWithContextAsync(new(secondRequest, ContextExecutionIntent.NewRunFromContext, bytes, OrdinaryFixture.Grant(first, secondRequest.ExecutionId)), sink);
        Assert.Equal(20000, secondProvider.LastRequest!.Bounds.MaximumResponseBytes); Assert.Equal(ContextCaptureStatus.Delivered, second.CaptureStatus);
        var saved = sink.CopyRestrictedContext(); var node = JsonNode.Parse(saved.CopyRestrictedPayload())!;
        Assert.Equal(65536, (int)node["Records"]![1]!["Final"]!["Bounds"]!["MaximumResponseBytes"]!);
        Assert.Equal(20000, (int)node["Records"]![2]!["Final"]!["Bounds"]!["MaximumResponseBytes"]!);
        var third = OrdinaryFixture.Request(); var result = await OrdinaryFixture.Agent(new(), new SelectedAuthority(saved, second.Checkpoint!))
            .ExecuteWithContextAsync(new(third, ContextExecutionIntent.NewRunFromContext, saved, OrdinaryFixture.Grant(second, third.ExecutionId)));
        Assert.Equal(AgentTerminationReason.Completed, result.Outcome!.Reason); Assert.Equal(2, result.History.Count);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("one-under")]
    [InlineData("omitted-definitions")]
    public async Task RestoredByteCapacityIncludesLiveDefinitionsOnce(string mode)
    {
        var tools = Enumerable.Range(0, 8).Select(i => new RuntimeToolRegistration(new LargeDefinitionTool("t" + i), new CounterCapability())).ToArray();
        IContextAgent Agent(PersistentProvider p, IRuntimeContextAuthority? a = null, int capacity = 196608) => (IContextAgent)RuntimeAgentFactory.Create(
            new(p, tools, new RuntimeHooks(), contextAuthority: a), new(maximumRetainedBytes: capacity, requireContinuation: true));
        var original = new PersistentProvider(true, true); var sink = new RestrictedContextHost();
        var source = await Agent(original).ExecuteWithContextAsync(new(OrdinaryFixture.Request(), ContextExecutionIntent.Fresh), sink);
        Assert.Equal(ContextCaptureStatus.Delivered, source.CaptureStatus); var bytes = sink.CopyRestrictedContext(); var old = original.LastRequest!;
        var bare = new ProviderRequest(old.Scope, old.Attempt, old.Inputs, [], old.Continuation, ProviderCapabilities.Continuation, old.Bounds);
        var definitions = old.PayloadByteCount - bare.PayloadByteCount;
        var oldOmission = bytes.PayloadByteCount + old.Bounds.MaximumResponseBytes;
        Assert.True(oldOmission < old.PayloadByteCount); // This fixture detects the actual omission, not merely response reservation.
        var capacity = mode == "omitted-definitions" ? oldOmission : bytes.PayloadByteCount + definitions + old.Bounds.MaximumResponseBytes - (mode == "one-under" ? 1 : 0);
        var authority = new SelectedAuthority(bytes, source.Checkpoint!); var provider = new PersistentProvider(tools: true); var next = OrdinaryFixture.Request();
        var result = await Agent(provider, authority, capacity).ExecuteWithContextAsync(new(next, ContextExecutionIntent.ContinueRun, bytes, OrdinaryFixture.Grant(source, next.ExecutionId)));
        Assert.Equal(mode == "exact" ? ContextAdmission.Supplied : ContextAdmission.Rejected, result.Admission);
        Assert.Equal(mode == "exact" ? 1 : 0, authority.Claims); Assert.Equal(mode == "exact" ? 1 : 0, provider.Effects);
        if (mode == "exact") Assert.Equal(old.Bounds.MaximumResponseBytes, provider.LastRequest!.Bounds.MaximumResponseBytes);
    }

    private static AgentContextEnvelope Envelope(AgentContextEnvelope original, JsonNode node) => new(original.ImplementationId, 1, 1, JsonSerializer.SerializeToUtf8Bytes(node));
    private static async Task RejectTrusted(ContextCheckpointInfo checkpoint, AgentContextEnvelope original, JsonNode node, ContextExecutionIntent intent)
    {
        var bytes = Envelope(original, node); var authority = new SelectedAuthority(bytes, checkpoint); var provider = new PersistentProvider();
        var request = OrdinaryFixture.Request(); var hooks = new RuntimeHooks();
        var result = await OrdinaryFixture.Agent(provider, authority, hooks).ExecuteWithContextAsync(new(request, intent, bytes,
            new(Guid.NewGuid(), checkpoint, request.ExecutionId, Guid.NewGuid())));
        Assert.Equal(ContextAdmission.Rejected, result.Admission); Assert.Equal(ContextRejectionCode.InvalidContext, result.RejectionCode);
        Assert.Equal(0, authority.Claims); Assert.Equal(0, provider.Imports); Assert.Equal(0, provider.Effects); Assert.Empty(hooks.Exposures);
    }
    private sealed class LargeDefinitionTool(string name) : FunctionTool<CounterCapability>(new(name, new string('d', 1024), Schema(), Schema(), "counter_increment", ToolEffect.ReadOnly))
    {
        private static ToolSchema Schema()
        {
            var names = Enumerable.Range(0, 32).Select(i => $"field{i:00}_" + new string('x', 48)).ToArray();
            return ToolSchema.Parse(JsonSerializer.Serialize(new { type = "object", properties = names.ToDictionary(n => n, _ => new { type = "string" }), required = names, additionalProperties = false }));
        }
        protected override ValueTask<ToolOutput> InvokeCoreAsync(ToolCall call, CounterCapability capability, CancellationToken token) => throw new InvalidOperationException("Definition-only test must not invoke tools.");
    }
}

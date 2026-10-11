using System.Text.Json.Nodes;
using CustomTools;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Tools;

public sealed class PartialRecoveryTests
{
    internal sealed record Source(ContextExecutionResult Result, AgentContextEnvelope Envelope, CounterCapability Counter,
        RecoveryCounterCapability Recovery, RuntimeToolRegistration[] Tools);
    internal static RuntimeToolRegistration[] Bindings(CounterCapability counter, RecoveryCounterCapability recovery) =>
        [new(new CounterTool(maximumResultBytes: 100), counter), new(new RecoverableCounterTool(), recovery,
            new("keyed_recovery", recovery.TargetSnapshot, recovery.Guarantee))];
    internal static ProviderResponse Calls(ProviderRequest r) => new(r.Scope, r.Attempt, ProviderFinish.ToolCalls, "PARTIAL_MODEL_CANARY",
        [new("first", "counter", "{\"amount\":7}"), new("uncertain", "recover", "{\"amount\":5}"), new("last", "counter", "{\"amount\":1}")], new(r.Scope, r.Attempt, [1]));
    internal static async Task<Source> Capture(ToolRecoveryGuarantee guarantee = ToolRecoveryGuarantee.QueryOutcome,
        Action<RecoveryCounterCapability>? configure = null, bool cancel = false, bool suffix = true, ProviderExchangeBounds? bounds = null)
    {
        var counter = new CounterCapability(); var recovery = new RecoveryCounterCapability("TARGET_CANARY", guarantee) { ThrowAfterEffect = !cancel };
        configure?.Invoke(recovery);
        var tools = Bindings(counter, recovery); var provider = new PersistentProvider(tools: true) { Respond = r =>
        { var response = Calls(r); return suffix ? response : new(r.Scope, r.Attempt, ProviderFinish.ToolCalls, response.Text, response.Calls.Take(2).ToArray(), response.Continuation); } };
        var request = ToolContextFixture.Request(3, new(maximumToolInvocations: 3)); var sink = new RestrictedContextHost();
        using var cancellation = new CancellationTokenSource();
        var pending = ToolContextFixture.Agent(provider, tools, bounds: bounds).ExecuteWithContextAsync(new(request, ContextExecutionIntent.Fresh), sink,
            cancellationToken: cancellation.Token).AsTask();
        if (cancel) { await recovery.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel(); }
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ContextCaptureStatus.Delivered, result.CaptureStatus);
        return new(result, sink.CopyRestrictedContext(), counter, recovery, tools);
    }
    internal static async Task<(ContextExecutionResult Result, RestrictedContextHost Sink, PersistentProvider Provider)> Resume(Source source,
        int allowance = 2, Action<PersistentProvider>? configure = null, RuntimeToolRegistration[]? tools = null,
        SolusAgent.Runtime.Startup.RuntimeOptions? options = null, ProviderExchangeBounds? bounds = null)
    {
        var provider = new PersistentProvider(tools: true) { Respond = ToolContextFixture.Final }; configure?.Invoke(provider);
        var request = ToolContextFixture.Request(2, new(maximumToolInvocations: allowance)); var sink = new RestrictedContextHost();
        var result = await ToolContextFixture.Agent(provider, tools ?? Bindings(source.Counter, source.Recovery),
            new SelectedAuthority(source.Envelope, source.Result.Checkpoint!), options, bounds: bounds).ExecuteWithContextAsync(
            new(request, ContextExecutionIntent.ContinueRun, source.Envelope, OrdinaryFixture.Grant(source.Result, request.ExecutionId)), sink);
        return (result, sink, provider);
    }
    [Theory]
    [InlineData(ToolRecoveryGuarantee.QueryOutcome)]
    [InlineData(ToolRecoveryGuarantee.ConcurrentSafeRepeat)]
    public async Task CompletesOnlyRemainingMembersAndKeepsOriginalFacts(ToolRecoveryGuarantee guarantee)
    {
        var source = await Capture(guarantee); source.Recovery.ThrowAfterEffect = false;
        var original = JsonNode.Parse(source.Envelope.CopyRestrictedPayload())!;
        var (result, sink, provider) = await Resume(source, configure: p => p.Inspect = r =>
        {
            var results = r.Inputs.Where(i => i.ToolResult is not null).Select(i => i.ToolResult!).ToArray();
            Assert.Equal(new[] { "first", "uncertain", "last" }, results.Select(t => t.Call.CallId));
            Assert.All(results, t => Assert.Equal(ToolOutcome.Succeeded, t.Outcome));
            Assert.True(results[0].IsHistorical); Assert.Equal(guarantee == ToolRecoveryGuarantee.QueryOutcome, results[1].IsHistorical);
            Assert.True(results[1].InvocationStarted);
        });
        Assert.Equal(ContextAdmission.Supplied, result.Admission); Assert.Equal(AgentTerminationReason.Completed, result.Outcome!.Reason);
        Assert.Equal(2, result.Outcome.Usage!.ToolInvocations!.Invoked); Assert.Equal(2, result.History[0].Usage.ToolInvocations!.Invoked);
        Assert.Equal(1, result.History[0].Usage.ToolInvocations!.ReleasedUnstarted);
        Assert.Equal(2, source.Counter.Effects); Assert.Equal(8, source.Counter.Total); Assert.Equal(1, source.Recovery.Effects);
        Assert.Equal(guarantee == ToolRecoveryGuarantee.QueryOutcome ? 1 : 2, source.Recovery.Invocations);
        Assert.Equal(guarantee == ToolRecoveryGuarantee.QueryOutcome ? 1 : 0, source.Recovery.Queries); Assert.Equal(1, provider.Effects);
        Assert.Equal(ContextCaptureStatus.Delivered, result.CaptureStatus);
        var saved = JsonNode.Parse(sink.CopyRestrictedContext().CopyRestrictedPayload())!;
        Assert.Equal(original["Members"]!.ToJsonString(), saved["Members"]!.ToJsonString());
        Assert.Equal(2, saved["Rounds"]![1]!["RecoveryActions"]!.AsArray().Count);
        Assert.DoesNotContain("TARGET_CANARY", result.ToString()); Assert.DoesNotContain("PARTIAL_MODEL_CANARY", result.Outcome.ToString());
        // The completed capture must itself be consumable; this exercises full historical action validation.
        var next = ToolContextFixture.Request(); var authority = new SelectedAuthority(sink.CopyRestrictedContext(), result.Checkpoint!);
        var later = await ToolContextFixture.Agent(new(tools: true), Bindings(source.Counter, source.Recovery), authority).ExecuteWithContextAsync(
            new(next, ContextExecutionIntent.NewRunFromContext, sink.CopyRestrictedContext(), OrdinaryFixture.Grant(result, next.ExecutionId)));
        Assert.Equal(ContextAdmission.Supplied, later.Admission); Assert.Equal(0, later.Outcome!.Usage!.ToolInvocations!.Invoked);
    }
    [Theory]
    [InlineData(RecoveryQueryBehavior.Unresolved)]
    [InlineData(RecoveryQueryBehavior.WrongOperation)]
    [InlineData(RecoveryQueryBehavior.WrongCall)]
    [InlineData(RecoveryQueryBehavior.InvalidJson)]
    [InlineData(RecoveryQueryBehavior.Oversized)]
    [InlineData(RecoveryQueryBehavior.PriorFailure)]
    [InlineData(RecoveryQueryBehavior.Throw)]
    public async Task QueryFailureNeverRedispatchesOrRunsDependentSuffix(RecoveryQueryBehavior behavior)
    {
        var source = await Capture(); source.Recovery.QueryBehavior = behavior;
        var (result, sink, provider) = await Resume(source);
        Assert.Equal(ContextAdmission.Supplied, result.Admission); Assert.Equal(AgentTerminationReason.Failed, result.Outcome!.Reason);
        Assert.Equal(1, result.Outcome.Usage!.ToolInvocations!.Invoked); Assert.Equal(1, result.Outcome.Usage.ToolInvocations.ReleasedUnstarted);
        Assert.Equal(1, source.Recovery.Invocations); Assert.Equal(1, source.Recovery.Queries); Assert.Equal(1, source.Counter.Effects);
        Assert.Equal(0, provider.Effects); Assert.Equal(ContextCaptureStatus.Delivered, result.CaptureStatus);
        source.Recovery.QueryBehavior = RecoveryQueryBehavior.Normal;
        var (final, _, _) = await Resume(source with { Envelope = sink.CopyRestrictedContext(), Result = result });
        if (behavior == RecoveryQueryBehavior.PriorFailure)
        { Assert.Equal(ContextAdmission.Rejected, final.Admission); Assert.Equal(1, source.Recovery.Queries); return; }
        Assert.Equal(ContextAdmission.Supplied, final.Admission); Assert.Equal(AgentTerminationReason.Completed, final.Outcome!.Reason);
        Assert.Equal(2, final.History.Count); Assert.Equal(3, final.History.Sum(r => r.Usage.ToolInvocations!.Invoked));
        Assert.Equal(2, final.Outcome.Usage!.ToolInvocations!.Invoked);
    }
    [Theory]
    [InlineData(ToolRecoveryGuarantee.QueryOutcome)]
    [InlineData(ToolRecoveryGuarantee.ConcurrentSafeRepeat)]
    public async Task EntireRemainingBatchRequiresAllowanceBeforeAnyRecovery(ToolRecoveryGuarantee guarantee)
    {
        var source = await Capture(guarantee); source.Recovery.ThrowAfterEffect = false;
        var (result, _, provider) = await Resume(source, allowance: 1);
        Assert.Equal(ContextAdmission.Supplied, result.Admission); Assert.Equal(AgentTerminationReason.ResourceLimit, result.Outcome!.Reason);
        Assert.Equal(0, result.Outcome.Usage!.ToolInvocations!.Invoked); Assert.Equal(0, source.Recovery.Queries);
        Assert.Equal(1, source.Recovery.Invocations); Assert.Equal(1, source.Counter.Effects); Assert.Equal(0, provider.Effects);
    }
    [Fact]
    public async Task ChangedOrMissingHostBindingRejectsBeforeImportOrEffects()
    {
        var source = await Capture();
        foreach (var binding in new ToolRecoveryBinding?[] { null, new("keyed_recovery", "CHANGED_TARGET", ToolRecoveryGuarantee.QueryOutcome) })
        {
            RuntimeToolRegistration[] tools = [source.Tools[0], new(new RecoverableCounterTool(), source.Recovery, binding)];
            var (result, _, provider) = await Resume(source, tools: tools);
            Assert.Equal(ContextAdmission.Rejected, result.Admission); Assert.Equal(0, provider.Imports); Assert.Equal(0, source.Recovery.Queries);
        }
        Assert.Equal(1, source.Recovery.Invocations); Assert.Equal(1, source.Counter.Effects);
    }
    [Fact]
    public async Task DeniedCurrentPermissionBlocksWholeRemainingBatch()
    {
        var source = await Capture(); source.Recovery.Allowed = false;
        var (result, _, provider) = await Resume(source);
        Assert.Equal(ContextAdmission.Supplied, result.Admission); Assert.Equal(0, result.Outcome!.Usage!.ToolInvocations!.Invoked);
        Assert.Equal(0, source.Recovery.Queries); Assert.Equal(0, provider.Effects); Assert.Equal(1, source.Counter.Effects);
    }
    [Fact]
    public async Task OutstandingOriginalUsesSameKeyAndLateCompletionCannotRewriteFrozenContext()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RecoveryCounterCapability? target = null;
        var source = await Capture(ToolRecoveryGuarantee.ConcurrentSafeRepeat, cap =>
        { target = cap; cap.BeforeReturn = (_, _) => cap.Invocations == 1 ? new(release.Task) : ValueTask.CompletedTask; }, cancel: true);
        var frozen = source.Envelope.CopyRestrictedPayload();
        var (result, _, _) = await Resume(source);
        Assert.Equal(AgentTerminationReason.Completed, result.Outcome!.Reason); Assert.Equal(1, target!.Effects); Assert.Equal(2, target.Invocations);
        release.TrySetResult(); Assert.Equal(frozen, source.Envelope.CopyRestrictedPayload());
        Assert.Equal(2, result.Outcome.Usage!.ToolInvocations!.Invoked);
    }
    [Theory]
    [InlineData(ToolRecoveryGuarantee.QueryOutcome, false)]
    [InlineData(ToolRecoveryGuarantee.QueryOutcome, true)]
    [InlineData(ToolRecoveryGuarantee.ConcurrentSafeRepeat, false)]
    public async Task OneRemainingEntryChargesExactlyOneQueryOrRepeat(ToolRecoveryGuarantee guarantee, bool unresolved)
    {
        var source = await Capture(guarantee, suffix: false); source.Recovery.ThrowAfterEffect = false;
        if (unresolved) source.Recovery.QueryBehavior = RecoveryQueryBehavior.Unresolved;
        var (result, _, provider) = await Resume(source, allowance: 1);
        Assert.Equal(1, result.Outcome!.Usage!.ToolInvocations!.Invoked); Assert.Equal(0, result.Outcome.Usage.ToolInvocations.ReleasedUnstarted);
        Assert.Equal(unresolved ? AgentTerminationReason.Failed : AgentTerminationReason.Completed, result.Outcome.Reason);
        Assert.Equal(unresolved ? 0 : 1, provider.Effects); Assert.Equal(1, source.Recovery.Effects);
        Assert.Equal(guarantee == ToolRecoveryGuarantee.QueryOutcome ? 1 : 2, source.Recovery.Invocations);
    }
}

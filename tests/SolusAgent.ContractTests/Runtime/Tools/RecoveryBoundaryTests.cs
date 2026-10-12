using System.Text.Json;
using System.Text.Json.Nodes;
using CustomTools;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Tools;

public sealed class RecoveryBoundaryTests
{
    private sealed class ProbeAuthority : IRuntimeContextAuthority
    {
        internal int Claims;
        public bool TryClaim(AgentContextEnvelope envelope, ContextRoundGrant grant) { Claims++; return false; }
    }
    [Theory]
    [InlineData(ToolRecoveryGuarantee.QueryOutcome, 4, false)]
    [InlineData(ToolRecoveryGuarantee.QueryOutcome, 5, false)]
    [InlineData(ToolRecoveryGuarantee.ConcurrentSafeRepeat, 4, false)]
    [InlineData(ToolRecoveryGuarantee.ConcurrentSafeRepeat, 5, false)]
    [InlineData(ToolRecoveryGuarantee.QueryOutcome, 4, true)]
    [InlineData(ToolRecoveryGuarantee.QueryOutcome, 5, true)]
    [InlineData(ToolRecoveryGuarantee.ConcurrentSafeRepeat, 4, true)]
    [InlineData(ToolRecoveryGuarantee.ConcurrentSafeRepeat, 5, true)]
    public async Task ProjectedProviderInputsAreAdmittedBeforeClaimAndRecovery(ToolRecoveryGuarantee guarantee, int maximumInputs, bool recordedFailure)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bounds = new ProviderExchangeBounds(maximumInputs: maximumInputs);
        var source = await PartialRecoveryTests.Capture(guarantee, cap =>
        { if (!recordedFailure) cap.BeforeReturn = (_, _) => cap.Invocations == 1 ? new(release.Task) : ValueTask.CompletedTask; },
            cancel: !recordedFailure, bounds: bounds);
        source.Recovery.ThrowAfterEffect = false;
        try
        {
            var authority = new SelectedAuthority(source.Envelope, source.Result.Checkpoint!);
            var provider = new PersistentProvider(tools: true) { Respond = ToolContextFixture.Final };
            var request = ToolContextFixture.Request(2, new(maximumToolInvocations: 2));
            var result = await ToolContextFixture.Agent(provider, PartialRecoveryTests.Bindings(source.Counter, source.Recovery), authority, bounds: bounds)
                .ExecuteWithContextAsync(new(request, ContextExecutionIntent.ContinueRun, source.Envelope, OrdinaryFixture.Grant(source.Result, request.ExecutionId)));
            if (maximumInputs == 4)
            {
                Assert.Equal(ContextAdmission.Rejected, result.Admission); Assert.Equal(0, authority.Claims); Assert.Equal(0, provider.Imports);
                Assert.Equal(0, source.Recovery.Queries); Assert.Equal(1, source.Recovery.Invocations); Assert.Equal(1, source.Counter.Effects);
            }
            else
            {
                Assert.Equal(ContextAdmission.Supplied, result.Admission); Assert.Equal(AgentTerminationReason.Completed, result.Outcome!.Reason);
                Assert.Equal(2, result.Outcome.Usage!.ToolInvocations!.Invoked); Assert.Equal(1, authority.Claims); Assert.Equal(2, source.Counter.Effects);
            }
        }
        finally { release.TrySetResult(); }
    }
    [Fact]
    public async Task ExactConservativeRetentionCapacityAndOneByteBelowAreCheckedBeforeClaim()
    {
        var source = await PartialRecoveryTests.Capture();
        async Task<bool> Fits(int capacity)
        {
            var authority = new ProbeAuthority(); var request = ToolContextFixture.Request(2, new(maximumToolInvocations: 2));
            var provider = new PersistentProvider(tools: true);
            await ToolContextFixture.Agent(provider, source.Tools, authority, new(maximumRetainedBytes: capacity, requireContinuation: true))
                .ExecuteWithContextAsync(new(request, ContextExecutionIntent.ContinueRun, source.Envelope, OrdinaryFixture.Grant(source.Result, request.ExecutionId)));
            Assert.Equal(0, provider.Imports); Assert.Equal(0, source.Recovery.Queries); return authority.Claims == 1;
        }
        var low = 1; var high = 196608; Assert.True(await Fits(high));
        while (low < high) { var middle = low + (high - low) / 2; if (await Fits(middle)) high = middle; else low = middle + 1; }
        Assert.False(await Fits(low - 1)); Assert.True(await Fits(low));
        var (below, _, rejectedProvider) = await PartialRecoveryTests.Resume(source, options: new(maximumRetainedBytes: low - 1, requireContinuation: true));
        Assert.Equal(ContextAdmission.Rejected, below.Admission); Assert.Equal(0, rejectedProvider.Imports);
        var (exact, _, _) = await PartialRecoveryTests.Resume(source, options: new(maximumRetainedBytes: low, requireContinuation: true));
        Assert.Equal(ContextAdmission.Supplied, exact.Admission); Assert.Equal(AgentTerminationReason.Completed, exact.Outcome!.Reason);
        Assert.Equal(ContextCaptureStatus.Delivered, exact.CaptureStatus); Assert.Equal(2, exact.Outcome.Usage!.ToolInvocations!.Invoked);
    }
    [Fact]
    public async Task WholeRemainingRecordReservationRejectsOneBelowBeforeClaim()
    {
        var source = await PartialRecoveryTests.Capture();
        var (below, _, provider) = await PartialRecoveryTests.Resume(source, options: new(maximumRecords: 5, requireContinuation: true));
        Assert.Equal(ContextAdmission.Rejected, below.Admission); Assert.Equal(0, provider.Imports); Assert.Equal(0, source.Recovery.Queries);
        var (exact, _, _) = await PartialRecoveryTests.Resume(source, options: new(maximumRecords: 6, requireContinuation: true));
        Assert.Equal(ContextAdmission.Supplied, exact.Admission); Assert.Equal(AgentTerminationReason.Completed, exact.Outcome!.Reason);
        Assert.Equal(ContextCaptureStatus.Delivered, exact.CaptureStatus);
    }
    [Theory]
    [InlineData("duplicate-action")]
    [InlineData("completed-member")]
    [InlineData("wrong-call")]
    [InlineData("wrong-kind")]
    [InlineData("duplicate-operation")]
    [InlineData("old-usage")]
    [InlineData("current-usage")]
    [InlineData("released-entered")]
    public async Task ImpossibleHistoricalActionGraphRejectsBeforeClaim(string mutation)
    {
        var source = await PartialRecoveryTests.Capture(); var (result, sink, _) = await PartialRecoveryTests.Resume(source);
        var original = sink.CopyRestrictedContext(); var node = JsonNode.Parse(original.CopyRestrictedPayload())!;
        var actions = node["Rounds"]![1]!["RecoveryActions"]!;
        switch (mutation)
        {
            case "duplicate-action": actions[1]!["ActionId"] = actions[0]!["ActionId"]!.DeepClone(); break;
            case "completed-member": actions[0]!["MemberIndex"] = 0; break;
            case "wrong-call": actions[0]!["Result"]!["Call"]!["CallId"] = "FOREIGN_CALL_CANARY"; break;
            case "wrong-kind": actions[0]!["Kind"] = 1; break;
            case "duplicate-operation": node["Members"]![1]!["Operation"]!["OperationId"] = node["Members"]![0]!["Operation"]!["OperationId"]!.DeepClone(); break;
            case "old-usage": node["Rounds"]![0]!["Usage"]!["ToolInvocations"]!["Invoked"] = 1; break;
            case "current-usage": node["Rounds"]![1]!["Usage"]!["ToolInvocations"]!["Invoked"] = 1; break;
            case "released-entered": node["Rounds"]![1]!["RecoveryReleased"] = new JsonArray(1); break;
        }
        var altered = new AgentContextEnvelope(original.ImplementationId, original.FormatVersion, original.CompatibilityVersion, JsonSerializer.SerializeToUtf8Bytes(node));
        var authority = new SelectedAuthority(altered, result.Checkpoint!); var provider = new PersistentProvider(tools: true); var next = ToolContextFixture.Request();
        var denied = await ToolContextFixture.Agent(provider, source.Tools, authority).ExecuteWithContextAsync(
            new(next, ContextExecutionIntent.NewRunFromContext, altered, OrdinaryFixture.Grant(result, next.ExecutionId)));
        Assert.Equal(ContextAdmission.Rejected, denied.Admission); Assert.Equal(0, authority.Claims); Assert.Equal(0, provider.Imports);
        Assert.Equal(1, source.Recovery.Invocations); Assert.Equal(1, source.Recovery.Queries);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedRecoveryRetainsCursorChargesAndFrozenQueryFacts(bool queryEntered)
    {
        var source = await PartialRecoveryTests.Capture(); var clock = new ControlledTimeProvider();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (queryEntered) source.Recovery.BeforeQuery = (_, _) => { clock.Advance(TimeSpan.FromSeconds(31)); return new(release.Task); };
        else clock.TimerCreated = _ => { clock.TimerCreated = null; clock.Advance(TimeSpan.FromSeconds(31)); };
        var (middle, sink, provider) = await PartialRecoveryTests.Resume(source, options: new(timeProvider: clock, requireContinuation: true));
        Assert.Equal(AgentTerminationReason.ResourceLimit, middle.Outcome!.Reason); Assert.Equal(queryEntered ? 1 : 0, middle.Outcome.Usage!.ToolInvocations!.Invoked);
        Assert.Equal(queryEntered ? 1 : 0, middle.Outcome.Usage.ToolInvocations.ReleasedUnstarted); Assert.Equal(0, provider.Effects);
        Assert.Equal(ContextCaptureStatus.Delivered, middle.CaptureStatus); var captured = sink.CopyRestrictedContext(); var frozen = captured.CopyRestrictedPayload();
        source.Recovery.BeforeQuery = null;
        var (last, _, _) = await PartialRecoveryTests.Resume(source with { Envelope = captured, Result = middle });
        Assert.Equal(AgentTerminationReason.Completed, last.Outcome!.Reason); Assert.Equal(2, last.History.Count);
        Assert.Equal(queryEntered ? 3 : 2, last.History.Sum(r => r.Usage.ToolInvocations!.Invoked));
        Assert.Equal(2, last.Outcome.Usage!.ToolInvocations!.Invoked); release.TrySetResult(); Assert.Equal(frozen, captured.CopyRestrictedPayload());
    }
    [Fact]
    public async Task RevokedPermissionDuringQueryCannotAdmitResultOrDependentEffect()
    {
        var source = await PartialRecoveryTests.Capture();
        source.Recovery.BeforeQuery = (_, _) => { source.Recovery.Allowed = false; return ValueTask.CompletedTask; };
        var (result, _, provider) = await PartialRecoveryTests.Resume(source);
        Assert.Equal(AgentTerminationReason.Failed, result.Outcome!.Reason); Assert.Equal(1, result.Outcome.Usage!.ToolInvocations!.Invoked);
        Assert.Equal(0, provider.Effects); Assert.Equal(1, source.Counter.Effects); Assert.Equal(1, source.Recovery.Invocations);
    }
    [Fact]
    public async Task AChargedQueryLeavesZeroRemainingAllowanceForAnotherToolBatch()
    {
        var source = await PartialRecoveryTests.Capture(suffix: false);
        var (result, _, provider) = await PartialRecoveryTests.Resume(source, allowance: 1, configure: p => p.Respond = r =>
            new(r.Scope, r.Attempt, ProviderFinish.ToolCalls, null, [new("next", "counter", "{\"amount\":1}")], new(r.Scope, r.Attempt, [2])));
        Assert.Equal(AgentTerminationReason.ResourceLimit, result.Outcome!.Reason); Assert.Equal(1, result.Outcome.Usage!.ToolInvocations!.Invoked);
        Assert.Equal(1, source.Recovery.Queries); Assert.Equal(1, source.Recovery.Invocations); Assert.Equal(1, source.Counter.Effects);
        Assert.Equal(1, provider.Effects);
    }
    private sealed class DenySuffix(IFunctionTool inner) : IFunctionTool
    {
        public ToolDescriptor Descriptor => inner.Descriptor;
        public ToolPreparation Prepare(ToolCall call) => inner.Prepare(call);
        public ToolError ValidateInvocation(PreparedToolInvocation p, ToolCall call, IToolCapability? cap) =>
            call.CallId == "last" ? ToolError.UnsupportedCapability : inner.ValidateInvocation(p, call, cap);
        public ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation p, ToolCall call, IToolCapability? cap, CancellationToken token = default) => inner.InvokeAsync(p, call, cap, token);
    }
    [Fact]
    public async Task LaterSuffixPermissionFailurePreventsEarlierRecoveryQuery()
    {
        var source = await PartialRecoveryTests.Capture();
        var tools = PartialRecoveryTests.Bindings(source.Counter, source.Recovery);
        tools[0] = new(new DenySuffix(new CounterTool(maximumResultBytes: 100)), source.Counter);
        var (result, _, provider) = await PartialRecoveryTests.Resume(source, tools: tools);
        Assert.Equal(ContextAdmission.Supplied, result.Admission); Assert.Equal(AgentTerminationReason.Failed, result.Outcome!.Reason);
        Assert.Equal(0, result.Outcome.Usage!.ToolInvocations!.Invoked); Assert.Equal(0, source.Recovery.Queries);
        Assert.Equal(0, provider.Effects); Assert.Equal(1, source.Counter.Effects);
    }
}

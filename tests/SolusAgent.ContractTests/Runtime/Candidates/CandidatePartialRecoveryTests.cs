using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CustomTools;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.ContractTests.Runtime.Tools;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Context;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Candidates;

public sealed partial class CandidatePartialRecoveryTests
{
    private const string Correction = "PARTIAL_CANDIDATE_CORRECTION";
    private static readonly ProviderExchangeBounds Bounds = new(maximumResponseBytes: 512);
    private sealed record Source(ContextCandidateExecutionResult Result, AgentContextEnvelope Envelope,
        CounterCapability Counter, RecoveryCounterCapability Recovery, ScriptedCandidateHost Host, RuntimeToolRegistration[] Tools);

    private static IContextCandidateAgent Agent(PersistentProvider provider, RuntimeToolRegistration[] tools,
        IRuntimeContextAuthority? authority = null, RuntimeOptions? options = null) =>
        (IContextCandidateAgent)RuntimeAgentFactory.Create(new(provider, tools, new RuntimeHooks(), Bounds, contextAuthority: authority),
            options ?? new(requireContinuation: true));

    private static async Task<Source> Capture(ToolRecoveryGuarantee guarantee = ToolRecoveryGuarantee.QueryOutcome)
    {
        var counter = new CounterCapability();
        var recovery = new RecoveryCounterCapability("CANDIDATE_RECOVERY_TARGET", guarantee) { FailAfterEffect = true };
        var tools = PartialRecoveryTests.Bindings(counter, recovery);
        var provider = new PersistentProvider(tools: true)
        { Respond = r => r.Inputs.Any(i => i.Model is not null) ? PartialRecoveryTests.Calls(r) : ToolContextFixture.Final(r) };
        var host = CandidateContextFixture.Host(CandidateDecision.Reject, CandidateContinuation.Continue, Correction);
        var sink = new RestrictedContextHost();
        var result = await Agent(provider, tools).ExecuteCandidatesWithContextAsync(
            new(CandidateContextFixture.Request(limits: new(maximumToolInvocations: 3)), ContextExecutionIntent.Fresh), new(2, 1, 0), host, sink);
        Assert.Equal(ContextCaptureStatus.Delivered, result.Context.CaptureStatus);
        Assert.Equal(1, result.Candidates!.RepairsAdmitted); Assert.Single(host.Submissions);
        Assert.Equal(2, result.Context.Outcome!.Usage!.ToolInvocations!.Invoked);
        recovery.FailAfterEffect = false;
        return new(result, sink.CopyRestrictedContext(), counter, recovery, host, tools);
    }

    private static async Task<(ContextCandidateExecutionResult Result, RestrictedContextHost Sink, PersistentProvider Provider, ScriptedCandidateHost Host)>
        Resume(Source source, int allowance = 2, RuntimeOptions? options = null, IProgress<AgentProgress>? progress = null,
            CancellationToken token = default, IRuntimeContextAuthority? authority = null)
    {
        var provider = new PersistentProvider(tools: true) { Respond = ToolContextFixture.Final };
        var host = CandidateContextFixture.Host(); var sink = new RestrictedContextHost();
        var request = CandidateContextFixture.Request(limits: new(maximumToolInvocations: allowance));
        var result = await Agent(provider, source.Tools, authority ?? new SelectedAuthority(source.Envelope, source.Result.Context.Checkpoint!), options)
            .ExecuteCandidatesWithContextAsync(CandidateContextFixture.Resume(source.Result, source.Envelope, request), new(1, 0, 0), host, sink, progress, token);
        return (result, sink, provider, host);
    }

    [Theory]
    [InlineData(ToolRecoveryGuarantee.QueryOutcome)]
    [InlineData(ToolRecoveryGuarantee.ConcurrentSafeRepeat)]
    public async Task AdmittedRepairRecoversWithoutCorrectionOrAdmissionDuplication(ToolRecoveryGuarantee guarantee)
    {
        var source = await Capture(guarantee); var frozen = source.Envelope.CopyRestrictedPayload();
        var (result, sink, provider, host) = await Resume(source);
        Assert.Equal(ContextAdmission.Supplied, result.Context.Admission); Assert.Equal(CandidateStopReason.Completed, result.Candidates!.StopReason);
        Assert.Equal(0, result.Candidates.RepairsAdmitted); Assert.Equal(0, result.Candidates.ContinuationsAdmitted);
        Assert.Single(result.Candidates.HistoricalReceipts); Assert.Single(result.Candidates.Receipts);
        Assert.Equal(source.Host.Submissions[0].SubmissionId, Assert.Single(host.Submissions).RepairsSubmissionId);
        Assert.Single(provider.LastRequest!.Inputs, i => i.Text == Correction);
        Assert.Equal(new[] { "first", "uncertain", "last" }, provider.LastRequest.Inputs.Where(i => i.ToolResult is not null).Select(i => i.ToolResult!.Call.CallId));
        Assert.Equal(2, result.Candidates.Outcome.Usage!.ToolInvocations!.Invoked);
        Assert.Equal(2, Assert.Single(result.Context.History).Usage.ToolInvocations!.Invoked);
        Assert.Equal(1, provider.Effects); Assert.Equal(2, source.Counter.Effects); Assert.Equal(1, source.Recovery.Effects);
        Assert.Equal(guarantee == ToolRecoveryGuarantee.QueryOutcome ? 1 : 0, source.Recovery.Queries);
        Assert.Equal(guarantee == ToolRecoveryGuarantee.QueryOutcome ? 1 : 2, source.Recovery.Invocations);
        Assert.Equal(frozen, source.Envelope.CopyRestrictedPayload());
        // The resulting history is itself consumable without another provider/tool/Host effect.
        var next = await Resume(source with { Result = result, Envelope = sink.CopyRestrictedContext() });
        Assert.Equal(ContextAdmission.Supplied, next.Result.Context.Admission); Assert.Equal(0, next.Provider.Effects); Assert.Empty(next.Host.Submissions);
        Assert.Equal(2, next.Result.Context.History.Count); Assert.Equal(1, source.Recovery.Effects);
    }

    [Theory]
    [InlineData(RecoveryQueryBehavior.Unresolved)] [InlineData(RecoveryQueryBehavior.WrongOperation)]
    [InlineData(RecoveryQueryBehavior.WrongCall)] [InlineData(RecoveryQueryBehavior.InvalidJson)]
    [InlineData(RecoveryQueryBehavior.Oversized)] [InlineData(RecoveryQueryBehavior.PriorFailure)] [InlineData(RecoveryQueryBehavior.Throw)]
    public async Task QueryFailureRecapturesHonestFactsAndNeverRunsDependentWork(RecoveryQueryBehavior behavior)
    {
        var source = await Capture(); source.Recovery.QueryBehavior = behavior;
        var (result, sink, provider, host) = await Resume(source);
        Assert.Equal(ContextAdmission.Supplied, result.Context.Admission); Assert.Equal(CandidateStopReason.ProductionFailed, result.Candidates!.StopReason);
        Assert.Equal(1, source.Recovery.Queries); Assert.Equal(1, source.Recovery.Invocations); Assert.Equal(1, source.Recovery.Effects);
        Assert.Equal(1, source.Counter.Effects); Assert.Equal(0, provider.Effects); Assert.Empty(host.Submissions);
        Assert.Equal(1, result.Context.Outcome!.Usage!.ToolInvocations!.Invoked); Assert.Equal(1, result.Context.Outcome.Usage.ToolInvocations.ReleasedUnstarted);
        Assert.Empty(result.Context.Outcome.Usage.Attempts); Assert.Equal(ContextCaptureStatus.Delivered, result.Context.CaptureStatus);
        source.Recovery.QueryBehavior = RecoveryQueryBehavior.Normal;
        var next = await Resume(source with { Result = result, Envelope = sink.CopyRestrictedContext() });
        Assert.Equal(behavior == RecoveryQueryBehavior.PriorFailure ? ContextAdmission.Rejected : ContextAdmission.Supplied, next.Result.Context.Admission);
        if (behavior != RecoveryQueryBehavior.PriorFailure)
        {
            Assert.Equal(CandidateStopReason.Completed, next.Result.Candidates!.StopReason);
            Assert.Single(next.Provider.LastRequest!.Inputs, i => i.Text == Correction);
            Assert.Equal(0, next.Result.Candidates.RepairsAdmitted);
            Assert.Equal(2, next.Result.Context.History.Count);
        }
        Assert.Equal(1, source.Recovery.Effects);
    }

    [Theory]
    [InlineData(ToolRecoveryGuarantee.QueryOutcome)] [InlineData(ToolRecoveryGuarantee.ConcurrentSafeRepeat)]
    public async Task InsufficientWholeBatchAllowanceAllowsNoRecoveryEntry(ToolRecoveryGuarantee guarantee)
    {
        var source = await Capture(guarantee);
        var (result, sink, provider, host) = await Resume(source, allowance: 1);
        Assert.Equal(ContextAdmission.Supplied, result.Context.Admission); Assert.Equal(CandidateStopReason.RuntimeLimit, result.Candidates!.StopReason);
        Assert.Equal(0, result.Context.Outcome!.Usage!.ToolInvocations!.Invoked); Assert.Equal(0, source.Recovery.Queries);
        Assert.Equal(1, source.Recovery.Invocations); Assert.Equal(1, source.Counter.Effects); Assert.Equal(0, provider.Effects); Assert.Empty(host.Submissions);
        Assert.Equal(ContextCaptureStatus.Delivered, result.Context.CaptureStatus);
        var next = await Resume(source with { Result = result, Envelope = sink.CopyRestrictedContext() });
        Assert.Equal(CandidateStopReason.Completed, next.Result.Candidates!.StopReason); Assert.Equal(0, next.Result.Candidates.RepairsAdmitted);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RecoveryOnlyCutRetainsTheSameEpisodeForAnotherAuthorizedRound(bool deadline)
    {
        var source = await Capture(); var entered = RuntimeFixture.Barrier(); var release = RuntimeFixture.Barrier();
        var clock = new ControlledTimeProvider(); using var cancel = new CancellationTokenSource();
        source.Recovery.BeforeQuery = (_, _) => { entered.TrySetResult(); return new(release.Task); };
        var pending = Resume(source, options: new(timeProvider: clock, requireContinuation: true), token: cancel.Token);
        await RuntimeFixture.Await(entered.Task);
        if (deadline) clock.Advance(TimeSpan.FromSeconds(31)); else cancel.Cancel();
        var (result, sink, provider, host) = await RuntimeFixture.Await(pending);
        var bytes = sink.CopyRestrictedContext(); var frozen = bytes.CopyRestrictedPayload();
        Assert.Equal(deadline ? CandidateStopReason.DurationLimit : CandidateStopReason.Cancelled, result.Candidates!.StopReason);
        Assert.Empty(result.Context.Outcome!.Usage!.Attempts); Assert.Equal(1, result.Context.Outcome.Usage.ToolInvocations!.Invoked);
        Assert.Equal(0, provider.Effects); Assert.Empty(host.Submissions); Assert.Equal(ContextCaptureStatus.Delivered, result.Context.CaptureStatus);
        source.Recovery.BeforeQuery = null;
        var next = await Resume(source with { Result = result, Envelope = bytes });
        Assert.Equal(CandidateStopReason.Completed, next.Result.Candidates!.StopReason); Assert.Equal(0, next.Result.Candidates.RepairsAdmitted);
        release.TrySetResult(); await release.Task;
        Assert.Equal(frozen, bytes.CopyRestrictedPayload()); Assert.Equal(1, source.Recovery.Effects); Assert.Equal(2, source.Recovery.Queries);
        Assert.Single(next.Provider.LastRequest!.Inputs, i => i.Text == Correction);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CutBeforeFirstRecoveryEntryStillRecapturesRestoredFacts(bool deadline)
    {
        var source = await Capture(); var clock = new ControlledTimeProvider(); using var cancel = new CancellationTokenSource();
        if (deadline) clock.TimerCreated = _ => { clock.TimerCreated = null; clock.Advance(TimeSpan.FromSeconds(31)); };
        var authority = new AtClaim(new SelectedAuthority(source.Envelope, source.Result.Context.Checkpoint!), deadline ? null : cancel.Cancel);
        var (result, sink, provider, host) = await Resume(source, options: new(timeProvider: clock, requireContinuation: true), token: cancel.Token, authority: authority);
        Assert.Equal(deadline ? CandidateStopReason.DurationLimit : CandidateStopReason.Cancelled, result.Candidates!.StopReason);
        Assert.Equal(ContextCaptureStatus.Delivered, result.Context.CaptureStatus); Assert.Empty(result.Context.Outcome!.Usage!.Attempts);
        Assert.Equal(0, result.Context.Outcome.Usage.ToolInvocations!.Invoked); Assert.Equal(0, source.Recovery.Queries);
        Assert.Equal(0, provider.Effects); Assert.Empty(host.Submissions);
        var next = await Resume(source with { Result = result, Envelope = sink.CopyRestrictedContext() });
        Assert.Equal(CandidateStopReason.Completed, next.Result.Candidates!.StopReason); Assert.Equal(0, next.Result.Candidates.RepairsAdmitted);
    }

    [Fact]
    public async Task ObserverCutAfterRecoveryCommitsNoProviderAndReusesAllResultsLater()
    {
        var source = await Capture();
        var (result, sink, provider, host) = await Resume(source, progress: new ThrowingProgress());
        Assert.Equal(CandidateStopReason.ProgressObserverFailed, result.Candidates!.StopReason);
        Assert.Equal(0, provider.Effects); Assert.Empty(host.Submissions); Assert.Equal(2, source.Counter.Effects);
        Assert.Equal(2, result.Context.Outcome!.Usage!.ToolInvocations!.Invoked); Assert.Equal(ContextCaptureStatus.Delivered, result.Context.CaptureStatus);
        var next = await Resume(source with { Result = result, Envelope = sink.CopyRestrictedContext() });
        Assert.Equal(CandidateStopReason.Completed, next.Result.Candidates!.StopReason);
        Assert.Equal(0, next.Result.Context.Outcome!.Usage!.ToolInvocations!.Invoked); Assert.Equal(1, source.Recovery.Queries); Assert.Equal(2, source.Counter.Effects);
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(1)]
    public async Task CandidateAndRecoveryRecordCapacityIsAdmittedBeforeClaim(int delta)
    {
        var source = await Capture();
        // Six current records + one unstarted result + two recovery actions + three original
        // candidate records + model/submission/prospective follow-on = fifteen retained records.
        var authority = new SelectedAuthority(source.Envelope, source.Result.Context.Checkpoint!);
        var (result, _, provider, host) = await Resume(source, options: new(maximumRecords: 15 + delta, requireContinuation: true), authority: authority);
        Assert.Equal(delta < 0 ? ContextAdmission.Rejected : ContextAdmission.Supplied, result.Context.Admission);
        Assert.Equal(delta < 0 ? 0 : 1, authority.Claims); Assert.Equal(delta < 0 ? 0 : 1, provider.Imports);
        Assert.Equal(delta < 0 ? 0 : 1, source.Recovery.Queries); Assert.Equal(delta < 0 ? 1 : 2, source.Counter.Effects);
        Assert.Equal(delta < 0 ? 0 : 1, host.Submissions.Count);
        if (delta >= 0) Assert.Equal(CandidateStopReason.Completed, result.Candidates!.StopReason);
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(1)]
    public async Task ResponseAndCandidateGrowthBytesAreReservedBeforeRecovery(int delta)
    {
        var source = await Capture(); var request = CandidateContextFixture.Request(limits: new(maximumToolInvocations: 2));
        var resume = CandidateContextFixture.Resume(source.Result, source.Envelope, request);
        var saved = JsonSerializer.Deserialize<OrdinaryCheckpoint>(source.Envelope.CopyRestrictedPayload())!;
        static int Bytes(string? text) => Encoding.UTF8.GetByteCount(text ?? "");
        var definitions = source.Tools.Sum(t => Bytes(t.Descriptor.Name) + Bytes(t.Descriptor.Description) + Bytes(t.Descriptor.CapabilityId)
            + Bytes(t.Descriptor.InputSchema.NormalizedJson) + Bytes(t.Descriptor.ResultSchema.NormalizedJson));
        var recovery = saved.Members.Select((m, i) => (m, i)).Where(p => p.m.State != SolusAgent.Runtime.Tools.ToolMemberState.Succeeded)
            .Sum(p => OrdinaryContextCodec.RecoveryReservationBytes(p.i, p.m.Call, source.Tools.Single(t => t.Descriptor.Name == p.m.Call.ToolName).Descriptor));
        var id = Guid.NewGuid(); var ledger = saved.Candidates!;
        var prospective = ledger with
        {
            Submissions = [.. ledger.Submissions, new(ledger.Episodes[^1].Id, request.ExecutionId, id, 7, true, resume.RoundGrant!.RoundId,
                new(request.ExecutionId, id, CandidateAcknowledgement.Acknowledged, CandidateDecision.Reject, CandidateContinuation.Continue), MaximumEscapingCorrection)],
            Episodes = [.. ledger.Episodes, new(Guid.NewGuid(), ledger.Episodes[^1].WorkId, id, null, null, null)]
        };
        var required = source.Envelope.PayloadByteCount + definitions + recovery + Bounds.MaximumResponseBytes
            + OrdinaryContextCodec.CandidateBytes(prospective) - OrdinaryContextCodec.CandidateBytes(ledger);
        var authority = new SelectedAuthority(source.Envelope, source.Result.Context.Checkpoint!);
        var provider = new PersistentProvider(tools: true) { Respond = ToolContextFixture.Final }; var host = CandidateContextFixture.Host();
        var result = await Agent(provider, source.Tools, authority, new(maximumRetainedBytes: required + delta, requireContinuation: true))
            .ExecuteCandidatesWithContextAsync(resume, new(1, 0, 0), host);
        Assert.Equal(delta < 0 ? ContextAdmission.Rejected : ContextAdmission.Supplied, result.Context.Admission);
        Assert.Equal(delta < 0 ? 0 : 1, authority.Claims); Assert.Equal(delta < 0 ? 0 : 1, provider.Imports);
        Assert.Equal(delta < 0 ? 0 : 1, source.Recovery.Queries); Assert.Equal(delta < 0 ? 1 : 2, source.Counter.Effects);
        if (delta >= 0) Assert.Equal(CandidateStopReason.Completed, result.Candidates!.StopReason);
    }

    private sealed class AtClaim(IRuntimeContextAuthority inner, Action? after) : IRuntimeContextAuthority
    { public bool TryClaim(AgentContextEnvelope context, ContextRoundGrant grant) { var claimed = inner.TryClaim(context, grant); if (claimed) after?.Invoke(); return claimed; } }
    private sealed class ThrowingProgress : IProgress<AgentProgress>
    { public void Report(AgentProgress value) => throw new InvalidOperationException("RECOVERY_OBSERVER_CANARY"); }
}

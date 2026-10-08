using System.Text.Json;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Candidates;

public sealed class CutAndIsolationTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task CooperativeHostCancellationAtRunCutRetainsUnknownAndEarlierAcceptance(bool cancel, bool throwSynchronously)
    {
        var clock = new ControlledTimeProvider(); using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var host = new ScriptedCandidateHost((s, token) =>
        {
            if (++calls == 1) return CandidateFixture.Feedback(s, instruction: CandidateContinuation.Continue);
            if (cancel) cancellation.Cancel(); else clock.Advance(TimeSpan.FromSeconds(10));
            Assert.True(token.IsCancellationRequested);
            if (throwSynchronously) throw new OperationCanceledException(token);
            return ValueTask.FromCanceled<CandidateFeedback?>(token);
        });
        var provider = CandidateFixture.Provider();
        var result = await CandidateFixture.Agent(provider, options: new(clock)).ExecuteCandidatesAsync(CandidateFixture.Request(), host,
            cancellationToken: cancellation.Token);
        Assert.Equal(cancel ? CandidateStopReason.Cancelled : CandidateStopReason.DurationLimit, result.StopReason);
        Assert.Equal(cancel ? AgentTerminationReason.Cancelled : AgentTerminationReason.ResourceLimit, result.Outcome.Reason);
        Assert.Equal(1, result.AcceptedCount); Assert.Equal(2, result.Receipts.Count);
        Assert.Equal(CandidateAcknowledgement.Unknown, result.Receipts[1].Acknowledgement);
        Assert.Equal(1, result.ContinuationsAdmitted); Assert.Equal(0, result.RepairsAdmitted);
        Assert.Equal(2, result.Outcome.CompletedWorkUnits); Assert.Equal(2, result.Outcome.Usage!.Attempts.Count);
        Assert.Equal(2, provider.Effects); Assert.Equal(2, host.Submissions.Count);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HostOwnCancellationWithoutRunCutRemainsFailedAcknowledgement(bool throwSynchronously)
    {
        using var hostCancellation = new CancellationTokenSource(); hostCancellation.Cancel();
        var host = new ScriptedCandidateHost((_, token) =>
        {
            Assert.False(token.IsCancellationRequested);
            if (throwSynchronously) throw new OperationCanceledException(hostCancellation.Token);
            return ValueTask.FromCanceled<CandidateFeedback?>(hostCancellation.Token);
        });
        var provider = CandidateFixture.Provider();
        var result = await CandidateFixture.Agent(provider).ExecuteCandidatesAsync(CandidateFixture.Request(), host);
        Assert.Equal(CandidateStopReason.FailedAcknowledgement, result.StopReason);
        Assert.Equal(AgentTerminationReason.Failed, result.Outcome.Reason);
        Assert.Equal(CandidateAcknowledgement.Failed, result.Receipts.Single().Acknowledgement);
        Assert.Equal(1, result.Outcome.CompletedWorkUnits); Assert.Single(result.Outcome.Usage!.Attempts);
        Assert.Equal(1, provider.Effects); Assert.Single(host.Submissions);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task PendingHostCutReturnsBeforeReleaseAndLateFeedbackOrFaultCannotMutate(bool cancel, bool lateFault)
    {
        var clock = new ControlledTimeProvider(); using var cancellation = new CancellationTokenSource();
        var entered = RuntimeFixture.Barrier<CandidateSubmission>(); var feedback = RuntimeFixture.Barrier<CandidateFeedback?>();
        var hostCalls = 0; ScriptedCandidateHost? host = null;
        host = new((s, _) =>
        {
            host!.ApplyEffect();
            if (++hostCalls == 1) return CandidateFixture.Feedback(s, instruction: CandidateContinuation.Continue);
            entered.SetResult(s); return new(feedback.Task);
        });
        var provider = CandidateFixture.Provider();
        var pending = CandidateFixture.Agent(provider, options: new(clock)).ExecuteCandidatesAsync(CandidateFixture.Request(), host,
            cancellationToken: cancellation.Token).AsTask();
        var submission = await RuntimeFixture.Await(entered.Task);
        if (cancel) cancellation.Cancel(); else clock.Advance(TimeSpan.FromSeconds(10));
        var result = await RuntimeFixture.Await(pending);
        Assert.False(feedback.Task.IsCompleted); Assert.Equal(cancel ? CandidateStopReason.Cancelled : CandidateStopReason.DurationLimit, result.StopReason);
        Assert.Equal(cancel ? AgentTerminationReason.Cancelled : AgentTerminationReason.ResourceLimit, result.Outcome.Reason);
        Assert.Equal(1, result.AcceptedCount); Assert.Equal(CandidateAcknowledgement.Unknown, result.Receipts[^1].Acknowledgement);
        Assert.Equal(2, result.Outcome.CompletedWorkUnits); Assert.Equal(2, result.Outcome.Usage!.Attempts.Count);
        Assert.Equal(1, result.ContinuationsAdmitted); Assert.Equal(2, host.Effects); Assert.Equal(2, provider.Effects);
        var before = JsonSerializer.Serialize(result);
        if (lateFault) feedback.SetException(new InvalidOperationException("LATE_EXCEPTION_CANARY"));
        else feedback.SetResult(await CandidateFixture.Feedback(submission));
        Assert.Equal(before, JsonSerializer.Serialize(result)); Assert.Equal(2, provider.Effects); Assert.Equal(2, host.Submissions.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<CandidateReceipt>)result.Receipts).Add(result.Receipts[0]));
    }

    [Theory]
    [InlineData(false, CandidateContinuation.Continue)] [InlineData(true, CandidateContinuation.Continue)]
    [InlineData(false, CandidateContinuation.End)] [InlineData(true, CandidateContinuation.End)]
    public async Task AlreadyReturnedAcknowledgementIsRetainedButCutForbidsFurtherEffects(bool cancel, CandidateContinuation instruction)
    {
        var clock = new ControlledTimeProvider(); using var cancellation = new CancellationTokenSource();
        var host = new ScriptedCandidateHost((s, _) =>
        {
            if (cancel) cancellation.Cancel(); else clock.Advance(TimeSpan.FromSeconds(10));
            return CandidateFixture.Feedback(s, instruction: instruction);
        });
        var provider = CandidateFixture.Provider();
        var result = await CandidateFixture.Agent(provider, options: new(clock)).ExecuteCandidatesAsync(CandidateFixture.Request(), host,
            cancellationToken: cancellation.Token);
        Assert.Equal(cancel ? CandidateStopReason.Cancelled : CandidateStopReason.DurationLimit, result.StopReason);
        Assert.True(result.Receipts.Single().IsAccepted); Assert.Equal(instruction, result.Receipts[0].Continuation);
        Assert.Equal(0, result.ContinuationsAdmitted); Assert.Equal(1, provider.Effects); Assert.Single(host.Submissions);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CutDuringOrdinaryProgressPreventsUnsentHostReceipt(bool cancel)
    {
        var clock = new ControlledTimeProvider(); using var cancellation = new CancellationTokenSource();
        var provider = CandidateFixture.Provider(); var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s));
        var result = await CandidateFixture.Agent(provider, options: new(clock)).ExecuteCandidatesAsync(CandidateFixture.Request(), host,
            new InlineProgress(_ => { if (cancel) cancellation.Cancel(); else clock.Advance(TimeSpan.FromSeconds(10)); }), cancellation.Token);
        Assert.Equal(cancel ? CandidateStopReason.Cancelled : CandidateStopReason.DurationLimit, result.StopReason);
        Assert.Equal(1, result.Outcome.CompletedWorkUnits); Assert.Empty(result.Receipts); Assert.Empty(host.Submissions);
    }

    [Theory]
    [InlineData("provider", false)] [InlineData("provider", true)]
    [InlineData("exposure", false)] [InlineData("exposure", true)]
    [InlineData("settlement", false)] [InlineData("settlement", true)]
    public async Task SharedWholeRunCutAtEveryAsyncProductionStagePreservesEarlierReceipt(string stage, bool cancel)
    {
        var clock = new ControlledTimeProvider(); using var cancellation = new CancellationTokenSource();
        var entered = RuntimeFixture.Barrier(); var response = RuntimeFixture.Barrier<ProviderResponse>();
        var permission = RuntimeFixture.Barrier<ExposureAcknowledgement?>(); var settlement = RuntimeFixture.Barrier<SettlementAcknowledgement?>();
        var hooks = new RuntimeHooks(); ProviderRequest? heldRequest = null; RuntimeExposure? heldExposure = null; RuntimeSettlement? heldSettlement = null;
        if (stage == "exposure") hooks.Before = (e, _) =>
        {
            if (hooks.Exposures.Count == 1) return ValueTask.FromResult<ExposureAcknowledgement?>(RuntimeHooks.Permit(e));
            heldExposure = e; entered.SetResult(); return new(permission.Task);
        };
        if (stage == "settlement") hooks.After = (s, _) =>
        {
            if (hooks.Settlements.Count == 1) return ValueTask.FromResult<SettlementAcknowledgement?>(RuntimeHooks.Continue(s));
            heldSettlement = s; entered.SetResult(); return new(settlement.Task);
        };
        var provider = new ScriptedProvider([ScriptedProvider.Final, (r, o, _) =>
        {
            o.CaptureUsage(new(7, null));
            if (stage != "provider") return ValueTask.FromResult(RuntimeFixture.Final(r));
            heldRequest = r; entered.SetResult(); return new(response.Task);
        }]);
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s, instruction: CandidateContinuation.Continue));
        var run = CandidateFixture.Agent(provider, hooks, new(clock)).ExecuteCandidatesAsync(CandidateFixture.Request(), host,
            cancellationToken: cancellation.Token).AsTask();
        await RuntimeFixture.Await(entered.Task);
        if (cancel) cancellation.Cancel(); else clock.Advance(TimeSpan.FromSeconds(10));
        if (stage == "settlement") clock.Advance(TimeSpan.FromSeconds(1));
        var result = await RuntimeFixture.Await(run);
        Assert.Equal(cancel ? CandidateStopReason.Cancelled : CandidateStopReason.DurationLimit, result.StopReason);
        Assert.Equal(1, result.AcceptedCount); Assert.Single(result.Receipts); Assert.Single(host.Submissions);
        Assert.Equal(1, result.ContinuationsAdmitted); Assert.Equal(2, result.Outcome.Usage!.Attempts.Count);
        Assert.Equal(stage == "settlement" ? 2 : 1, result.Outcome.CompletedWorkUnits);
        Assert.Equal(stage == "exposure" ? null : 7L, result.Outcome.Usage.Attempts[1].Usage.InputTokens);
        var snapshot = JsonSerializer.Serialize(result);
        if (stage == "provider") { Assert.False(response.Task.IsCompleted); response.SetResult(RuntimeFixture.Final(heldRequest!)); }
        if (stage == "exposure") { Assert.False(permission.Task.IsCompleted); permission.SetResult(RuntimeHooks.Permit(heldExposure!)); }
        if (stage == "settlement") { Assert.False(settlement.Task.IsCompleted); settlement.SetResult(RuntimeHooks.Continue(heldSettlement!)); }
        Assert.Equal(snapshot, JsonSerializer.Serialize(result)); Assert.Single(host.Submissions);
    }

    [Fact]
    public async Task EqualExecutionIdsAndSwappedFeedbackCannotCrossRunsOnSharedAgent()
    {
        var aEntered = RuntimeFixture.Barrier<CandidateSubmission>(); var bEntered = RuntimeFixture.Barrier<CandidateSubmission>();
        var aFeedback = RuntimeFixture.Barrier<CandidateFeedback?>(); var bFeedback = RuntimeFixture.Barrier<CandidateFeedback?>();
        var aHost = new ScriptedCandidateHost((s, _) => { aEntered.SetResult(s); return new(aFeedback.Task); });
        var bHost = new ScriptedCandidateHost((s, _) => { bEntered.SetResult(s); return new(bFeedback.Task); });
        var provider = CandidateFixture.Provider(); var agent = CandidateFixture.Agent(provider); var executionId = Guid.NewGuid();
        var aRun = agent.ExecuteCandidatesAsync(CandidateFixture.Request(executionId: executionId), aHost).AsTask();
        var bRun = agent.ExecuteCandidatesAsync(CandidateFixture.Request(executionId: executionId), bHost).AsTask();
        var a = await RuntimeFixture.Await(aEntered.Task); var b = await RuntimeFixture.Await(bEntered.Task);
        Assert.NotEqual(a.SubmissionId, b.SubmissionId);
        aFeedback.SetResult(await CandidateFixture.Feedback(b)); bFeedback.SetResult(await CandidateFixture.Feedback(a));
        var aResult = await RuntimeFixture.Await(aRun); var bResult = await RuntimeFixture.Await(bRun);
        foreach (var result in new[] { aResult, bResult })
        { Assert.Equal(CandidateStopReason.MismatchedFeedback, result.StopReason); Assert.Equal(0, result.AcceptedCount); Assert.Single(result.Receipts); }
        Assert.Equal(a.SubmissionId, aResult.Receipts[0].SubmissionId); Assert.Equal(b.SubmissionId, bResult.Receipts[0].SubmissionId);
        Assert.NotEqual(aResult.Outcome.Usage!.Attempts[0].PhysicalAttemptId, bResult.Outcome.Usage!.Attempts[0].PhysicalAttemptId);
        Assert.NotEqual(aResult.Outcome.Usage.Attempts[0].LogicalCallId, bResult.Outcome.Usage.Attempts[0].LogicalCallId);
        Assert.Equal(2, provider.Effects);
    }

    [Fact]
    public async Task ConcurrentEqualIdsKeepTheirOwnCorrectionAndAcceptedHistory()
    {
        var aEntered = RuntimeFixture.Barrier<CandidateSubmission>(); var bEntered = RuntimeFixture.Barrier<CandidateSubmission>();
        var aFeedback = RuntimeFixture.Barrier<CandidateFeedback?>(); var bFeedback = RuntimeFixture.Barrier<CandidateFeedback?>();
        var aHost = Host(aEntered, aFeedback); var bHost = Host(bEntered, bFeedback);
        var steps = Enumerable.Repeat<Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>>>(
            (r, o, _) =>
            {
                o.CaptureUsage(new(3, 2)); var tag = r.Inputs[1].Text!;
                var repairing = r.Inputs.Any(input => input.Model is not null);
                if (repairing) { Assert.Equal("fix:" + tag, r.Inputs[^1].Text); Assert.Equal(ProviderInputKind.InputData, r.Inputs[^1].Kind); }
                return ValueTask.FromResult(RuntimeFixture.Final(r, (repairing ? "repaired:" : "draft:") + tag));
            }, 4).ToArray();
        var provider = new ScriptedProvider(steps); var agent = CandidateFixture.Agent(provider); var id = Guid.NewGuid();
        var aRun = agent.ExecuteCandidatesAsync(Request("A"), aHost).AsTask();
        var bRun = agent.ExecuteCandidatesAsync(Request("B"), bHost).AsTask();
        var a = await RuntimeFixture.Await(aEntered.Task); var b = await RuntimeFixture.Await(bEntered.Task);
        aFeedback.SetResult(await CandidateFixture.Feedback(a, CandidateDecision.Reject, CandidateContinuation.Continue, "fix:A"));
        bFeedback.SetResult(await CandidateFixture.Feedback(b, CandidateDecision.Reject, CandidateContinuation.Continue, "fix:B"));
        var aResult = await RuntimeFixture.Await(aRun); var bResult = await RuntimeFixture.Await(bRun);
        Assert.Equal(CandidateStopReason.Completed, aResult.StopReason); Assert.Equal(CandidateStopReason.Completed, bResult.StopReason);
        Assert.Equal("repaired:A", aHost.Submissions[1].Payload); Assert.Equal("repaired:B", bHost.Submissions[1].Payload);
        Assert.Equal(a.SubmissionId, aHost.Submissions[1].RepairsSubmissionId); Assert.Equal(b.SubmissionId, bHost.Submissions[1].RepairsSubmissionId);
        Assert.Equal(4, aResult.Outcome.Usage!.Attempts.Concat(bResult.Outcome.Usage!.Attempts).Select(x => x.PhysicalAttemptId).Distinct().Count());
        Assert.All(new[] { aResult, bResult }, r => { Assert.Equal(1, r.AcceptedCount); Assert.Equal(1, r.RepairsAdmitted); });
        CandidateExecutionRequest Request(string tag) => new(RuntimeFixture.Request(2,
            data: [new(AgentInputSource.Repository, tag)], executionId: id), new(2, 1, 0));
        static ScriptedCandidateHost Host(TaskCompletionSource<CandidateSubmission> entered, TaskCompletionSource<CandidateFeedback?> reply)
        {
            var count = 0;
            return new((s, _) => { if (++count != 1) return CandidateFixture.Feedback(s); entered.SetResult(s); return new(reply.Task); });
        }
    }
}

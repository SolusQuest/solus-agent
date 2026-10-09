using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using Xunit;
using Limits = SolusAgent.ContractTests.Runtime.Execution.DispatchLimitTests;

namespace SolusAgent.ContractTests.Runtime.Candidates;

public sealed class DispatchLimitTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RequiredSupportWithNullPoliciesAllowsCandidateCompletion(bool nullPolicy)
    {
        var provider = CandidateFixture.Provider(); var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s));
        var execution = nullPolicy ? RuntimeFixture.Request(required: AgentCapability.DispatchLimits) : Limits.Request();
        var result = await CandidateFixture.Agent(provider).ExecuteCandidatesAsync(new(execution, new(3, 2, 2)), host);
        Assert.Equal(CandidateStopReason.Completed, result.StopReason); Assert.Equal(1, result.Outcome.CompletedWorkUnits);
        Assert.Single(host.Submissions); Assert.Equal(1, provider.Effects);
    }

    [Theory]
    [InlineData(1, 3)] [InlineData(3, 1)] [InlineData(2, 2)]
    [InlineData(null, 1)] [InlineData(1, null)] [InlineData(null, 2)] [InlineData(2, null)]
    public async Task RepairAndContinuationUseNewLogicalWorkAndDenyOnlyNextProduction(int? logical, int? physical)
    {
        var guarded = CandidateFixture.Provider(); var provider = new InterfaceScriptedProvider(guarded.Scope, guarded.ExchangeAsync);
        var hooks = new RuntimeHooks(); var count = 0;
        var host = new ScriptedCandidateHost((s, _) => ++count == 1
            ? CandidateFixture.Feedback(s, CandidateDecision.Reject, CandidateContinuation.Continue, "repair data")
            : CandidateFixture.Feedback(s, instruction: CandidateContinuation.Continue));
        var result = await CandidateFixture.Agent(provider, hooks).ExecuteCandidatesAsync(new(Limits.Request(logical, physical), new(8, 7, 7)), host);
        var admitted = Math.Min(logical ?? int.MaxValue, physical ?? int.MaxValue);
        Assert.Equal(CandidateStopReason.RuntimeLimit, result.StopReason);
        Assert.Equal(AgentTerminationReason.ResourceLimit, result.Outcome.Reason);
        Assert.Equal(admitted, result.Outcome.CompletedWorkUnits); Assert.Equal(admitted, provider.Calls);
        Assert.Equal(admitted, guarded.Effects); Assert.Equal(admitted, hooks.Exposures.Count);
        Assert.Equal(admitted, hooks.Settlements.Count); Assert.Equal(admitted, host.Submissions.Count);
        Assert.Equal(admitted, result.Receipts.Count); Assert.Equal(admitted > 1 ? 1 : 0, result.RepairsAdmitted);
        Assert.Equal(0, result.ContinuationsAdmitted);
        var attempts = result.Outcome.Usage!.Attempts;
        Assert.Equal(UsageInventoryCoverage.Complete, result.Outcome.Usage.Coverage);
        Assert.Equal(admitted, attempts.Select(a => a.LogicalCallId).Distinct().Count());
        Assert.Equal(admitted, attempts.Select(a => a.PhysicalAttemptId).Distinct().Count());
        Assert.All(attempts, a => Assert.Equal(1, a.AttemptNumber));
        if (admitted > 1) Assert.Equal(host.Submissions[0].SubmissionId, host.Submissions[1].RepairsSubmissionId);
    }

    [Theory]
    [InlineData(DispatchExposure.NotDispatched, 2)]
    [InlineData(DispatchExposure.Unknown, 1)] [InlineData(DispatchExposure.Dispatched, 1)]
    public async Task OnlyFinalConfirmedNoSendAllowsPublicSlotReuse(DispatchExposure firstExposure, int expected)
    {
        var calls = 0;
        var guarded = new DelegateProvider(new("p", "m"), (r, o, _) =>
        {
            o.ObserveDispatch(++calls == 1 ? firstExposure : DispatchExposure.Dispatched);
            return ValueTask.FromResult(RuntimeFixture.Final(r));
        });
        var provider = new InterfaceScriptedProvider(guarded.Scope, guarded.ExchangeAsync); var hooks = new RuntimeHooks();
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s, instruction: CandidateContinuation.Continue));
        var result = await CandidateFixture.Agent(provider, hooks).ExecuteCandidatesAsync(new(Limits.Request(physical: 1), new(8, 7, 7)), host);
        Assert.Equal(CandidateStopReason.RuntimeLimit, result.StopReason);
        Assert.Equal(expected, provider.Calls); Assert.Equal(expected, hooks.Exposures.Count);
        Assert.Equal(expected, result.Outcome.CompletedWorkUnits); Assert.Equal(expected, host.Submissions.Count);
        Assert.Equal(expected, result.Outcome.Usage!.Attempts.Count);
        Assert.Equal(firstExposure, result.Outcome.Usage.Attempts[0].Exposure);
        Assert.All(result.Outcome.Usage.Attempts, a => Assert.Equal(UsageCompleteness.Unavailable, a.Usage.Completeness));
        if (expected == 2) Assert.Equal(DispatchExposure.Dispatched, result.Outcome.Usage.Attempts[1].Exposure);
    }

    [Theory]
    [InlineData("logical")] [InlineData("inventory")]
    public async Task PhysicalRefundDoesNotReleaseLogicalOrInventoryCapacity(string bound)
    {
        var guarded = new DelegateProvider(new("p", "m"), (r, o, _) =>
        { o.ObserveDispatch(DispatchExposure.NotDispatched); return ValueTask.FromResult(RuntimeFixture.Final(r)); });
        var provider = new InterfaceScriptedProvider(guarded.Scope, guarded.ExchangeAsync);
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s, instruction: CandidateContinuation.Continue));
        var result = await CandidateFixture.Agent(provider, options: new(maximumAttempts: bound == "inventory" ? 1 : 64))
            .ExecuteCandidatesAsync(new(Limits.Request(logical: bound == "logical" ? 1 : null, physical: 1), new(8, 7, 7)), host);
        Assert.Equal(CandidateStopReason.RuntimeLimit, result.StopReason); Assert.Equal(1, provider.Calls);
        Assert.Equal(1, result.Outcome.CompletedWorkUnits); Assert.Single(result.Outcome.Usage!.Attempts);
        Assert.Equal(DispatchExposure.NotDispatched, result.Outcome.Usage.Attempts[0].Exposure);
        Assert.Single(host.Submissions); Assert.Equal(0, result.ContinuationsAdmitted);
    }

    [Fact]
    public async Task FinalAtLastSlotStillDeliversAndCompletesCandidate()
    {
        var provider = CandidateFixture.Provider(); var hooks = new RuntimeHooks();
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s));
        var result = await CandidateFixture.Agent(provider, hooks).ExecuteCandidatesAsync(new(Limits.Request(1, 1), new(3, 2, 2)), host);
        Assert.Equal(CandidateStopReason.Completed, result.StopReason); Assert.Equal(1, result.AcceptedCount);
        Assert.Single(host.Submissions); Assert.Single(hooks.Settlements); Assert.Equal(1, provider.Effects);
    }

    [Fact]
    public async Task ConfiguredCountsAreEnforcedWithoutRequiringCapabilityFlag()
    {
        var guarded = CandidateFixture.Provider(); var provider = new InterfaceScriptedProvider(guarded.Scope, guarded.ExchangeAsync);
        var hooks = new RuntimeHooks(); var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s,
            instruction: CandidateContinuation.Continue));
        var execution = new AgentRequest(Guid.NewGuid(), "i", [], new(8, TimeSpan.FromSeconds(10)), AgentCapability.None, new(1, 1));
        var result = await CandidateFixture.Agent(provider, hooks).ExecuteCandidatesAsync(new(execution, new(8, 7, 7)), host);
        Assert.Equal(CandidateStopReason.RuntimeLimit, result.StopReason); Assert.Equal(1, provider.Calls);
        Assert.Single(hooks.Exposures); Assert.Single(host.Submissions); Assert.Single(result.Outcome.Usage!.Attempts);
    }

    [Fact]
    public async Task ConcurrentEqualExecutionIdsDoNotShareCountAllowancesOrRepairIdentity()
    {
        var aEntered = RuntimeFixture.Barrier<CandidateSubmission>(); var bEntered = RuntimeFixture.Barrier<CandidateSubmission>();
        var aFeedback = RuntimeFixture.Barrier<CandidateFeedback?>(); var bFeedback = RuntimeFixture.Barrier<CandidateFeedback?>();
        var aHost = Host(aEntered, aFeedback); var bHost = Host(bEntered, bFeedback);
        var guarded = CandidateFixture.Provider(); var provider = new InterfaceScriptedProvider(guarded.Scope, guarded.ExchangeAsync);
        var agent = CandidateFixture.Agent(provider); var id = Guid.NewGuid();
        var aRun = agent.ExecuteCandidatesAsync(new(Limits.Request(1, 1, id), new(3, 2, 2)), aHost).AsTask();
        var bRun = agent.ExecuteCandidatesAsync(new(Limits.Request(2, 2, id), new(3, 2, 2)), bHost).AsTask();
        var a = await RuntimeFixture.Await(aEntered.Task); var b = await RuntimeFixture.Await(bEntered.Task);
        aFeedback.SetResult(await CandidateFixture.Feedback(a, CandidateDecision.Reject, CandidateContinuation.Continue, "fix a"));
        bFeedback.SetResult(await CandidateFixture.Feedback(b, CandidateDecision.Reject, CandidateContinuation.Continue, "fix b"));
        var aResult = await RuntimeFixture.Await(aRun); var bResult = await RuntimeFixture.Await(bRun);
        Assert.Equal(CandidateStopReason.RuntimeLimit, aResult.StopReason); Assert.Equal(CandidateStopReason.Completed, bResult.StopReason);
        Assert.Single(aHost.Submissions); Assert.Equal(2, bHost.Submissions.Count);
        Assert.Equal(0, aResult.RepairsAdmitted); Assert.Equal(1, bResult.RepairsAdmitted);
        Assert.Equal(b.SubmissionId, bHost.Submissions[1].RepairsSubmissionId);
        var attempts = aResult.Outcome.Usage!.Attempts.Concat(bResult.Outcome.Usage!.Attempts).ToArray();
        Assert.Equal(3, provider.Calls); Assert.Equal(3, attempts.Length);
        Assert.Equal(3, attempts.Select(a => a.LogicalCallId).Distinct().Count());
        Assert.Equal(3, attempts.Select(a => a.PhysicalAttemptId).Distinct().Count());
        Assert.All(attempts, a => { Assert.Equal(id, a.ExecutionId); Assert.Equal(1, a.AttemptNumber); });
        static ScriptedCandidateHost Host(TaskCompletionSource<CandidateSubmission> entered, TaskCompletionSource<CandidateFeedback?> feedback)
        {
            var calls = 0;
            return new((s, _) => { if (++calls > 1) return CandidateFixture.Feedback(s); entered.SetResult(s); return new(feedback.Task); });
        }
    }
}

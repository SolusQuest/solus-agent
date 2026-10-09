using System.Text.Json;
using CustomTools;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Candidates;
using SolusAgent.ContractTests.Runtime.Tools;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Execution;

public sealed class TokenThresholdTests
{
    private static AgentRequest Request(AgentUsageLimits? limits, bool required = false, int units = 8) =>
        new(Guid.NewGuid(), "i", [], new(units, TimeSpan.FromSeconds(10)),
            required ? AgentCapability.UsageThresholds : AgentCapability.None, limits);
    private static Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>> Step(
        UsageObservation usage, string? toolId = null, bool invalid = false) => (r, o, _) =>
    {
        o.CaptureUsage(usage);
        return ValueTask.FromResult(new ProviderResponse(r.Scope,
            invalid ? new(r.Attempt.ExecutionId, Guid.NewGuid(), Guid.NewGuid()) : r.Attempt,
            toolId is null ? ProviderFinish.Final : ProviderFinish.ToolCalls, toolId is null ? "f" : null,
            toolId is null ? [] : [ToolFixture.Counter(toolId)]));
    };
    private static async Task<AgentOutcome> Execute(IAgent agent, AgentRequest request, bool candidates,
        IProgress<AgentProgress>? progress = null, CancellationToken cancellation = default) => candidates
        ? (await ((ICandidateAgent)agent).ExecuteCandidatesAsync(new(request, new(8, 8, 8)),
            new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s)), progress, cancellation)).Outcome
        : await agent.ExecuteAsync(request, progress, cancellation);
    private static IAgent Agent(ScriptedProvider provider, CounterCapability cap, RuntimeHooks? hooks = null, RuntimeOptions? options = null) =>
        RuntimeAgentFactory.Create(new(provider, [new(new CounterTool(maximumResultBytes: 64), cap)], hooks ?? new()), options);

    public static IEnumerable<object?[]> Comparisons()
    {
        foreach (var candidates in new[] { false, true })
        foreach (var output in new[] { false, true })
        foreach (var required in new[] { false, true })
        foreach (var value in new long?[] { null, 0, 4, 5, 6 })
            yield return [candidates, output, required, value];
    }

    [Theory, MemberData(nameof(Comparisons))]
    public async Task BothPublicPathsCompareOnlyConfiguredAxisAfterAdmittedToolBatch(bool candidates, bool output, bool required, long? value)
    {
        var measurement = output ? new UsageObservation(null, value) : new(value, null);
        var provider = new ScriptedProvider([Step(measurement, "c1"), Step(new(1, 1))]);
        var cap = new CounterCapability(); var hooks = new RuntimeHooks();
        var limits = new AgentUsageLimits(inputTokenThreshold: output ? null : 5, outputTokenThreshold: output ? 5 : null);
        var outcome = await Execute(Agent(provider, cap, hooks), Request(limits, required), candidates);
        var expected = value is null ? AgentTerminationReason.Partial : value >= 5 ? AgentTerminationReason.ResourceLimit : AgentTerminationReason.Completed;
        Assert.Equal(expected, outcome.Reason); Assert.Equal(1, cap.Effects);
        Assert.Equal(expected == AgentTerminationReason.Completed ? 2 : 1, provider.Effects);
        Assert.Equal(provider.Effects, hooks.Exposures.Count); Assert.Equal(provider.Effects, outcome.Usage!.Attempts.Count);
        Assert.Equal(value, output ? outcome.Usage.Attempts[0].Usage.OutputTokens : outcome.Usage.Attempts[0].Usage.InputTokens);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ReportingOnlyUnknownAndUnconfiguredOverflowDoNotStopWork(bool candidates)
    {
        var provider = new ScriptedProvider([Step(new(long.MaxValue, null), "a"), Step(new(1, null), "b"), Step(new(null, null))]);
        var cap = new CounterCapability();
        var outcome = await Execute(Agent(provider, cap), Request(null, true), candidates);
        Assert.Equal(AgentTerminationReason.Completed, outcome.Reason); Assert.Equal(3, provider.Effects); Assert.Equal(2, cap.Effects);
        Assert.Equal(TokenObservationCoverage.Overflow, outcome.Usage!.InputTokens.Coverage);
        Assert.Equal(TokenObservationCoverage.Unavailable, outcome.Usage.OutputTokens.Coverage);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task KnownReachedAxisWinsEvenWhenOtherRequiredComparisonIsUnknown(bool candidates, bool output)
    {
        var provider = new ScriptedProvider([Step(output ? new(null, 5) : new(5, null), "a"), Step(new(0, 0))]);
        var cap = new CounterCapability();
        var outcome = await Execute(Agent(provider, cap), Request(new(inputTokenThreshold: 5, outputTokenThreshold: 5)), candidates);
        Assert.Equal(AgentTerminationReason.ResourceLimit, outcome.Reason); Assert.Equal(1, provider.Effects); Assert.Equal(1, cap.Effects);
    }

    [Theory]
    [InlineData(null)] [InlineData(5L)] [InlineData(6L)]
    public async Task PlainFinalCompletionDoesNotRequireAnotherTokenComparison(long? input)
    {
        var provider = new ScriptedProvider([Step(new(input, null))]);
        var outcome = await RuntimeFixture.Agent(provider).ExecuteAsync(Request(new(inputTokenThreshold: 5, outputTokenThreshold: 5), true));
        Assert.Equal(AgentTerminationReason.Completed, outcome.Reason); Assert.Equal(input, outcome.Usage!.Attempts[0].Usage.InputTokens);
    }

    public static IEnumerable<object?[]> FeedbackCases()
    {
        foreach (var decision in new[] { CandidateDecision.Accept, CandidateDecision.Reject })
        foreach (var end in new[] { false, true })
        foreach (var input in new long?[] { null, 5, 6 }) yield return [decision, end, input];
    }

    [Theory, MemberData(nameof(FeedbackCases))]
    public async Task CandidateFeedbackOwnsCompletionAndDeniedFollowOnDoesNotCount(CandidateDecision decision, bool end, long? input)
    {
        var provider = new ScriptedProvider([Step(new(input, null)), Step(new(0, 0))]);
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s, decision,
            end ? CandidateContinuation.End : CandidateContinuation.Continue));
        var result = await CandidateFixture.Agent(provider).ExecuteCandidatesAsync(new(Request(new(inputTokenThreshold: 5), true), new(8, 8, 8)), host);
        var stop = end ? decision == CandidateDecision.Accept ? CandidateStopReason.Completed : CandidateStopReason.HostEnded
            : input is null ? CandidateStopReason.UsageAccountingUnavailable : CandidateStopReason.RuntimeLimit;
        Assert.Equal(stop, result.StopReason); Assert.Single(result.Receipts); Assert.Single(host.Submissions);
        Assert.Equal(1, provider.Effects); Assert.Equal(0, result.RepairsAdmitted); Assert.Equal(0, result.ContinuationsAdmitted);
        Assert.Equal(end && decision == CandidateDecision.Accept ? AgentTerminationReason.Completed
            : !end && input.HasValue ? AgentTerminationReason.ResourceLimit : AgentTerminationReason.Partial, result.Outcome.Reason);
    }

    [Theory]
    [InlineData(null)] [InlineData(5L)]
    public async Task DeniedCandidateProductionChecksTokensBeforeOversizedCorrection(long? input)
    {
        var provider = new ScriptedProvider([Step(new(input, 0)), Step(new(0, 0))]);
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s, CandidateDecision.Reject,
            CandidateContinuation.Continue, new string('x', 300)));
        var result = await CandidateFixture.Agent(provider, options: new(maximumRetainedBytes: 256)).ExecuteCandidatesAsync(
            new(Request(new(inputTokenThreshold: 5)), new(8, 8, 8)), host);
        Assert.Equal(input is null ? CandidateStopReason.UsageAccountingUnavailable : CandidateStopReason.RuntimeLimit, result.StopReason);
        Assert.Equal(0, result.RepairsAdmitted); Assert.Equal(1, provider.Effects); Assert.Single(result.Receipts);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task OverflowStopsOnlyRemainingProductionAndPreservesEarlierSnapshots(bool candidates, bool final)
    {
        var provider = new ScriptedProvider([Step(new(long.MaxValue - 1, 0), "a"), Step(new(2, 0), final ? null : "b"), Step(new(0, 0))]);
        var cap = new CounterCapability(); var progress = new List<AgentProgress>();
        var outcome = await Execute(Agent(provider, cap), Request(new(inputTokenThreshold: long.MaxValue)), candidates, new InlineProgress(progress.Add));
        Assert.Equal(final ? AgentTerminationReason.Completed : AgentTerminationReason.Partial, outcome.Reason);
        Assert.Equal(2, provider.Effects); Assert.Equal(final ? 1 : 2, cap.Effects);
        Assert.Equal(TokenObservationCoverage.Overflow, outcome.Usage!.InputTokens.Coverage); Assert.Null(outcome.Usage.InputTokens.ObservedTokens);
        Assert.Equal(2, outcome.Usage.Attempts.Count); Assert.Equal(long.MaxValue - 1, progress[0].Usage!.InputTokens.ObservedTokens);
        Assert.Equal(TokenObservationCoverage.Complete, progress[0].Usage!.InputTokens.Coverage);
    }

    [Theory]
    [InlineData(false, "invalid")] [InlineData(true, "invalid")]
    [InlineData(false, "observer")] [InlineData(true, "observer")]
    [InlineData(false, "settlement")] [InlineData(true, "settlement")]
    public async Task LaterFailuresRetainBothAttemptsAndTheirAggregate(bool candidates, string failure)
    {
        var provider = new ScriptedProvider([Step(new(3, 2), "a"), Step(new(4, null), invalid: failure == "invalid")]);
        var hooks = new RuntimeHooks { After = (s, _) => ValueTask.FromResult<SettlementAcknowledgement?>(
            s.Observation.Usage.InputTokens == 4 && failure == "settlement" ? null : RuntimeHooks.Continue(s)) };
        var cap = new CounterCapability(); var progress = new InlineProgress(p =>
        { if (p.CompletedWorkUnits == 2 && failure == "observer") throw new InvalidOperationException("OBSERVER_CANARY"); });
        var outcome = await Execute(Agent(provider, cap, hooks), Request(new(inputTokenThreshold: 10)), candidates, progress);
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(2, provider.Effects);
        Assert.Equal(7, outcome.Usage!.InputTokens.ObservedTokens); Assert.Equal(2, outcome.Usage.OutputTokens.ObservedTokens);
        Assert.Equal(TokenObservationCoverage.Partial, outcome.Usage.OutputTokens.Coverage); Assert.Equal(2, outcome.Usage.Attempts.Count);
    }

    [Fact]
    public async Task HostFeedbackFailureRetainsCapturedTokensInsteadOfAccountingStop()
    {
        var provider = new ScriptedProvider([Step(new(null, 7))]);
        var host = new ScriptedCandidateHost((_, _) => throw new InvalidOperationException("HOST_CANARY"));
        var result = await CandidateFixture.Agent(provider).ExecuteCandidatesAsync(new(Request(new(inputTokenThreshold: 5)), new(8, 8, 8)), host);
        Assert.Equal(CandidateStopReason.FailedAcknowledgement, result.StopReason);
        Assert.Equal(7, result.Outcome.Usage!.OutputTokens.ObservedTokens); Assert.Equal(TokenObservationCoverage.Unavailable, result.Outcome.Usage.InputTokens.Coverage);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExistingWorkCeilingWinsOverMissingRequiredMeasurement(bool candidates)
    {
        var provider = new ScriptedProvider([Step(new(null, 0), "a"), Step(new(0, 0))]);
        var cap = new CounterCapability();
        var outcome = await Execute(Agent(provider, cap), Request(new(inputTokenThreshold: 5), units: 1), candidates);
        Assert.Equal(AgentTerminationReason.ResourceLimit, outcome.Reason); Assert.Equal(1, cap.Effects); Assert.Equal(1, provider.Effects);
        Assert.Equal(TokenObservationCoverage.Unavailable, outcome.Usage!.InputTokens.Coverage);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task KnownReachedInputWinsOverOutputOverflowOnNextProduction(bool candidates)
    {
        var provider = new ScriptedProvider([Step(new(1, long.MaxValue - 1), "a"), Step(new(4, 2), "b"), Step(new(0, 0))]);
        var cap = new CounterCapability();
        var outcome = await Execute(Agent(provider, cap), Request(new(inputTokenThreshold: 5, outputTokenThreshold: long.MaxValue)), candidates);
        Assert.Equal(AgentTerminationReason.ResourceLimit, outcome.Reason); Assert.Equal(2, cap.Effects); Assert.Equal(2, provider.Effects);
        Assert.Equal(5, outcome.Usage!.InputTokens.ObservedTokens); Assert.Equal(TokenObservationCoverage.Overflow, outcome.Usage.OutputTokens.Coverage);
        Assert.Null(outcome.Usage.OutputTokens.ObservedTokens);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SettlementStopStillPreventsToolEffectsAtTokenThreshold(bool candidates)
    {
        var provider = new ScriptedProvider([Step(new(5, 0), "a")]); var cap = new CounterCapability();
        var hooks = new RuntimeHooks { After = (s, _) => ValueTask.FromResult<SettlementAcknowledgement?>(
            new(s.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Stop)) };
        var outcome = await Execute(Agent(provider, cap, hooks), Request(new(inputTokenThreshold: 5)), candidates);
        Assert.Equal(AgentTerminationReason.Partial, outcome.Reason); Assert.Equal(0, cap.Effects);
        Assert.Equal(5, outcome.Usage!.InputTokens.ObservedTokens); Assert.Equal(1, provider.Effects);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task CutAfterAcceptedBatchWinsBeforeNextTokenComparison(bool candidates, bool cancel)
    {
        var clock = new ControlledTimeProvider(); using var cts = new CancellationTokenSource();
        var provider = new ScriptedProvider([Step(new(5, null), "a"), Step(new(0, 0))]); var cap = new CounterCapability();
        var progress = new InlineProgress(_ => { if (cancel) cts.Cancel(); else clock.Advance(TimeSpan.FromSeconds(10)); });
        var outcome = await Execute(Agent(provider, cap, options: new(clock)), Request(new(inputTokenThreshold: 5)), candidates, progress, cts.Token);
        Assert.Equal(cancel ? AgentTerminationReason.Cancelled : AgentTerminationReason.ResourceLimit, outcome.Reason);
        Assert.Equal(1, cap.Effects); Assert.Equal(1, provider.Effects); Assert.Equal(5, outcome.Usage!.InputTokens.ObservedTokens);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PendingHostAtThresholdRetainsUnknownReceiptAndFrozenUsageAfterCut(bool cancel)
    {
        var clock = new ControlledTimeProvider(); using var cts = new CancellationTokenSource();
        var entered = RuntimeFixture.Barrier<CandidateSubmission>(); var release = RuntimeFixture.Barrier<CandidateFeedback?>();
        var provider = new ScriptedProvider([Step(new(5, null))]);
        var host = new ScriptedCandidateHost((s, _) => { entered.SetResult(s); return new(release.Task); });
        var pending = CandidateFixture.Agent(provider, options: new(clock)).ExecuteCandidatesAsync(
            new(Request(new(inputTokenThreshold: 5)), new(8, 8, 8)), host, cancellationToken: cts.Token).AsTask();
        var submission = await RuntimeFixture.Await(entered.Task);
        if (cancel) cts.Cancel(); else clock.Advance(TimeSpan.FromSeconds(10));
        var result = await RuntimeFixture.Await(pending);
        Assert.Equal(cancel ? CandidateStopReason.Cancelled : CandidateStopReason.DurationLimit, result.StopReason);
        Assert.Equal(CandidateAcknowledgement.Unknown, Assert.Single(result.Receipts).Acknowledgement);
        Assert.Equal(5, result.Outcome.Usage!.InputTokens.ObservedTokens); Assert.False(release.Task.IsCompleted);
        var snapshot = JsonSerializer.Serialize(result); release.SetResult(await CandidateFixture.Feedback(submission));
        Assert.Equal(snapshot, JsonSerializer.Serialize(result)); Assert.Equal(1, provider.Effects);
    }

    [Fact]
    public void CandidateAccountingCategoryRequiresPartialAndFactoryAdvertisesOnlyImplementedThresholds()
    {
        Assert.True(RuntimeAgentFactory.Support.SupportedCapabilities.HasFlag(AgentCapability.UsageThresholds));
        var id = Guid.NewGuid();
        Assert.Equal(AgentTerminationReason.Partial, new CandidateExecutionResult(new(id, AgentTerminationReason.Partial, 0),
            CandidateStopReason.UsageAccountingUnavailable, []).Outcome.Reason);
        Assert.Throws<ArgumentException>(() => new CandidateExecutionResult(new(id, AgentTerminationReason.Completed, 0),
            CandidateStopReason.UsageAccountingUnavailable, []));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task ProviderCutRetainsCapturedAggregateAndLateWorkCannotRewriteIt(bool candidates, bool cancel)
    {
        var clock = new ControlledTimeProvider(); using var cts = new CancellationTokenSource();
        var entered = RuntimeFixture.Barrier(); var release = RuntimeFixture.Barrier(); var finished = RuntimeFixture.Barrier();
        var provider = new ScriptedProvider([async (r, o, _) =>
        { o.CaptureUsage(new(5, null)); entered.SetResult(); await release.Task; finished.SetResult(); return RuntimeFixture.Final(r); }]);
        var cap = new CounterCapability();
        var pending = Execute(Agent(provider, cap, options: new(clock)), Request(new(inputTokenThreshold: 5)), candidates, cancellation: cts.Token);
        await RuntimeFixture.Await(entered.Task);
        if (cancel) cts.Cancel(); else clock.Advance(TimeSpan.FromSeconds(10));
        var outcome = await RuntimeFixture.Await(pending);
        Assert.False(release.Task.IsCompleted); Assert.Equal(cancel ? AgentTerminationReason.Cancelled : AgentTerminationReason.ResourceLimit, outcome.Reason);
        Assert.Equal(5, outcome.Usage!.InputTokens.ObservedTokens); Assert.Equal(0, outcome.CompletedWorkUnits);
        var snapshot = JsonSerializer.Serialize(outcome); release.SetResult(); await RuntimeFixture.Await(finished.Task);
        Assert.Equal(snapshot, JsonSerializer.Serialize(outcome)); Assert.Equal(1, provider.Effects);
    }
}

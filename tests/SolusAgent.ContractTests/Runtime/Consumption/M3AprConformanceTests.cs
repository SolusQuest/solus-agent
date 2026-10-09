using System.Text.Json;
using AprHost;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.ContractTests.Runtime.Tools;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Consumption;

public sealed class M3AprConformanceTests
{
    private static AgentAccountingPolicy Policy(UnknownUsagePolicy unknown = UnknownUsagePolicy.Stop,
        long input = 100, long output = 100) => unknown == UnknownUsagePolicy.Stop
            ? new(new(5, 4), input, output) : new(new(5, 4), input, output, unknown);

    private static AgentRequest Request(AgentUsageLimits limits, int work = 8, bool requireThresholds = false) =>
        new(Guid.NewGuid(), ConsumptionFixture.Instructions,
            [new(AgentInputSource.Repository, ConsumptionFixture.InstructionLikeData)], new(work, TimeSpan.FromSeconds(10)),
            AgentCapability.WorkUnitLimit | AgentCapability.Cancellation | AgentCapability.DurationLimit
                | AgentCapability.UsageReporting | AgentCapability.DispatchLimits | AgentCapability.UsageAccounting
                | (requireThresholds ? AgentCapability.UsageThresholds : AgentCapability.None), limits);

    private static ValueTask<ProviderResponse> Final(ProviderRequest request, ProviderObservation observation,
        string item, UsageObservation? usage = null)
    {
        observation.CaptureUsage(usage ?? new(3, 2));
        return ValueTask.FromResult(new ProviderResponse(request.Scope, request.Attempt, ProviderFinish.Final, item, [],
            ConsumptionFixture.Continuation(request, ConsumptionFixture.TurnReplay(request.Inputs.Count))));
    }

    private static ValueTask<ProviderResponse> Tool(ProviderRequest request, ProviderObservation observation,
        string call, int amount = 7) => ConsumptionFixture.ToolCalls(request, observation,
            [ToolFixture.Counter(call, amount)], ConsumptionFixture.TurnReplay(request.Inputs.Count));

    [Theory]
    [InlineData(7L)] [InlineData(42L)]
    public async Task MixedAprRunKeepsRetryRepairAndExplicitEffectsDistinct(long correction)
    {
        AprBusinessHost? host = null;
        var requests = new List<ProviderRequest>();
        var hooks = new AccountingHostProbe { ReverseAccountingInventory = true };
        var startup = new ProductionStartup(
        [
            (r, o, _) => { requests.Add(r); return Tool(r, o, "first"); },
            (r, o, _) => { requests.Add(r); return Final(r, o, "apr-item:initial:" + ConsumptionFixture.CounterTotal(r, "first")); },
            (r, o, _) =>
            {
                requests.Add(r); Assert.Equal(1, host!.Acceptance.AcceptedCount); Assert.Equal(0, host.Acceptance.EffectCount);
                Assert.Equal(1, host.Acceptance.ApplyEffects());
                o.CaptureUsage(new(3, 2)); throw new ProviderFailureException(new(ProviderRetryKind.Transient));
            },
            (r, o, _) =>
            {
                requests.Add(r); Assert.Equal(1, host!.Acceptance.EffectCount); Assert.Equal(0, host.Acceptance.ApplyEffects());
                return Final(r, o, "apr-item:rejected:0");
            },
            (r, o, _) =>
            {
                requests.Add(r); Assert.Equal(0, host!.Acceptance.ApplyEffects());
                o.CaptureUsage(new(3, 2)); throw new ProviderFailureException(new(ProviderRetryKind.Throttled));
            },
            (r, o, _) => { requests.Add(r); return Tool(r, o, "repair", 3); },
            (r, o, _) => { requests.Add(r); return Final(r, o,
                "apr-item:repaired:" + (ConsumptionFixture.CorrectedValue(r) + ConsumptionFixture.CounterTotal(r, "repair"))); },
        ], hooks, new(requireContinuation: true));
        host = new(startup.Agent, startup.Agent);
        var channel = new ExchangeChannel([new(true), new(false, CorrectedValue: correction), new(true, CandidateContinuation.End)]);
        var progress = new List<AgentProgress>();
        var result = await host.ExecuteCandidatesAsync(new(Request(new(maximumLogicalCalls: 5, maximumPhysicalDispatches: 7,
            accountingPolicy: Policy(), retryPolicy: new(2)), work: 5), new(3, 1, 1)), channel, new InlineProgress(progress.Add));

        Assert.Equal(CandidateStopReason.Completed, result.StopReason); Assert.Equal(AgentTerminationReason.Completed, result.Outcome.Reason);
        Assert.Equal(5, result.Outcome.CompletedWorkUnits); Assert.Equal(1, result.RepairsAdmitted); Assert.Equal(1, result.ContinuationsAdmitted);
        Assert.Equal(3, result.Receipts.Count); Assert.Equal(2, result.AcceptedCount); Assert.Equal(2, host.Acceptance.AcceptedCount);
        Assert.Equal(new[] { "apr-item:initial:7", "apr-item:rejected:0", "apr-item:repaired:" + (correction + 10) },
            channel.Submissions.Select(s => s.Payload));
        Assert.Equal(channel.Submissions[1].SubmissionId, channel.Submissions[2].RepairsSubmissionId);
        Assert.Null(channel.Submissions[0].RepairsSubmissionId); Assert.Null(channel.Submissions[1].RepairsSubmissionId);
        Assert.Equal(3, channel.Submissions.Select(s => s.SubmissionId).Distinct().Count());
        Assert.Equal(2, startup.CounterCapability.Effects); Assert.Equal(0, startup.TransformCapability.Effects);
        Assert.Equal(1, host.Acceptance.EffectCount); Assert.Equal(1, host.Acceptance.ApplyEffects());
        Assert.Equal(0, host.Acceptance.ApplyEffects()); Assert.Equal(2, host.Acceptance.EffectCount);

        var usage = result.Outcome.Usage!; var accounting = usage.Accounting!;
        Assert.Equal(7, startup.Provider.Effects); Assert.Equal(7, usage.Attempts.Count); Assert.Equal(7, accounting.Attempts.Count);
        Assert.Equal(5, requests.Select(r => r.Attempt.LogicalCallId).Distinct().Count());
        Assert.Equal(7, requests.Select(r => r.Attempt.PhysicalAttemptId).Distinct().Count());
        Assert.Equal(new[] { 1, 1, 1, 2, 1, 2, 1 }, requests.Select(r => r.Attempt.AttemptNumber));
        foreach (var index in new[] { 2, 4 })
        {
            var first = requests[index]; var retry = requests[index + 1];
            Assert.Equal(first.Attempt.LogicalCallId, retry.Attempt.LogicalCallId);
            Assert.NotEqual(first.Attempt.PhysicalAttemptId, retry.Attempt.PhysicalAttemptId);
            Assert.Equal(first.Inputs, retry.Inputs); Assert.Equal(first.Tools, retry.Tools);
            Assert.Same(first.Continuation, retry.Continuation); Assert.Same(first.Bounds, retry.Bounds);
            Assert.Equal(first.RequiredCapabilities, retry.RequiredCapabilities); Assert.NotSame(first.Observation, retry.Observation);
        }
        Assert.NotEqual(requests[3].Attempt.LogicalCallId, requests[4].Attempt.LogicalCallId);
        Assert.Equal(new[] { 2, 4, 5, 5, 7, 7, 9 }, requests.Select(r => r.Inputs.Count));
        Assert.All(requests.Skip(4), r => Assert.Single(r.Inputs, i => i.Text == ConsumptionFixture.Correction(correction)));
        Assert.All(requests.Skip(2), r => Assert.Single(r.Inputs, i => i.ToolResult?.Call.CallId == "first"));
        Assert.All(requests.Skip(1), r => Assert.NotNull(r.Continuation));
        Assert.Equal(21, usage.InputTokens.ObservedTokens); Assert.Equal(14, usage.OutputTokens.ObservedTokens);
        Assert.Equal(21, accounting.Input.MeasuredTokens); Assert.Equal(14, accounting.Output.MeasuredTokens);
        Assert.Equal(0, accounting.Input.ReservedTokens); Assert.Equal(0, accounting.Output.UnresolvedTokens);
        Assert.Equal(0, accounting.Input.ConservativeChargeTokens);
        Assert.Equal(7, hooks.Exposures.Count); Assert.Equal(7, hooks.Settlements.Count);
        Assert.True(hooks.Settlements[^1].Accounting!.Matches(accounting));
        Assert.All(accounting.Attempts, a => Assert.True(a.IsFinalized));
        Assert.Equal(5, hooks.Exposures[0].Accounting!.Input.ReservedTokens);
        Assert.Single(hooks.Exposures[0].Accounting!.Attempts);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, progress.Select(p => p.CompletedWorkUnits));
        Assert.Equal(2, progress[1].Usage!.Attempts.Count); Assert.Equal(6, progress[1].Usage!.Accounting!.Input.MeasuredTokens);
    }

    public static IEnumerable<object[]> Limits()
    {
        foreach (var dimension in new[] { "logical", "physical", "input", "output", "accounting-input", "accounting-output" })
        foreach (var room in new[] { false, true }) yield return [dimension, room];
    }

    [Theory, MemberData(nameof(Limits))]
    public async Task NextProductionLimitsRetainAcceptedAprProgress(string dimension, bool room)
    {
        var hooks = new AccountingHostProbe();
        var startup = new ProductionStartup([(r, o, _) => Tool(r, o, "first"),
            (r, o, _) => Final(r, o, "apr-item:first:" + ConsumptionFixture.CounterTotal(r, "first")),
            (r, o, _) => Final(r, o, "apr-item:next:" + ConsumptionFixture.CounterTotal(r, "first"))], hooks, new(requireContinuation: true));
        var host = new AprBusinessHost(startup.Agent, startup.Agent);
        var limits = new AgentUsageLimits(maximumLogicalCalls: dimension == "logical" ? room ? 3 : 2 : null,
            maximumPhysicalDispatches: dimension == "physical" ? room ? 3 : 2 : null,
            inputTokenThreshold: dimension == "input" ? room ? 7 : 6 : null,
            outputTokenThreshold: dimension == "output" ? room ? 5 : 4 : null,
            accountingPolicy: Policy(input: dimension == "accounting-input" ? room ? 11 : 10 : 100,
                output: dimension == "accounting-output" ? room ? 8 : 7 : 100));
        var channel = new ExchangeChannel([new(true), new(true, CandidateContinuation.End)]);
        var result = await host.ExecuteCandidatesAsync(ConsumptionFixture.Candidates(Request(limits, requireThresholds: true)), channel);
        var count = room ? 3 : 2; var accepted = room ? 2 : 1;
        Assert.Equal(room ? CandidateStopReason.Completed : CandidateStopReason.RuntimeLimit, result.StopReason);
        Assert.Equal(room ? AgentTerminationReason.Completed : AgentTerminationReason.ResourceLimit, result.Outcome.Reason);
        Assert.Equal(count, startup.Provider.Effects); Assert.Equal(count, hooks.Exposures.Count); Assert.Equal(count, hooks.Settlements.Count);
        Assert.Equal(count, result.Outcome.CompletedWorkUnits); Assert.Equal(count, result.Outcome.Usage!.Attempts.Count);
        Assert.Equal(accepted, channel.Submissions.Count); Assert.Equal(accepted, result.AcceptedCount); Assert.Equal(accepted, host.Acceptance.AcceptedCount);
        Assert.True(result.Receipts[0].IsAccepted); Assert.Equal(7, host.Acceptance.Accepted[0].Item.Value);
        Assert.Equal(1, startup.CounterCapability.Effects); Assert.Equal(0, host.Acceptance.EffectCount);
        Assert.Equal(accepted, host.Acceptance.ApplyEffects()); Assert.Equal(0, host.Acceptance.ApplyEffects());
        Assert.Equal(count * 3, result.Outcome.Usage.Accounting!.Input.MeasuredTokens);
        Assert.True(hooks.Settlements[^1].Accounting!.Matches(result.Outcome.Usage.Accounting));
    }

    public static IEnumerable<object[]> UnknownPolicies()
    {
        foreach (var policy in Enum.GetValues<UnknownUsagePolicy>())
        foreach (var missing in new[] { "input", "output", "both" }) yield return [policy, missing];
    }

    [Theory, MemberData(nameof(UnknownPolicies))]
    public async Task UnknownUsagePolicyPreservesMeasurementsAndFiniteAprContinuation(UnknownUsagePolicy policy, string missing)
    {
        var input = missing is "input" or "both"; var output = missing is "output" or "both";
        var observation = new UsageObservation(input ? null : 3, output ? null : 2);
        var hooks = new AccountingHostProbe { ReverseAccountingInventory = true };
        var startup = new ProductionStartup([(r, o, _) => Tool(r, o, "first"),
            (r, o, _) => Final(r, o, "apr-item:first:7", observation),
            (r, o, _) => Final(r, o, "apr-item:next:8", observation),
            (r, o, _) => Final(r, o, "apr-item:forbidden:9")], hooks, new(requireContinuation: true));
        var host = new AprBusinessHost(startup.Agent, startup.Agent);
        var channel = new ExchangeChannel([new(true), new(true), new(true, CandidateContinuation.End)]);
        // Permissive unknown policy cannot simultaneously require complete observed-threshold coverage.
        var result = await host.ExecuteCandidatesAsync(ConsumptionFixture.Candidates(Request(new(maximumPhysicalDispatches: 3,
            inputTokenThreshold: 100, outputTokenThreshold: 100, accountingPolicy: Policy(policy)),
            requireThresholds: policy == UnknownUsagePolicy.Stop)), channel);
        var stopped = policy == UnknownUsagePolicy.Stop; var count = stopped ? 2 : 3;
        Assert.Equal(stopped ? CandidateStopReason.UsageAccountingUnavailable : CandidateStopReason.RuntimeLimit, result.StopReason);
        Assert.Equal(stopped ? AgentTerminationReason.Partial : AgentTerminationReason.ResourceLimit, result.Outcome.Reason);
        Assert.Equal(count, startup.Provider.Effects); Assert.Equal(count, hooks.Exposures.Count); Assert.Equal(count, result.Outcome.CompletedWorkUnits);
        Assert.Equal(count - 1, result.AcceptedCount); Assert.Equal(count - 1, host.Acceptance.AcceptedCount);
        Assert.Equal(count - 1, channel.Submissions.Count); Assert.Equal(1, startup.CounterCapability.Effects);
        var usage = result.Outcome.Usage!; var ledger = usage.Accounting!;
        Assert.Equal(count, ledger.Attempts.Count); Assert.Equal(0, ledger.Input.ReservedTokens); Assert.Equal(0, ledger.Output.ReservedTokens);
        Assert.Equal(input ? null : 3L, usage.Attempts[1].Usage.InputTokens); Assert.Equal(output ? null : 2L, usage.Attempts[1].Usage.OutputTokens);
        Assert.Equal(input ? TokenObservationCoverage.Partial : TokenObservationCoverage.Complete, usage.InputTokens.Coverage);
        Assert.Equal(output ? TokenObservationCoverage.Partial : TokenObservationCoverage.Complete, usage.OutputTokens.Coverage);
        AssertAxis(ledger.Input, ledger.Attempts[1].Input, input, 3, 5);
        AssertAxis(ledger.Output, ledger.Attempts[1].Output, output, 2, 4);
        Assert.True(hooks.Settlements[^1].Accounting!.Matches(ledger));
        Assert.Equal(0, host.Acceptance.EffectCount); Assert.Equal(count - 1, host.Acceptance.ApplyEffects()); Assert.Equal(0, host.Acceptance.ApplyEffects());

        void AssertAxis(RunAccountingDimension axis, AccountingDimension entry, bool absent, long measured, long reservation)
        {
            var charge = absent && policy == UnknownUsagePolicy.ConservativeCharge;
            Assert.Equal(absent ? charge ? AccountingDisposition.ConservativeCharge : AccountingDisposition.Unresolved : AccountingDisposition.Measured,
                entry.Disposition);
            Assert.Equal(absent ? reservation : measured, entry.Amount);
            Assert.Equal(absent ? measured : count * measured, axis.MeasuredTokens);
            Assert.Equal(charge ? (count - 1) * reservation : 0, axis.ConservativeChargeTokens);
            Assert.Equal(absent && !charge ? (count - 1) * reservation : 0, axis.UnresolvedTokens);
            Assert.Equal(absent ? charge ? AccountingBalanceCoverage.Provisional : AccountingBalanceCoverage.Unknown : AccountingBalanceCoverage.Known,
                axis.Coverage);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RetryUsesPhysicalCapacityWithoutReadmittingLogicalWork(bool physicalLimit)
    {
        AprBusinessHost? host = null;
        var hooks = new AccountingHostProbe();
        var startup = new ProductionStartup([(r, o, _) => Tool(r, o, "first"),
            (r, o, _) => Final(r, o, "apr-item:first:7"),
            (r, o, _) =>
            {
                Assert.Equal(1, host!.Acceptance.ApplyEffects());
                o.CaptureUsage(new(3, 2)); throw new ProviderFailureException(new(ProviderRetryKind.Transient));
            }, (r, o, _) => Final(r, o, "apr-item:next:8")], hooks, new(requireContinuation: true));
        host = new(startup.Agent, startup.Agent);
        var channel = new ExchangeChannel([new(true), new(true, CandidateContinuation.End)]);
        var result = await host.ExecuteCandidatesAsync(ConsumptionFixture.Candidates(Request(new(
            maximumLogicalCalls: physicalLimit ? 8 : 3, maximumPhysicalDispatches: physicalLimit ? 3 : 8,
            accountingPolicy: Policy(), retryPolicy: new(2)))), channel);
        Assert.Equal(physicalLimit ? CandidateStopReason.RuntimeLimit : CandidateStopReason.Completed, result.StopReason);
        var attempts = result.Outcome.Usage!.Attempts;
        Assert.Equal(physicalLimit ? 3 : 4, attempts.Count); Assert.Equal(attempts.Count, startup.Provider.Effects);
        Assert.Equal(3, attempts.Select(a => a.LogicalCallId).Distinct().Count());
        Assert.Equal(physicalLimit ? 2 : 3, result.Outcome.CompletedWorkUnits);
        Assert.Equal(physicalLimit ? 1 : 2, result.AcceptedCount); Assert.Equal(result.AcceptedCount, host.Acceptance.AcceptedCount);
        Assert.Equal(0, result.RepairsAdmitted); Assert.Equal(1, result.ContinuationsAdmitted);
        Assert.Equal(1, startup.CounterCapability.Effects); Assert.Equal(1, host.Acceptance.EffectCount);
        Assert.Equal(physicalLimit ? 0 : 1, host.Acceptance.ApplyEffects()); Assert.Equal(0, host.Acceptance.ApplyEffects());
        Assert.Equal(attempts.Count, hooks.Exposures.Count); Assert.Equal(attempts.Count, hooks.Settlements.Count);
        if (!physicalLimit)
        {
            Assert.Equal(attempts[2].LogicalCallId, attempts[3].LogicalCallId);
            Assert.NotEqual(attempts[2].PhysicalAttemptId, attempts[3].PhysicalAttemptId); Assert.Equal(2, attempts[3].AttemptNumber);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NonAuthorizingSettlementRetainsAprProgressAndCannotRetry(bool staleAccounting)
    {
        var hooks = new RuntimeHooks { After = (s, _) => ValueTask.FromResult<SettlementAcknowledgement?>(
            s.ProviderOutcome != ProviderOutcome.Failed ? RuntimeHooks.Continue(s)
            : staleAccounting ? new(s.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue, s.Exposure.Accounting)
            : new(s.Exposure, RuntimeHookStatus.Unknown, accounting: s.Accounting)) };
        AprBusinessHost? host = null;
        var startup = new ProductionStartup([(r, o, _) => Tool(r, o, "first"),
            (r, o, _) => Final(r, o, "apr-item:first:7"), (r, o, _) =>
            {
                Assert.Equal(1, host!.Acceptance.ApplyEffects());
                o.CaptureUsage(new(3, 2)); throw new ProviderFailureException(new(ProviderRetryKind.Transient));
            }, (r, o, _) => Final(r, o, "apr-item:forbidden:8")], hooks, new(requireContinuation: true));
        host = new(startup.Agent, startup.Agent);
        var channel = new ExchangeChannel([new(true), new(true, CandidateContinuation.End)]);
        var result = await host.ExecuteCandidatesAsync(ConsumptionFixture.Candidates(Request(new(maximumPhysicalDispatches: 4,
            accountingPolicy: Policy(), retryPolicy: new(2)))), channel);
        Assert.Equal(CandidateStopReason.ProductionFailed, result.StopReason); Assert.Equal(AgentTerminationReason.Failed, result.Outcome.Reason);
        Assert.Equal(3, startup.Provider.Effects); Assert.Equal(3, hooks.Exposures.Count); Assert.Equal(3, hooks.Settlements.Count);
        Assert.Equal(2, result.Outcome.CompletedWorkUnits); Assert.Single(result.Receipts); Assert.True(result.Receipts[0].IsAccepted);
        Assert.Single(channel.Submissions); Assert.Equal(1, host.Acceptance.AcceptedCount); Assert.Equal(1, startup.CounterCapability.Effects);
        Assert.Equal(1, host.Acceptance.EffectCount); Assert.Equal(0, host.Acceptance.ApplyEffects());
        Assert.Equal(3, result.Outcome.Usage!.Attempts.Count); Assert.Equal(9, result.Outcome.Usage.Accounting!.Input.MeasuredTokens);
        Assert.All(result.Outcome.Usage.Accounting.Attempts, a => Assert.True(a.IsFinalized));
        Assert.True(hooks.Settlements[^1].Accounting!.Matches(result.Outcome.Usage.Accounting));
    }

    public static IEnumerable<object[]> Cuts()
    {
        foreach (var stage in new[] { "provider", "host" })
        foreach (var cancel in new[] { false, true })
        foreach (var failure in new[] { false, true }) yield return [stage, cancel, failure];
    }

    [Theory, MemberData(nameof(Cuts))]
    public async Task HeldProviderOrAprHostCutReturnsBeforeReleaseAndFreezesSnapshots(string stage, bool cancel, bool lateFailure)
    {
        var clock = new ControlledTimeProvider(); using var cancellation = new CancellationTokenSource();
        var entered = RuntimeFixture.Barrier(); var release = RuntimeFixture.Barrier();
        var providerPublished = RuntimeFixture.Barrier<Task<ProviderExchangeResult>>();
        var hooks = new AccountingHostProbe(); var options = new RuntimeOptions(clock, requireContinuation: true);
        AprBusinessHost? host = null; var lateWriteRejected = false;
        var startup = new ProductionStartup([(r, o, _) => Tool(r, o, "first"),
            (r, o, _) => Final(r, o, "apr-item:first:7"), async (r, o, _) =>
            {
                Assert.Equal(1, host!.Acceptance.ApplyEffects());
                if (stage == "host") return await Final(r, o, "apr-item:late:8");
                o.CaptureUsage(new(7, null)); entered.TrySetResult(); await release.Task;
                try { o.CaptureUsage(new(99, 99)); }
                catch (ProviderContractException exception) { lateWriteRejected = exception.Error == ProviderError.ObservationClosed; }
                if (lateFailure) throw new ProviderFailureException(new(ProviderRetryKind.Transient));
                return new ProviderResponse(r.Scope, r.Attempt, ProviderFinish.Final, "apr-item:late:8", []);
            }, (r, o, _) => Final(r, o, "apr-item:forbidden:9")], hooks, options);
        ICandidateAgent agent = startup.Agent;
        if (stage == "provider")
        {
            // Observe the whole guarded exchange using public composition, without changing shared startup.
            var forwarding = new InterfaceScriptedProvider(startup.Provider.Scope, (r, token) =>
            {
                var operation = startup.Provider.ExchangeAsync(r, token).AsTask();
                if (startup.Provider.Effects == 3) providerPublished.TrySetResult(operation);
                return new(operation);
            });
            agent = (ICandidateAgent)RuntimeAgentFactory.Create(new(forwarding, startup.Bindings, hooks), options);
        }
        var watching = new WatchingAgent(agent);
        host = new(agent, watching);
        var channel = new HeldAprChannel(stage == "host", lateFailure, entered, release);
        var progress = new List<AgentProgress>();
        var pending = host.ExecuteCandidatesAsync(ConsumptionFixture.Candidates(Request(new(maximumPhysicalDispatches: 4,
            accountingPolicy: Policy(), retryPolicy: new(2)))), channel, new InlineProgress(progress.Add), cancellation.Token).AsTask();
        Task? operation = null; CandidateExecutionResult? result = null; string? frozen = null; string? frozenProgress = null;
        IReadOnlyList<AprAcceptanceRecord>? acceptedCopy = null; Exception? lateError = null;
        try
        {
            await RuntimeFixture.Await(entered.Task);
            operation = stage == "provider" ? await RuntimeFixture.Await(providerPublished.Task)
                : await RuntimeFixture.Await(watching.SecondDelivery.Task);
            Assert.False(operation.IsCompleted); Assert.False(pending.IsCompleted);
            acceptedCopy = host.Acceptance.Accepted; Assert.Single(acceptedCopy); Assert.Equal(1, host.Acceptance.EffectCount);
            if (cancel) cancellation.Cancel(); else clock.Advance(TimeSpan.FromSeconds(10));
            result = await RuntimeFixture.Await(pending);
            Assert.Equal(cancel ? CandidateStopReason.Cancelled : CandidateStopReason.DurationLimit, result.StopReason);
            Assert.Equal(cancel ? AgentTerminationReason.Cancelled : AgentTerminationReason.ResourceLimit, result.Outcome.Reason);
            Assert.False(release.Task.IsCompleted); Assert.False(operation.IsCompleted);
            Assert.Equal(stage == "host" ? 3 : 2, result.Outcome.CompletedWorkUnits); Assert.Equal(1, result.AcceptedCount);
            Assert.Equal(stage == "host" ? 2 : 1, result.Receipts.Count); Assert.Equal(1, host.Acceptance.AcceptedCount);
            if (stage == "host") Assert.Equal(CandidateAcknowledgement.Unknown, result.Receipts[^1].Acknowledgement);
            var usage = result.Outcome.Usage!;
            Assert.Equal(3, usage.Attempts.Count); Assert.Equal(stage == "host" ? 9 : 13, usage.Accounting!.Input.MeasuredTokens);
            Assert.Equal(stage == "host" ? AccountingDisposition.Measured : AccountingDisposition.Unresolved,
                usage.Accounting.Attempts[^1].Output.Disposition);
            Assert.Equal(stage == "host" ? 2L : null, usage.Attempts[^1].Usage.OutputTokens);
            Assert.Equal(0, usage.Accounting.Input.ReservedTokens); Assert.Equal(0, usage.Accounting.Output.ReservedTokens);
            frozen = JsonSerializer.Serialize(result); frozenProgress = JsonSerializer.Serialize(progress.ToArray());
        }
        finally
        {
            release.TrySetResult();
            if (operation is not null) lateError = await Record.ExceptionAsync(() => operation.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        Assert.NotNull(operation); Assert.True(operation.IsCompleted); Assert.NotNull(result);
        if (stage == "host" && lateFailure) Assert.IsType<InvalidOperationException>(lateError); else Assert.Null(lateError);
        Assert.Equal(frozen, JsonSerializer.Serialize(result)); Assert.Equal(frozenProgress, JsonSerializer.Serialize(progress.ToArray()));
        Assert.Single(acceptedCopy!); Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(stage == "host" && !lateFailure ? 2 : 1, host.Acceptance.AcceptedCount);
        Assert.Equal(3, startup.Provider.Effects); Assert.Equal(1, startup.CounterCapability.Effects);
        Assert.Equal(stage == "host" ? 2 : 1, channel.Calls); Assert.Equal(channel.Calls, watching.DeliveryCount);
        Assert.Equal(1, host.Acceptance.EffectCount); Assert.Equal(stage == "host" && !lateFailure ? 1 : 0, host.Acceptance.ApplyEffects());
        Assert.Equal(0, host.Acceptance.ApplyEffects());
        Assert.Equal(5, hooks.Exposures[0].Accounting!.Input.ReservedTokens); Assert.Single(hooks.Exposures[0].Accounting!.Attempts);
        if (stage == "provider") Assert.True(lateWriteRejected);
    }

    private sealed class HeldAprChannel(bool hold, bool fail, TaskCompletionSource entered, TaskCompletionSource release) : ICandidateHost
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        public async ValueTask<CandidateFeedback?> SubmitAsync(CandidateSubmission submission, CancellationToken cancellationToken = default)
        {
            var ordinal = Interlocked.Increment(ref calls);
            if (ordinal == 2 && hold)
            {
                entered.TrySetResult(); await release.Task;
                if (fail) throw new InvalidOperationException("Synthetic late APR feedback failure.");
            }
            return new(submission.ExecutionId, submission.SubmissionId, CandidateAcknowledgement.Acknowledged,
                CandidateDecision.Accept, ordinal == 1 ? CandidateContinuation.Continue : CandidateContinuation.End);
        }
    }

    // Transparent observation of the actual APR recording decorator, including its independent late acceptance.
    private sealed class WatchingAgent(ICandidateAgent inner) : ICandidateAgent
    {
        private int deliveryCount;
        public AgentCapability SupportedCapabilities => inner.SupportedCapabilities;
        internal int DeliveryCount => Volatile.Read(ref deliveryCount);
        internal TaskCompletionSource<Task<CandidateFeedback?>> SecondDelivery { get; } = RuntimeFixture.Barrier<Task<CandidateFeedback?>>();
        public ValueTask<AgentOutcome> ExecuteAsync(AgentRequest request, IProgress<AgentProgress>? progress = null,
            CancellationToken cancellationToken = default) => inner.ExecuteAsync(request, progress, cancellationToken);
        public ValueTask<CandidateExecutionResult> ExecuteCandidatesAsync(CandidateExecutionRequest request, ICandidateHost host,
            IProgress<AgentProgress>? progress = null, CancellationToken cancellationToken = default) =>
            inner.ExecuteCandidatesAsync(request, new WatchingHost(this, host), progress, cancellationToken);
        private sealed class WatchingHost(WatchingAgent owner, ICandidateHost host) : ICandidateHost
        {
            public ValueTask<CandidateFeedback?> SubmitAsync(CandidateSubmission submission, CancellationToken cancellationToken = default)
            {
                var operation = host.SubmitAsync(submission, cancellationToken).AsTask();
                if (Interlocked.Increment(ref owner.deliveryCount) == 2) owner.SecondDelivery.TrySetResult(operation);
                return new(operation);
            }
        }
    }
}

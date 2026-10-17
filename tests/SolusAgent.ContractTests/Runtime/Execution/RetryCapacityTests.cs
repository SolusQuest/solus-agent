using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Candidates;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Execution;

public sealed class RetryCapacityTests
{
    internal static AgentUsageLimits Limits(int policy, int physical = 128, long? inputAllowance = null) =>
        new(maximumLogicalCalls: 1, maximumPhysicalDispatches: physical,
            accountingPolicy: new(new(5, 4), inputAllowance ?? 3L * physical + 2, 2L * physical + 2), retryPolicy: new(policy));
    internal static AgentRequest Request(AgentUsageLimits limits) => RetryTests.Request(limits, units: 1, duration: TimeSpan.FromSeconds(30));
    internal static async Task<(AgentOutcome Outcome, CandidateExecutionResult? Candidate)> Execute(
        IAgent agent, AgentRequest request, bool candidate, ScriptedCandidateHost host, CancellationToken token = default)
    {
        if (!candidate) return (await agent.ExecuteAsync(request, cancellationToken: token), null);
        var result = await ((ICandidateAgent)agent).ExecuteCandidatesAsync(new(request, new(1, 0, 0)), host, cancellationToken: token);
        return (result.Outcome, result);
    }
    internal static ScriptedCandidateHost Host() => new((s, _) => CandidateFixture.Feedback(s));

    [Theory]
    [InlineData(false, 65)] [InlineData(true, 65)] [InlineData(false, 128)] [InlineData(true, 128)]
    public async Task OneLogicalCallSettlesEveryAttemptBeforeOneAcceptedFinal(bool candidate, int count)
    {
        var seen = new List<ProviderRequest>();
        var provider = new ScriptedProvider(Enumerable.Range(1, count).Select<int,
            Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>>>(i => (r, o, _) =>
        {
            seen.Add(r); o.CaptureUsage(new(3, 2));
            if (i < count) throw new ProviderFailureException(new(ProviderRetryKind.Transient));
            return ValueTask.FromResult(RuntimeFixture.Final(r));
        }).ToArray());
        var hooks = new RuntimeHooks(); var host = Host();
        var request = Request(Limits(count, count));
        var (result, feedback) = await Execute(AccountingTests.Agent(provider, hooks, options: new(maximumAttempts: 128)), request, candidate, host);
        Assert.Equal(AgentTerminationReason.Completed, result.Reason); Assert.Equal(1, result.CompletedWorkUnits);
        Assert.Equal(count, provider.Effects); Assert.Equal(candidate ? 1 : 0, host.Submissions.Count);
        if (candidate) { Assert.Equal(1, feedback!.AcceptedCount); Assert.Equal(CandidateStopReason.Completed, feedback.StopReason); }
        Assert.Single(seen.Select(r => r.Attempt.LogicalCallId).Distinct());
        Assert.Equal(count, seen.Select(r => r.Attempt.PhysicalAttemptId).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, count), seen.Select(r => r.Attempt.AttemptNumber));
        Assert.All(seen, r =>
        {
            Assert.Equal(request.ExecutionId, r.Attempt.ExecutionId); Assert.Equal(seen[0].Inputs, r.Inputs);
            Assert.Same(seen[0].Bounds, r.Bounds); Assert.Same(seen[0].History, r.History);
            Assert.Same(seen[0].Continuation, r.Continuation); Assert.Empty(r.Tools);
        });
        Assert.Throws<ProviderContractException>(() => seen[0].Observation.CaptureUsage(new(99, 99)));
        AssertAccounting(result, hooks, count);
    }

    internal static void AssertAccounting(AgentOutcome result, RuntimeHooks hooks, int count)
    {
        Assert.Equal(count, result.Usage!.Attempts.Count); Assert.Equal(3L * count, result.Usage.InputTokens.ObservedTokens);
        Assert.Equal(2L * count, result.Usage.OutputTokens.ObservedTokens);
        Assert.Equal(count, hooks.Exposures.Count); Assert.Equal(count, hooks.Settlements.Count);
        var accounting = result.Usage.Accounting!;
        Assert.Equal(count, accounting.Attempts.Count); Assert.Equal(0, accounting.Input.ReservedTokens); Assert.Equal(0, accounting.Output.ReservedTokens);
        Assert.Equal(3L * count, accounting.Input.AccountedTokens); Assert.Equal(2L * count, accounting.Output.AccountedTokens);
        Assert.True(hooks.Settlements[^1].Accounting!.Matches(accounting));
        for (var i = 0; i < count; i++)
        {
            var observed = result.Usage.Attempts[i]; var entry = accounting.Attempts[i];
            Assert.True(entry.MatchesObservation(observed, UnknownUsagePolicy.Stop)); Assert.True(entry.IsFinalized);
            Assert.Equal(AccountingDisposition.Measured, entry.Input.Disposition); Assert.Equal(AccountingDisposition.Measured, entry.Output.Disposition);
            Assert.Equal(3, entry.Input.Amount); Assert.Equal(2, entry.Output.Amount);
            Assert.Equal(observed.PhysicalAttemptId, hooks.Exposures[i].Attempt.PhysicalAttemptId);
            Assert.Equal(observed.PhysicalAttemptId, hooks.Settlements[i].Exposure.Attempt.PhysicalAttemptId);
            Assert.Equal(3L * i, hooks.Exposures[i].Accounting!.Input.MeasuredTokens);
            Assert.Equal(5, hooks.Exposures[i].Accounting!.Input.ReservedTokens);
            Assert.Equal(4, hooks.Exposures[i].Accounting!.Output.ReservedTokens);
            Assert.Equal(3L * (i + 1), hooks.Settlements[i].Accounting!.Input.MeasuredTokens);
            Assert.Equal(0, hooks.Settlements[i].Accounting!.Input.ReservedTokens);
            Assert.All(hooks.Settlements[i].Accounting!.Attempts, a => Assert.True(a.IsFinalized));
        }
    }

    public static IEnumerable<object[]> Neighbors()
    {
        foreach (var candidate in new[] { false, true })
        foreach (var count in new[] { 65, 128 })
        foreach (var limit in new[] { "policy", "physical", "inventory", "accounting" }) yield return [candidate, count, limit];
    }
    [Theory, MemberData(nameof(Neighbors))]
    public async Task IndependentOneUnderLimitsDenyTheNextDispatch(bool candidate, int count, string limit)
    {
        var steps = Enumerable.Repeat(ScriptedProvider.Failure(new(ProviderRetryKind.Transient)), count - 1).Append(ScriptedProvider.Final).ToArray();
        var provider = new ScriptedProvider(steps); var hooks = new RuntimeHooks(); var host = Host();
        var limits = Limits(limit == "policy" ? count - 1 : count, limit == "physical" ? count - 1 : count,
            limit == "accounting" ? 3L * (count - 1) + 4 : null);
        var (result, _) = await Execute(AccountingTests.Agent(provider, hooks, options: new(maximumAttempts: limit == "inventory" ? count - 1 : 128)),
            Request(limits), candidate, host);
        Assert.Equal(limit == "policy" ? AgentTerminationReason.Failed : AgentTerminationReason.ResourceLimit, result.Reason);
        Assert.Equal(count - 1, provider.Effects); Assert.Equal(0, result.CompletedWorkUnits); Assert.Empty(host.Submissions);
        AssertAccounting(result, hooks, count - 1);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task PermissionDoesNotEnlargeDefaultInventoryOrCreateDefaultRetries(bool candidate, bool omitted)
    {
        var provider = new ScriptedProvider(Enumerable.Repeat(ScriptedProvider.Failure(new(ProviderRetryKind.Transient)), 128).ToArray());
        var limits = new AgentUsageLimits(maximumPhysicalDispatches: 128, retryPolicy: omitted ? null : new(128));
        var host = Host(); var (result, _) = await Execute(AccountingTests.Agent(provider, new RuntimeHooks(),
            options: omitted ? new(maximumAttempts: 128) : new()), Request(limits), candidate, host);
        Assert.Equal(omitted ? AgentTerminationReason.Failed : AgentTerminationReason.ResourceLimit, result.Reason);
        Assert.Equal(omitted ? 1 : 64, provider.Effects); Assert.Equal(provider.Effects, result.Usage!.Attempts.Count); Assert.Empty(host.Submissions);
    }

    public static IEnumerable<object[]> Cuts()
    {
        foreach (var candidate in new[] { false, true })
        foreach (var count in new[] { 64, 127 })
        foreach (var mode in new[] { "cancel", "deadline", "unknown", "closure" }) yield return [candidate, count, mode];
    }
    [Theory, MemberData(nameof(Cuts))]
    public async Task HighOrdinalCutsAndRefusalsRetainObservationsWithoutFurtherEffects(bool candidate, int count, string mode)
    {
        using var cancellation = new CancellationTokenSource(); var clock = new ControlledTimeProvider();
        var provider = new ScriptedProvider(Enumerable.Range(1, count + 1).Select<int,
            Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>>>(i => (r, o, _) =>
        {
            o.CaptureUsage(i == count && mode == "unknown" ? new(null, 2) : new(3, 2));
            throw new ProviderFailureException(new(ProviderRetryKind.Transient));
        }).ToArray());
        var hooks = new RuntimeHooks { After = (s, _) =>
        {
            if (s.Exposure.Attempt.AttemptNumber == count)
            {
                if (mode == "cancel") cancellation.Cancel();
                if (mode == "deadline") clock.Advance(TimeSpan.FromSeconds(30));
                if (mode == "closure") return ValueTask.FromResult<SettlementAcknowledgement?>(null);
            }
            return ValueTask.FromResult<SettlementAcknowledgement?>(RuntimeHooks.Continue(s));
        } };
        var host = Host(); var (result, _) = await Execute(AccountingTests.Agent(provider, hooks, options: new(clock, maximumAttempts: 128)),
            Request(Limits(128, inputAllowance: 1000)), candidate, host, cancellation.Token);
        Assert.Equal(mode switch { "cancel" => AgentTerminationReason.Cancelled, "deadline" => AgentTerminationReason.ResourceLimit,
            "unknown" => AgentTerminationReason.Partial, _ => AgentTerminationReason.Failed }, result.Reason);
        Assert.Equal(count, provider.Effects); Assert.Equal(count, hooks.Exposures.Count); Assert.Equal(count, hooks.Settlements.Count);
        Assert.Empty(host.Submissions); Assert.Equal(0, result.CompletedWorkUnits); Assert.Equal(count, result.Usage!.Attempts.Count);
        Assert.Equal(3L * (mode == "unknown" ? count - 1 : count), result.Usage.InputTokens.ObservedTokens);
        if (mode == "unknown")
        {
            Assert.Null(result.Usage.Attempts[^1].Usage.InputTokens);
            Assert.Equal(AccountingDisposition.Unresolved, result.Usage.Accounting!.Attempts[^1].Input.Disposition);
            Assert.Equal(5, result.Usage.Accounting.Input.UnresolvedTokens);
        }
        Assert.All(result.Usage.Accounting!.Attempts, a => Assert.True(a.IsFinalized));
        Assert.Equal(0, result.Usage.Accounting.Input.ReservedTokens);
        clock.Advance(TimeSpan.FromMinutes(1)); Assert.Equal(count, provider.Effects);
    }
}

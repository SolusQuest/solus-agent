using CustomTools;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Candidates;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Execution;

public sealed class AccountingBoundaryTests
{
    public static IEnumerable<object[]> OverflowCases()
    {
        foreach (var candidate in new[] { false, true })
        foreach (var output in new[] { false, true })
        foreach (var final in new[] { false, true }) yield return [candidate, output, final];
    }
    [Theory, MemberData(nameof(OverflowCases))]
    public async Task SettledOverflowRetainsExactEntriesAndOtherAxisAndOnlyStopsFollowOn(bool candidate, bool output, bool final)
    {
        var provider = new ScriptedProvider([
            AccountingTests.Step(output ? new(1, long.MaxValue - 1) : new(long.MaxValue - 1, 1), "a"),
            AccountingTests.Step(output ? new(1, 2) : new(2, 1), final ? null : "b"), AccountingTests.Step(new(0, 0))]);
        var hooks = new AccountingHostProbe(); var tool = new CounterCapability();
        var result = await AccountingTests.Execute(AccountingTests.Agent(provider, hooks, tool),
            AccountingTests.Request(new(new(0, 0))), candidate);
        Assert.Equal(final ? AgentTerminationReason.Completed : AgentTerminationReason.ResourceLimit, result.Reason);
        Assert.Equal(2, provider.Effects); Assert.Equal(final ? 1 : 2, tool.Effects);
        var overflow = output ? result.Usage!.Accounting!.Output : result.Usage!.Accounting!.Input;
        var known = output ? result.Usage.Accounting.Input : result.Usage.Accounting.Output;
        Assert.Equal(AccountingBalanceCoverage.Overflow, overflow.Coverage); Assert.Null(overflow.AccountedTokens); Assert.Null(overflow.MeasuredTokens);
        Assert.Equal(AccountingBalanceCoverage.Known, known.Coverage); Assert.Equal(2, known.MeasuredTokens);
        Assert.Equal(long.MaxValue - 1, output ? result.Usage.Attempts[0].Usage.OutputTokens : result.Usage.Attempts[0].Usage.InputTokens);
        Assert.Equal(2, output ? result.Usage.Attempts[1].Usage.OutputTokens : result.Usage.Attempts[1].Usage.InputTokens);
        Assert.Equal(long.MaxValue - 1, output ? hooks.Settlements[0].Accounting!.Output.MeasuredTokens : hooks.Settlements[0].Accounting!.Input.MeasuredTokens);
    }

    [Theory]
    [InlineData(false, 1, 2)] [InlineData(true, 1, 2)] [InlineData(false, 2, 1)] [InlineData(true, 2, 1)]
    public async Task ExactInt64AdmissionFitsButNextReservationOverflowNeverDispatches(bool candidate, long reservation, int calls)
    {
        var provider = new ScriptedProvider([AccountingTests.Step(new(long.MaxValue - 1, 0), "a"), AccountingTests.Step(new(1, 0))]);
        var hooks = new AccountingHostProbe(); var tool = new CounterCapability();
        var result = await AccountingTests.Execute(AccountingTests.Agent(provider, hooks, tool),
            AccountingTests.Request(new(new(reservation, 0), long.MaxValue)), candidate);
        Assert.Equal(calls, provider.Effects); Assert.Equal(calls, result.Usage!.Accounting!.Attempts.Count);
        Assert.Equal(calls == 2 ? AgentTerminationReason.Completed : AgentTerminationReason.ResourceLimit, result.Reason);
        Assert.Equal(calls == 2 ? long.MaxValue : long.MaxValue - 1, result.Usage.Accounting.Input.MeasuredTokens);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task UnconfiguredMissingDimensionDoesNotInventRequiredComparison(bool candidate, bool output)
    {
        var provider = new ScriptedProvider([AccountingTests.Step(output ? new(null, 2) : new(2, null), "a"), AccountingTests.Step(new(1, 1))]);
        var result = await AccountingTests.Execute(AccountingTests.Agent(provider, new AccountingHostProbe(), new CounterCapability()),
            AccountingTests.Request(new(new(8, 5), output ? null : 20, output ? 20 : null)), candidate);
        Assert.Equal(AgentTerminationReason.Completed, result.Reason); Assert.Equal(2, provider.Effects);
        Assert.Equal(AccountingBalanceCoverage.Unknown, output ? result.Usage!.Accounting!.Input.Coverage : result.Usage!.Accounting!.Output.Coverage);
    }

    [Theory]
    [InlineData(false, UnknownUsagePolicy.ConservativeCharge)] [InlineData(true, UnknownUsagePolicy.ConservativeCharge)]
    [InlineData(false, UnknownUsagePolicy.ContinueUnknown)] [InlineData(true, UnknownUsagePolicy.ContinueUnknown)]
    public async Task ExplicitUnknownContinuationStillEnforcesPhysicalCount(bool candidate, UnknownUsagePolicy mode)
    {
        var provider = new ScriptedProvider([AccountingTests.Step(new(), "a"), AccountingTests.Step(new(), "b"), AccountingTests.Step(new())]);
        var tool = new CounterCapability();
        var result = await AccountingTests.Execute(AccountingTests.Agent(provider, new AccountingHostProbe(), tool),
            AccountingTests.Request(new(new(0, 0), unknownUsage: mode), count: 2, threshold: 10), candidate);
        Assert.Equal(AgentTerminationReason.ResourceLimit, result.Reason); Assert.Equal(2, provider.Effects); Assert.Equal(2, tool.Effects);
        Assert.Equal(TokenObservationCoverage.Unavailable, result.Usage!.InputTokens.Coverage);
        Assert.NotEqual(AccountingBalanceCoverage.Known, result.Usage.Accounting!.Input.Coverage);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task KnownAccountingLimitWinsOverMissingObservedComparison(bool candidate)
    {
        var provider = new ScriptedProvider([AccountingTests.Step(new(null, 5), "a"), AccountingTests.Step(new())]);
        var result = await AccountingTests.Execute(AccountingTests.Agent(provider, new AccountingHostProbe(), new CounterCapability()),
            AccountingTests.Request(new(new(8, 5), outputAllowance: 5), threshold: 10), candidate);
        Assert.Equal(AgentTerminationReason.ResourceLimit, result.Reason); Assert.Equal(1, provider.Effects);
    }

    public static IEnumerable<object?[]> EndCases()
    {
        foreach (var decision in new[] { CandidateDecision.Accept, CandidateDecision.Reject })
        foreach (var end in new[] { false, true })
        foreach (var measured in new long?[] { null, 8, 9 }) yield return [decision, end, measured];
    }
    [Theory, MemberData(nameof(EndCases))]
    public async Task CandidateEndOwnsCompletionAndFollowOnIsCheckedBeforeCorrection(CandidateDecision decision, bool end, long? measured)
    {
        var provider = new ScriptedProvider([AccountingTests.Step(new(measured, 0)), AccountingTests.Step(new(0, 0))]);
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s, decision,
            end ? CandidateContinuation.End : CandidateContinuation.Continue, decision == CandidateDecision.Reject && !end ? new string('x', 300) : null));
        var agent = (ICandidateAgent)AccountingTests.Agent(provider, new AccountingHostProbe(), options: new(maximumRetainedBytes: 256));
        var result = await agent.ExecuteCandidatesAsync(new(AccountingTests.Request(new(new(1, 0), 8)), new(8, 8, 8)), host);
        if (end)
        {
            Assert.Equal(decision == CandidateDecision.Accept ? CandidateStopReason.Completed : CandidateStopReason.HostEnded, result.StopReason);
            Assert.Equal(1, provider.Effects);
        }
        else
        {
            Assert.Equal(measured is null ? CandidateStopReason.UsageAccountingUnavailable : CandidateStopReason.RuntimeLimit, result.StopReason);
            Assert.Equal(1, provider.Effects);
            Assert.Equal(0, result.RepairsAdmitted); Assert.Equal(0, result.ContinuationsAdmitted);
        }
        Assert.Equal(measured, result.Outcome.Usage!.Attempts[0].Usage.InputTokens);
    }

    public static IEnumerable<object[]> LossCases()
    {
        foreach (var candidate in new[] { false, true })
        foreach (var mode in Enum.GetValues<UnknownUsagePolicy>())
        foreach (var exposure in Enum.GetValues<DispatchExposure>()) yield return [candidate, mode, exposure];
    }
    [Theory, MemberData(nameof(LossCases))]
    public async Task ProviderFailureNeverTurnsPossibleDispatchIntoFreeConsumption(bool candidate, UnknownUsagePolicy mode, DispatchExposure exposure)
    {
        var provider = new InterfaceScriptedProvider(new("p", "m"), (r, _) =>
        { r.Observation.ObserveDispatch(exposure); throw new InvalidOperationException(); });
        var result = await AccountingTests.Execute(AccountingTests.Agent(provider, new AccountingHostProbe()),
            AccountingTests.Request(new(new(8, 5), 20, 20, mode)), candidate);
        Assert.Equal(AgentTerminationReason.Failed, result.Reason); Assert.Equal(1, provider.Calls);
        var expected = exposure == DispatchExposure.NotDispatched ? AccountingDisposition.Released
            : mode == UnknownUsagePolicy.ConservativeCharge ? AccountingDisposition.ConservativeCharge : AccountingDisposition.Unresolved;
        Assert.Equal(expected, result.Usage!.Accounting!.Attempts[0].Input.Disposition);
        Assert.Equal(exposure == DispatchExposure.NotDispatched ? 0 : 8, result.Usage.Accounting.Input.AccountedTokens);
        Assert.Null(result.Usage.Attempts[0].Usage.InputTokens);
    }
}

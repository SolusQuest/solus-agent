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
using SolusAgent.Runtime.Execution;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Execution;

public sealed class AccountingTests
{
    internal static AgentRequest Request(AgentAccountingPolicy policy, bool required = false, Guid? id = null,
        int count = 4, long? threshold = null) => new(id ?? Guid.NewGuid(), "i", [], new(8, TimeSpan.FromSeconds(10)),
            required ? AgentCapability.UsageAccounting : AgentCapability.None,
            new(maximumPhysicalDispatches: count, inputTokenThreshold: threshold, accountingPolicy: policy));
    internal static Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>> Step(
        UsageObservation usage, string? tool = null, UsageAccounting? claim = null) => (r, o, _) =>
    {
        o.CaptureUsage(usage, claim);
        return ValueTask.FromResult(new ProviderResponse(r.Scope, r.Attempt, tool is null ? ProviderFinish.Final : ProviderFinish.ToolCalls,
            tool is null ? "f" : null, tool is null ? [] : [ToolFixture.Counter(tool)]));
    };
    internal static async Task<AgentOutcome> Execute(IAgent agent, AgentRequest request, bool candidate, CancellationToken token = default) => candidate
        ? (await ((ICandidateAgent)agent).ExecuteCandidatesAsync(new(request, new(8, 8, 8)),
            new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s)), cancellationToken: token)).Outcome
        : await agent.ExecuteAsync(request, cancellationToken: token);
    internal static IAgent Agent(IModelProvider provider, IRuntimeExposureHooks? hooks, CounterCapability? tool = null,
        ExposureStrength strength = ExposureStrength.Volatile, RuntimeOptions? options = null) => RuntimeAgentFactory.Create(
            new(provider, tool is null ? [] : [new(new CounterTool(maximumResultBytes: 64), tool)], hooks,
                requiredAcknowledgement: strength, requiredGuarantees: RuntimeGuarantee.None), options);

    public static IEnumerable<object?[]> Modes()
    {
        foreach (var candidate in new[] { false, true })
        foreach (var output in new[] { false, true })
        foreach (var mode in Enum.GetValues<UnknownUsagePolicy>())
        foreach (var value in new long?[] { null, 0, 3 }) yield return [candidate, output, mode, value];
    }
    [Theory, MemberData(nameof(Modes))]
    public async Task PolicyChangesNextAdmissionAndSettlesEachDimensionExclusively(bool candidate, bool output, UnknownUsagePolicy mode, long? value)
    {
        var provider = new ScriptedProvider([Step(output ? new(2, value) : new(value, 2), "a"), Step(new(1, 1))]);
        var host = new AccountingHostProbe(); var tool = new CounterCapability();
        var policy = new AgentAccountingPolicy(new(8, 5), 20, 20, mode);
        var outcome = await Execute(Agent(provider, host, tool), Request(policy, true), candidate);
        var stopped = value is null && mode == UnknownUsagePolicy.Stop;
        Assert.Equal(stopped ? AgentTerminationReason.Partial : AgentTerminationReason.Completed, outcome.Reason);
        Assert.Equal(stopped ? 1 : 2, provider.Effects); Assert.Equal(1, tool.Effects);
        var entry = outcome.Usage!.Accounting!.Attempts[0]; var axis = output ? entry.Output : entry.Input;
        var other = output ? entry.Input : entry.Output;
        Assert.Equal(value.HasValue ? AccountingDisposition.Measured : mode == UnknownUsagePolicy.ConservativeCharge
            ? AccountingDisposition.ConservativeCharge : AccountingDisposition.Unresolved, axis.Disposition);
        Assert.Equal(value ?? (output ? 5 : 8), axis.Amount);
        Assert.Equal(AccountingDisposition.Measured, other.Disposition); Assert.Equal(2, other.Amount);
        Assert.Equal(8, host.Exposures[0].Accounting!.Input.ReservedTokens); Assert.Equal(5, host.Exposures[0].Accounting!.Output.ReservedTokens);
        Assert.Equal(0, outcome.Usage.Accounting.Input.ReservedTokens); Assert.Equal(0, outcome.Usage.Accounting.Output.ReservedTokens);
        Assert.True(host.Settlements[^1].Accounting!.Matches(outcome.Usage.Accounting));
        Assert.Equal(value, output ? outcome.Usage.Attempts[0].Usage.OutputTokens : outcome.Usage.Attempts[0].Usage.InputTokens);
        if (value is null) Assert.NotEqual(TokenObservationCoverage.Complete, output ? outcome.Usage.OutputTokens.Coverage : outcome.Usage.InputTokens.Coverage);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task ActivePolicyCannotBypassMissingHooksAndCurrentRequestOwnsRule(bool candidate, bool required)
    {
        var provider = new ScriptedProvider([Step(new(1, 1)), Step(new(1, 1))]);
        var agent = Agent(provider, null);
        var before = await Execute(agent, RuntimeFixture.Request(), candidate);
        Assert.Equal(AgentTerminationReason.Completed, before.Reason);
        var result = await Execute(agent, Request(new(new(2, 2)), required), candidate);
        Assert.Equal(AgentTerminationReason.Failed, result.Reason); Assert.Equal(AgentFailureCode.ExecutionFailed, result.FailureCode);
        Assert.Equal(0, result.CompletedWorkUnits); Assert.Empty(result.Usage!.Attempts); Assert.Empty(result.Usage.Accounting!.Attempts);
        Assert.Equal(1, provider.Effects);
        var after = await Execute(agent, RuntimeFixture.Request(), candidate);
        Assert.Equal(AgentTerminationReason.Completed, after.Reason); Assert.Equal(2, provider.Effects);
    }

    [Theory]
    [InlineData(false, 7, 0)] [InlineData(true, 7, 0)]
    [InlineData(false, 8, 1)] [InlineData(true, 8, 1)]
    [InlineData(false, 10, 1)] [InlineData(true, 10, 1)]
    [InlineData(false, 11, 2)] [InlineData(true, 11, 2)]
    public async Task ReservationFitIsAtomicAndUsesActualSettledBalance(bool candidate, long allowance, int calls)
    {
        var provider = new ScriptedProvider([Step(new(3, 2), "a"), Step(new(1, 1))]);
        var host = new AccountingHostProbe(); var tool = new CounterCapability();
        var result = await Execute(Agent(provider, host, tool), Request(new(new(8, 5), allowance, 20)), candidate);
        Assert.Equal(calls, provider.Effects); Assert.Equal(calls, host.Exposures.Count); Assert.Equal(calls, result.Usage!.Attempts.Count);
        Assert.Equal(calls, result.Usage.Accounting!.Attempts.Count); Assert.Equal(calls == 0 ? 0 : 1, tool.Effects);
        Assert.Equal(calls == 2 ? AgentTerminationReason.Completed : AgentTerminationReason.ResourceLimit, result.Reason);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ProviderClaimsCannotReplaceLedgerAndDetailsRemainAvailable(bool candidate)
    {
        var details = new[] { new ProviderTokenCounter(ProviderTokenCounterKind.CacheRead, 2, TokenCounterRelationship.IncludedInInput),
            new ProviderTokenCounter(ProviderTokenCounterKind.Reasoning, 1, TokenCounterRelationship.IncludedInOutput) };
        var claim = new UsageAccounting(UsageSettlement.Unsettled, new(900, 800), new(700, 600));
        var provider = new ScriptedProvider([Step(new(3, 2, details), claim: claim)]); var host = new AccountingHostProbe();
        var result = await Execute(Agent(provider, host), Request(new(new(8, 5), 20, 20)), candidate);
        Assert.Equal(AgentTerminationReason.Completed, result.Reason);
        Assert.Same(claim, result.Usage!.Attempts[0].Accounting); Assert.Equal(2, result.Usage.Attempts[0].Usage.ProviderCounters.Count);
        Assert.Equal(3, result.Usage.Accounting!.Input.MeasuredTokens); Assert.Equal(0, result.Usage.Accounting.Input.ConservativeChargeTokens);
        Assert.Equal(17, result.Usage.Accounting.Input.RemainingAllowance); Assert.Equal(18, result.Usage.Accounting.Output.RemainingAllowance);
        Assert.Equal(AccountingBalanceCoverage.Known, result.Usage.Accounting.Input.Coverage);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task OverReservationPreservesActualAndOnlyBlocksNextProduction(bool candidate, bool final)
    {
        var provider = new ScriptedProvider([Step(new(12, 2), final ? null : "a"), Step(new(0, 0))]);
        var tool = new CounterCapability();
        var result = await Execute(Agent(provider, new AccountingHostProbe(), tool), Request(new(new(8, 5), 10, 20)), candidate);
        Assert.Equal(final ? AgentTerminationReason.Completed : AgentTerminationReason.ResourceLimit, result.Reason);
        Assert.Equal(1, provider.Effects); Assert.Equal(final ? 0 : 1, tool.Effects);
        Assert.Equal(12, result.Usage!.Accounting!.Input.MeasuredTokens); Assert.Equal(-2, result.Usage.Accounting.Input.RemainingAllowance);
        Assert.Equal(8, result.Usage.Accounting.Attempts[0].Reservation.InputTokens);
    }

    [Theory]
    [InlineData(UnknownUsagePolicy.Stop)] [InlineData(UnknownUsagePolicy.ConservativeCharge)] [InlineData(UnknownUsagePolicy.ContinueUnknown)]
    public void DuplicateFinalizationCannotReleaseOrChargeTwice(UnknownUsagePolicy mode)
    {
        var request = Request(new(new(8, 5), 50, 50, mode));
        using var cut = new RunCut(TimeProvider.System, request.Bounds.MaximumDuration, CancellationToken.None);
        var state = new RunState(request, new(new ScriptedProvider([Step(new())]), [], new RuntimeHooks()), new(), cut); state.Initialize();
        var first = state.AdmitTurn()!;
        var foreign = new ProviderAttempt(first.Attempt.ExecutionId, Guid.NewGuid(), first.Attempt.PhysicalAttemptId);
        Assert.Throws<ProviderContractException>(() => state.Retain(foreign,
            new(foreign.ExecutionId, foreign.LogicalCallId, foreign.PhysicalAttemptId, 1, DispatchExposure.NotDispatched, new())));
        Assert.Equal(8, state.Accounting()!.Input.ReservedTokens);
        var noSend = new UsageAttemptObservation(first.Attempt.ExecutionId, first.Attempt.LogicalCallId, first.Attempt.PhysicalAttemptId, 1,
            DispatchExposure.NotDispatched, new());
        state.Retain(first.Attempt, noSend); state.Retain(first.Attempt, noSend);
        var second = state.AdmitTurn()!;
        state.Retain(first.Attempt, noSend); // The old release cannot touch the new numeric/physical reservation.
        Assert.Equal(8, state.Accounting()!.Input.ReservedTokens);
        var lost = new UsageAttemptObservation(second.Attempt.ExecutionId, second.Attempt.LogicalCallId, second.Attempt.PhysicalAttemptId, 1,
            DispatchExposure.Unknown, new());
        state.Retain(second.Attempt, lost);
        state.Retain(second.Attempt, new(lost.ExecutionId, lost.LogicalCallId, lost.PhysicalAttemptId, 1, DispatchExposure.Unknown, new()));
        var before = state.Accounting()!;
        Assert.Throws<ProviderContractException>(() => state.Retain(second.Attempt,
            new(lost.ExecutionId, lost.LogicalCallId, lost.PhysicalAttemptId, 1, DispatchExposure.NotDispatched, new())));
        Assert.True(before.Matches(state.Accounting()!)); Assert.Equal(8, before.Input.AccountedTokens);
        Assert.Equal(2, state.Usage().Attempts.Count); Assert.Equal(0, before.Input.ReservedTokens);
    }
}

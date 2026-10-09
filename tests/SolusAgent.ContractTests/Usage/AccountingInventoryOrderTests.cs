using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.Usage;

public sealed class AccountingInventoryOrderTests
{
    public static IEnumerable<object[]> PoliciesAndMeasurements()
    {
        foreach (var policy in Enum.GetValues<UnknownUsagePolicy>())
        foreach (var mask in Enumerable.Range(0, 4)) yield return [policy, mask];
    }

    [Theory, MemberData(nameof(PoliciesAndMeasurements))]
    public void CompleteInventoriesMatchEveryPermutationWithoutReordering(UnknownUsagePolicy mode, int mask)
    {
        var (ledger, observations) = Inventory(mode, mask);
        foreach (var accountingOrder in Permutations(ledger.Attempts.ToArray()))
        foreach (var observationOrder in Permutations(observations))
        {
            var reordered = new RunAccountingSnapshot(ledger.ExecutionId, ledger.Policy, accountingOrder);
            var usage = new AgentRunUsage(ledger.ExecutionId, UsageInventoryCoverage.Complete, observationOrder, accounting: reordered);
            Assert.Equal(observationOrder, usage.Attempts);
            Assert.Equal(accountingOrder, usage.Accounting!.Attempts);
            Assert.True(ledger.Matches(reordered)); Assert.True(reordered.Matches(ledger));
            Assert.Equal(ledger.Input.AccountedTokens, reordered.Input.AccountedTokens);
            Assert.Equal(ledger.Output.Coverage, reordered.Output.Coverage);
        }
    }

    [Theory, MemberData(nameof(PoliciesAndMeasurements))]
    public void AssociationStillRejectsChangedIdentityOrdinalAmountsAndIncompleteState(UnknownUsagePolicy mode, int mask)
    {
        var (ledger, observations) = Inventory(mode, mask);
        var first = observations[0];
        foreach (var changed in new[]
        {
            new UsageAttemptObservation(Guid.NewGuid(), first.LogicalCallId, first.PhysicalAttemptId, 1, first.Exposure, first.Usage),
            new UsageAttemptObservation(first.ExecutionId, Guid.NewGuid(), first.PhysicalAttemptId, 1, first.Exposure, first.Usage),
            new UsageAttemptObservation(first.ExecutionId, first.LogicalCallId, Guid.NewGuid(), 1, first.Exposure, first.Usage),
            new UsageAttemptObservation(first.ExecutionId, first.LogicalCallId, first.PhysicalAttemptId, 2, first.Exposure, first.Usage),
            new UsageAttemptObservation(first.ExecutionId, first.LogicalCallId, first.PhysicalAttemptId, 1, first.Exposure, new(99, 99)),
        })
            Assert.Throws<ArgumentException>(() => new AgentRunUsage(ledger.ExecutionId, UsageInventoryCoverage.Complete,
                [observations[2], observations[1], changed], accounting: ledger));
        // Swapping retry ordinals keeps the complete inventory valid but breaks full identity correlation.
        var swapped = observations.Select(o => new UsageAttemptObservation(o.ExecutionId, o.LogicalCallId, o.PhysicalAttemptId,
            o.LogicalCallId == first.LogicalCallId ? 3 - o.AttemptNumber : o.AttemptNumber, o.Exposure, o.Usage)).ToArray();
        Assert.Throws<ArgumentException>(() => new AgentRunUsage(ledger.ExecutionId, UsageInventoryCoverage.Complete, swapped, accounting: ledger));
        Assert.Throws<ArgumentException>(() => new AgentRunUsage(ledger.ExecutionId, UsageInventoryCoverage.Complete, [first, first, observations[2]], accounting: ledger));
        Assert.Throws<ArgumentException>(() => new AgentRunUsage(ledger.ExecutionId, UsageInventoryCoverage.Complete, [first, observations[2]], accounting: ledger));
        Assert.Throws<ArgumentException>(() => new AgentRunUsage(ledger.ExecutionId, UsageInventoryCoverage.Partial, observations, accounting: ledger));
        var reserved = new AttemptAccounting(first.ExecutionId, first.LogicalCallId, first.PhysicalAttemptId, 1, ledger.Policy.Reservation,
            new(AccountingDisposition.Reserved, 8), new(AccountingDisposition.Reserved, 5));
        var unfinished = new RunAccountingSnapshot(ledger.ExecutionId, ledger.Policy, [ledger.Attempts[2], ledger.Attempts[1], reserved]);
        Assert.Throws<ArgumentException>(() => new AgentRunUsage(ledger.ExecutionId, UsageInventoryCoverage.Complete, observations, accounting: unfinished));
        Assert.False(ledger.Matches(unfinished));
        var wrongPolicy = new RunAccountingSnapshot(ledger.ExecutionId, new(new(8, 5), 101, 100, mode), ledger.Attempts);
        Assert.False(ledger.Matches(wrongPolicy));
    }

    [Theory, MemberData(nameof(PoliciesAndMeasurements))]
    public void HostReceiptsMatchReorderedHistoryButPreserveCurrentAttemptAndPriorValues(UnknownUsagePolicy mode, int mask)
    {
        var (ledger, observations) = Inventory(mode, mask);
        var current = ledger.Attempts[2];
        var reserved = new AttemptAccounting(current.ExecutionId, current.LogicalCallId, current.PhysicalAttemptId, current.AttemptNumber,
            current.Reservation, new(AccountingDisposition.Reserved, 8), new(AccountingDisposition.Reserved, 5));
        var before = new RunAccountingSnapshot(ledger.ExecutionId, ledger.Policy, [ledger.Attempts[0], ledger.Attempts[1], reserved]);
        var attempt = new ProviderAttempt(current.ExecutionId, current.LogicalCallId, current.PhysicalAttemptId, current.AttemptNumber);
        var exposure = new RuntimeExposure(new("p", "m"), attempt, ExposureStrength.Durable, before);
        var reorderedBefore = new RunAccountingSnapshot(ledger.ExecutionId, ledger.Policy, [ledger.Attempts[1], ledger.Attempts[0], reserved]);
        var receiptExposure = new RuntimeExposure(exposure.Scope, attempt, exposure.RequiredAcknowledgement, reorderedBefore);
        Assert.Equal(RuntimeStop.None, new ExposureAcknowledgement(receiptExposure, RuntimeHookStatus.Acknowledged,
            ExposureDecision.Permit, ExposureStrength.Durable).Assess(exposure));
        var reorderedAfter = new RunAccountingSnapshot(ledger.ExecutionId, ledger.Policy, [ledger.Attempts[1], ledger.Attempts[0], current]);
        var settlement = new RuntimeSettlement(exposure, observations[2], RuntimeStop.None, true,
            ProviderOutcome.Succeeded, ProviderError.None, reorderedAfter);
        foreach (var permutation in Permutations(ledger.Attempts.ToArray()))
            Assert.Equal(RuntimeStop.None, new SettlementAcknowledgement(receiptExposure, RuntimeHookStatus.Acknowledged,
                RuntimeContinuation.Continue, new(ledger.ExecutionId, ledger.Policy, permutation)).Assess(settlement));
        Assert.Throws<ArgumentException>(() => new RuntimeExposure(exposure.Scope, attempt, ExposureStrength.Durable,
            new(ledger.ExecutionId, ledger.Policy, [reserved, ledger.Attempts[1], ledger.Attempts[0]])));
        Assert.Throws<ArgumentException>(() => new RuntimeSettlement(exposure, observations[2], RuntimeStop.None, true,
            ProviderOutcome.Succeeded, ProviderError.None, new(ledger.ExecutionId, ledger.Policy, [current, ledger.Attempts[1], ledger.Attempts[0]])));
        var prior = ledger.Attempts[0];
        foreach (var changed in new[]
        {
            new AttemptAccounting(prior.ExecutionId, prior.LogicalCallId, Guid.NewGuid(), prior.AttemptNumber, prior.Reservation, prior.Input, prior.Output),
            new AttemptAccounting(prior.ExecutionId, prior.LogicalCallId, prior.PhysicalAttemptId, prior.AttemptNumber, prior.Reservation,
                new(AccountingDisposition.Measured, 99), prior.Output),
        })
        {
            var wrong = new RunAccountingSnapshot(ledger.ExecutionId, ledger.Policy, [ledger.Attempts[1], changed, current]);
            Assert.False(ledger.Matches(wrong));
            Assert.Equal(RuntimeStop.SettlementMismatch, new SettlementAcknowledgement(receiptExposure, RuntimeHookStatus.Acknowledged,
                RuntimeContinuation.Continue, wrong).Assess(settlement));
            Assert.Throws<ArgumentException>(() => new RuntimeSettlement(exposure, observations[2], RuntimeStop.None, true,
                ProviderOutcome.Succeeded, ProviderError.None, wrong));
        }
    }

    private static (RunAccountingSnapshot Ledger, UsageAttemptObservation[] Observations) Inventory(UnknownUsagePolicy mode, int mask)
    {
        var id = Guid.NewGuid(); var call = Guid.NewGuid(); var otherCall = Guid.NewGuid();
        var policy = new AgentAccountingPolicy(new(8, 5), 100, 100, mode);
        var observations = Enumerable.Range(0, 3).Select(i => new UsageAttemptObservation(id, i == 2 ? otherCall : call,
            Guid.NewGuid(), i == 2 ? 1 : i + 1, DispatchExposure.Dispatched,
            new((mask & 1) != 0 ? i + 1 : null, (mask & 2) != 0 ? i + 2 : null))).ToArray();
        AccountingDimension Axis(long? known, long reservation) => known is long value ? new(AccountingDisposition.Measured, value)
            : new(mode == UnknownUsagePolicy.ConservativeCharge ? AccountingDisposition.ConservativeCharge : AccountingDisposition.Unresolved, reservation);
        var entries = observations.Select(o => new AttemptAccounting(id, o.LogicalCallId, o.PhysicalAttemptId, o.AttemptNumber,
            policy.Reservation, Axis(o.Usage.InputTokens, 8), Axis(o.Usage.OutputTokens, 5))).ToArray();
        return (new(id, policy, entries), observations);
    }

    private static IEnumerable<T[]> Permutations<T>(T[] values)
    {
        foreach (var a in Enumerable.Range(0, 3))
        foreach (var b in Enumerable.Range(0, 3).Where(i => i != a))
            yield return [values[a], values[b], values[3 - a - b]];
    }
}

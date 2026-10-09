using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Providers;

namespace SolusAgent.Runtime.Execution;

// Per invocation, owned only by RunState. Provider accounting claims are never ledger inputs.
internal sealed class RunAccountingLedger(Guid executionId, AgentAccountingPolicy policy)
{
    private readonly List<AttemptAccounting> entries = [];
    public RunAccountingSnapshot Snapshot() => new(executionId, policy, entries);

    public (bool Limit, bool Unknown) Preflight()
    {
        var snapshot = Snapshot();
        var input = Compare(snapshot.Input, policy.Reservation.InputTokens);
        var output = Compare(snapshot.Output, policy.Reservation.OutputTokens);
        return (input.Limit || output.Limit, input.Unknown || output.Unknown);
    }
    private (bool Limit, bool Unknown) Compare(RunAccountingDimension value, long next)
    {
        if (value.AccountedTokens is not long debit || next > long.MaxValue - debit) return (true, false);
        if (value.Allowance is long allowance && debit + next > allowance) return (true, false);
        return (false, value.Allowance.HasValue && policy.UnknownUsage == UnknownUsagePolicy.Stop
            && value.Coverage == AccountingBalanceCoverage.Unknown);
    }
    public AttemptAccounting PrepareReservation(ProviderAttempt attempt) => new(attempt.ExecutionId, attempt.LogicalCallId,
        attempt.PhysicalAttemptId, attempt.AttemptNumber, policy.Reservation,
        new(AccountingDisposition.Reserved, policy.Reservation.InputTokens), new(AccountingDisposition.Reserved, policy.Reservation.OutputTokens));
    public void Reserve(AttemptAccounting entry) => entries.Add(entry);
    public void Settle(ProviderAttempt attempt, UsageAttemptObservation observation)
    {
        var index = entries.FindIndex(e => e.PhysicalAttemptId == attempt.PhysicalAttemptId);
        if (index < 0 || entries[index].IsFinalized) throw new ProviderContractException(ProviderError.InvalidAssociation);
        var original = entries[index];
        var settled = new AttemptAccounting(attempt.ExecutionId, attempt.LogicalCallId, attempt.PhysicalAttemptId, attempt.AttemptNumber,
            original.Reservation, SettleDimension(observation.Exposure, observation.Usage.InputTokens, original.Reservation.InputTokens),
            SettleDimension(observation.Exposure, observation.Usage.OutputTokens, original.Reservation.OutputTokens));
        entries[index] = settled;
    }
    private AccountingDimension SettleDimension(DispatchExposure exposure, long? measured, long reservation) =>
        exposure == DispatchExposure.NotDispatched ? new(AccountingDisposition.Released, 0)
        : measured is long known ? new(AccountingDisposition.Measured, known)
        : new(policy.UnknownUsage == UnknownUsagePolicy.ConservativeCharge ? AccountingDisposition.ConservativeCharge : AccountingDisposition.Unresolved, reservation);
}

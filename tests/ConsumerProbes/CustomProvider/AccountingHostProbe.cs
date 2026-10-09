using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Exposure;

namespace SolusAgent.ConsumerProbes.CustomProvider;

/// <summary>Independent Runtime.Api-only Host consuming numeric accounting through the actual ordered hook contract.</summary>
public sealed class AccountingHostProbe : IRuntimeExposureHooks
{
    public bool ReverseAccountingInventory { get; init; }
    public List<RuntimeExposure> Exposures { get; } = [];
    public List<RuntimeSettlement> Settlements { get; } = [];
    public ValueTask<ExposureAcknowledgement?> BeforeDispatchAsync(RuntimeExposure exposure, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Exposures.Add(exposure);
        var equivalent = new RuntimeExposure(exposure.Scope, exposure.Attempt, exposure.RequiredAcknowledgement,
            Reorder(Copy(exposure.Accounting), preserveCurrentLast: true));
        return ValueTask.FromResult<ExposureAcknowledgement?>(new(equivalent, RuntimeHookStatus.Acknowledged,
            ExposureDecision.Permit, exposure.RequiredAcknowledgement));
    }
    public ValueTask<SettlementAcknowledgement?> AfterAttemptAsync(RuntimeSettlement settlement, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Settlements.Add(settlement);
        return ValueTask.FromResult<SettlementAcknowledgement?>(new(settlement.Exposure, RuntimeHookStatus.Acknowledged,
            RuntimeContinuation.Continue, Reorder(Copy(settlement.Accounting), preserveCurrentLast: false)));
    }
    private RunAccountingSnapshot? Reorder(RunAccountingSnapshot? snapshot, bool preserveCurrentLast)
    {
        if (!ReverseAccountingInventory || snapshot is null || snapshot.Attempts.Count == 0) return snapshot;
        var entries = preserveCurrentLast ? snapshot.Attempts.SkipLast(1).Reverse().Append(snapshot.Attempts[^1]) : snapshot.Attempts.Reverse();
        return new(snapshot.ExecutionId, snapshot.Policy, entries.ToArray());
    }
    public static RunAccountingSnapshot? Copy(RunAccountingSnapshot? snapshot) => snapshot is null ? null : new(snapshot.ExecutionId,
        new(new(snapshot.Policy.Reservation.InputTokens, snapshot.Policy.Reservation.OutputTokens), snapshot.Policy.InputAllowance,
            snapshot.Policy.OutputAllowance, snapshot.Policy.UnknownUsage), snapshot.Attempts.Select(e =>
                new AttemptAccounting(e.ExecutionId, e.LogicalCallId, e.PhysicalAttemptId, e.AttemptNumber,
                    new(e.Reservation.InputTokens, e.Reservation.OutputTokens), new(e.Input.Disposition, e.Input.Amount),
                    new(e.Output.Disposition, e.Output.Amount))).ToArray());
}

using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Context;
using SolusAgent.Runtime.Tools;
using SolusAgent.Tools.Api;

namespace SolusAgent.Runtime.Execution;

internal sealed partial class RunState
{
    internal ProviderAttempt? RestoredToolCursor => restored?.Checkpoint.ToolCursor;
    private readonly Dictionary<int, int> recoveryReservations = [];
    internal bool ReserveRecoveryBatch(IReadOnlyList<int> indices)
    {
        var history = restored?.Checkpoint.Rounds.Sum(r => r.RecoveryActions?.Length ?? 0) ?? 0;
        if (indices.Count == 0) return true;
        if (records.Count + history + recoveryActions.Count > Options.MaximumRecords - indices.Count
            || toolRecords.Count + history + recoveryActions.Count > Options.MaximumRecords - indices.Count)
        { Close(RuntimeStop.ResourceLimit); return false; }
        long bytes = 0;
        foreach (var index in indices)
        {
            var member = toolRecords[index];
            var descriptor = Configuration.Tools.Single(t => t.Descriptor.Name == member.Call.ToolName).Descriptor;
            var reservation = OrdinaryContextCodec.RecoveryReservationBytes(index, member.Call, descriptor);
            bytes += reservation;
            recoveryReservations.Add(index, reservation);
        }
        if (bytes > Options.MaximumRetainedBytes - retainedBytes)
        { recoveryReservations.Clear(); Close(RuntimeStop.ResourceLimit); return false; }
        lock (toolUsageGate)
        {
            if (Request.UsageLimits?.MaximumToolInvocations is { } limit && indices.Count > limit - invokedTools - reservedTools)
            { recoveryReservations.Clear(); Close(RuntimeStop.ResourceLimit); return false; }
            foreach (var index in indices) toolRecords[index] = toolRecords[index] with { ReservationHeld = true };
            reservedTools += indices.Count;
        }
        return true;
    }
    internal int EnterRecovery(int memberIndex, RecoveryActionKind kind)
    {
        lock (toolUsageGate)
        {
            var member = toolRecords[memberIndex];
            if (!member.ReservationHeld || !recoveryReservations.Remove(memberIndex, out var bytes))
                throw new ProviderContractException(ProviderError.InvalidAssociation);
            reservedTools--; invokedTools++; retainedBytes += bytes;
            var action = recoveryActions.Count;
            recoveryActions.Add(new(Guid.NewGuid(), memberIndex, kind, RecoveryActionState.Unknown));
            toolRecords[memberIndex] = member with { ReservationHeld = false };
            if (kind != RecoveryActionKind.Query)
            {
                toolRecords[memberIndex] = toolRecords[memberIndex] with { State = ToolMemberState.InvokedUnknown, Result = null, Error = ToolError.None };
                records.RemoveAll(r => r.ToolResult?.Call.Matches(member.Call) == true);
            }
            return action;
        }
    }
    internal void FinishRecovery(int actionIndex, RecoveryActionState observation, ToolResult? result = null,
        ToolError error = ToolError.None)
    {
        var action = recoveryActions[actionIndex];
        recoveryActions[actionIndex] = action with { State = observation, Result = result, Error = error };
        var member = toolRecords[action.MemberIndex];
        if (result is not null)
        {
            var phase = result.Outcome switch { ToolOutcome.Succeeded => ToolMemberState.Succeeded,
                ToolOutcome.Cancelled => ToolMemberState.Cancelled, ToolOutcome.Rejected => ToolMemberState.Rejected, _ => ToolMemberState.Failed };
            toolRecords[action.MemberIndex] = member with { State = phase, Result = result, Error = result.Error };
            var position = records.FindIndex(r => r.ToolResult?.Call.Matches(member.Call) == true);
            if (position >= 0) records[position] = ProviderInput.FromTool(result);
            else records.Add(ProviderInput.FromTool(result));
        }
        else if (action.Kind != RecoveryActionKind.Query && observation is RecoveryActionState.Failed or RecoveryActionState.Rejected)
            toolRecords[action.MemberIndex] = member with { State = observation == RecoveryActionState.Failed ? ToolMemberState.Failed : ToolMemberState.Rejected,
                Result = null, Error = error };
    }
    internal void ReleaseRecoveryBatch(IReadOnlyList<int> indices)
    {
        lock (toolUsageGate)
            foreach (var index in indices)
            {
                if (!toolRecords[index].ReservationHeld) continue;
                toolRecords[index] = toolRecords[index] with { ReservationHeld = false };
                recoveryReservations.Remove(index); reservedTools--; releasedTools++; recoveryReleased.Add(index);
            }
    }
}

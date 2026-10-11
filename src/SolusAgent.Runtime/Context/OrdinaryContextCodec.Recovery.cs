using System.Text.Json;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Runtime.Tools;
using SolusAgent.Tools.Api;

namespace SolusAgent.Runtime.Context;

internal static partial class OrdinaryContextCodec
{
    // The reservation includes worst-case JSON escaping in retained audit facts, not just provider payload bytes.
    internal static int RecoveryReservationBytes(int index, ToolCall call, ToolDescriptor descriptor)
    {
        var action = new SavedRecoveryAction(Guid.NewGuid(), index, RecoveryActionKind.Query, RecoveryActionState.Returned,
            new(call, ToolOutcome.Succeeded, ToolError.None, true, new string('"', descriptor.MaximumResultBytes)), ToolError.None);
        return checked(JsonSerializer.SerializeToUtf8Bytes(action, Json).Length + CallBytes(call) + descriptor.MaximumResultBytes + 64);
    }
    private static ToolMemberState ResultState(SavedResult result) => result.Outcome switch
    {
        ToolOutcome.Succeeded => ToolMemberState.Succeeded, ToolOutcome.Cancelled => ToolMemberState.Cancelled,
        ToolOutcome.Failed => ToolMemberState.Failed, ToolOutcome.Rejected => ToolMemberState.Rejected, _ => throw new JsonException()
    };
    private static void ValidateMember(SavedMember member, RuntimeConfiguration config)
    {
        var registration = config.Tools.Single(t => t.Descriptor.Name == member.Call.ToolName);
        if (!Enum.IsDefined(member.State) || !Enum.IsDefined(member.Error) || member.ReservationHeld
            || member.Operation.OperationId == Guid.Empty || !member.Operation.Call.Matches(member.Call)
            || (member.Operation.Recovery is null ? registration.Recovery is not null : !member.Operation.Recovery.Matches(registration.Recovery)))
            throw new JsonException();
        if (member.Result is not { } result)
        {
            if (member.State is ToolMemberState.Succeeded or ToolMemberState.Cancelled
                || member.State is ToolMemberState.Unstarted or ToolMemberState.InvokedUnknown && member.Error != ToolError.None
                || member.State is ToolMemberState.Failed or ToolMemberState.Rejected && member.Error == ToolError.None) throw new JsonException();
            return;
        }
        if (member.State != ResultState(result) || !result.Call.Matches(member.Call) || result.Error != member.Error
            || !Enum.IsDefined(result.Error)) throw new JsonException();
        if (result.Outcome == ToolOutcome.Succeeded)
        {
            if (result.Error != ToolError.None || !result.InvocationStarted || result.Json is null
                || registration.Descriptor.ResultSchema.Validate(result.Json, registration.Descriptor.MaximumResultBytes) != ToolError.None) throw new JsonException();
        }
        else if (result.Error == ToolError.None || result.Json is not null
            || result.Outcome == ToolOutcome.Cancelled && result.Error != ToolError.Cancelled
            || result.Outcome != ToolOutcome.Cancelled && result.Error == ToolError.Cancelled
            || result.Outcome == ToolOutcome.Failed && !result.InvocationStarted && result.Error != ToolError.InvocationFailed
            || result.Outcome == ToolOutcome.Rejected && (result.InvocationStarted
                ? result.Error is not (ToolError.InvalidResult or ToolError.ResultMismatch)
                : result.Error is not (ToolError.CallMismatch or ToolError.PreparedMismatch or ToolError.UnsupportedCapability or ToolError.AlreadyInvoked)))
            throw new JsonException();
    }
    private static SavedMember[] EffectiveMembers(OrdinaryCheckpoint saved, RuntimeConfiguration config, RuntimeOptions options,
        out Dictionary<Guid, ProviderAttempt?> cursors)
    {
        cursors = [];
        var effective = saved.Members.ToArray();
        var identities = new HashSet<Guid>();
        foreach (var member in saved.Members)
        {
            ValidateMember(member, config);
            if (!identities.Add(member.Operation.OperationId)) throw new JsonException();
        }
        var actionIds = new HashSet<Guid>();
        var terminalNegativeQueries = new HashSet<int>();
        SavedRound? previous = null;
        var totalActions = 0;
        foreach (var round in saved.Rounds)
        {
            var actions = round.RecoveryActions ?? throw new JsonException();
            var released = round.RecoveryReleased ?? throw new JsonException();
            totalActions = checked(totalActions + actions.Length);
            if (totalActions > options.MaximumRecords - saved.Members.Length) throw new JsonException();
            var touched = new HashSet<int>();
            foreach (var action in actions)
            {
                if (previous?.ToolCursor is null || action.ActionId == Guid.Empty || !actionIds.Add(action.ActionId)
                    || action.MemberIndex < 0 || action.MemberIndex >= effective.Length || !touched.Add(action.MemberIndex)
                    || terminalNegativeQueries.Contains(action.MemberIndex)
                    || !Enum.IsDefined(action.Kind) || !Enum.IsDefined(action.State) || !Enum.IsDefined(action.Error)) throw new JsonException();
                var member = effective[action.MemberIndex];
                if (!member.ModelAttempt.Matches(previous.ToolCursor) || member.State == ToolMemberState.Succeeded
                    || member.ModelAttempt.ExecutionId == round.Usage.ExecutionId
                    || effective.Any(m => m.ModelAttempt.Matches(member.ModelAttempt) && m.Ordinal < member.Ordinal && m.State != ToolMemberState.Succeeded)) throw new JsonException();
                if (action.Kind == RecoveryActionKind.Start ? member.State != ToolMemberState.Unstarted
                    : member.State == ToolMemberState.Unstarted || member.Operation.Recovery?.Guarantee != (action.Kind == RecoveryActionKind.Repeat
                        ? ToolRecoveryGuarantee.ConcurrentSafeRepeat : ToolRecoveryGuarantee.QueryOutcome)) throw new JsonException();
                if ((action.State == RecoveryActionState.Returned) != (action.Result is not null)
                    || action.State is RecoveryActionState.Unknown or RecoveryActionState.Unresolved && action.Error != ToolError.None
                    || action.State == RecoveryActionState.Unresolved && action.Kind != RecoveryActionKind.Query
                    || action.State is RecoveryActionState.Rejected or RecoveryActionState.Failed && action.Error == ToolError.None
                    || action.State == RecoveryActionState.Failed && action.Error != ToolError.InvocationFailed) throw new JsonException();
                if (action.Result is { } result)
                {
                    if (action.Error != result.Error) throw new JsonException();
                    member = member with { State = ResultState(result), Result = result, Error = result.Error };
                    if (action.Kind == RecoveryActionKind.Query && result.Outcome != ToolOutcome.Succeeded) terminalNegativeQueries.Add(action.MemberIndex);
                }
                else if (action.Kind != RecoveryActionKind.Query)
                    member = member with { State = action.State switch { RecoveryActionState.Failed => ToolMemberState.Failed,
                        RecoveryActionState.Rejected => ToolMemberState.Rejected, _ => ToolMemberState.InvokedUnknown }, Result = null, Error = action.Error };
                ValidateMember(member, config); effective[action.MemberIndex] = member;
            }
            if (released.Distinct().Count() != released.Length || released.Any(i => i < 0 || i >= effective.Length || touched.Contains(i)
                || previous?.ToolCursor is null || !effective[i].ModelAttempt.Matches(previous.ToolCursor) || effective[i].State == ToolMemberState.Succeeded)) throw new JsonException();
            var owned = saved.Members.Where(m => m.ModelAttempt.ExecutionId == round.Usage.ExecutionId).ToArray();
            if (round.Usage.ToolInvocations is not { ReservedUnstarted: 0 } usage
                || usage.Invoked != owned.Count(m => m.State != ToolMemberState.Unstarted) + actions.Length
                || usage.ReleasedUnstarted != owned.Count(m => m.State == ToolMemberState.Unstarted) + released.Length) throw new JsonException();
            if (round.Facts.Length != 0 && previous?.ToolCursor is { } pending && effective.Any(m => m.ModelAttempt.Matches(pending)
                && m.State != ToolMemberState.Succeeded)) throw new JsonException();
            var tail = round.Facts.LastOrDefault();
            var cursor = tail is null ? previous?.ToolCursor is not null && previous.LogicalWorkId == round.LogicalWorkId ? previous.ToolCursor : null
                : tail is { AcceptedTools: true, Stop: RuntimeStop.None, SettlementStop: RuntimeStop.None, ClosureAcknowledged: true }
                    && Resumable(saved, effective, tail.Attempt) ? tail.Attempt : null;
            if (cursor is not null && terminalNegativeQueries.Any(i => effective[i].ModelAttempt.Matches(cursor))) cursor = null;
            cursors.Add(round.RoundId, cursor); previous = round;
        }
        return effective;
    }
    private static bool Resumable(OrdinaryCheckpoint saved, SavedMember[] effective, ProviderAttempt model)
    {
        var calls = saved.Records.SingleOrDefault(r => r.ToolModel?.Attempt.Matches(model) == true)?.ToolModel?.Calls;
        var members = effective.Where(m => m.ModelAttempt.Matches(model)).ToArray();
        return calls is not null && calls.Length == members.Length && members.All(m => m.State == ToolMemberState.Succeeded && m.Result is not null
            || m.State == ToolMemberState.Unstarted || m.Operation.Recovery is not null);
    }
}

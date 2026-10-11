using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;

namespace SolusAgent.Runtime.Tools;

internal enum ToolMemberState { Unstarted, InvokedUnknown, Succeeded, Rejected, Failed, Cancelled }

// Restricted run-owned data, never an ordinary progress/outcome or durable effect receipt.
internal sealed record ToolExecutionRecord(ProviderAttempt ModelAttempt, int Ordinal, ToolCall Call,
    ToolMemberState State, ToolResult? Result = null, ToolError Error = ToolError.None, bool ReservationHeld = false,
    ToolOperation? Operation = null)
{
    public bool? InvocationStarted => State == ToolMemberState.Unstarted ? false : Result?.InvocationStarted;
}

internal enum RecoveryActionKind { Start, Repeat, Query }
internal enum RecoveryActionState { Unknown, Unresolved, Returned, Rejected, Failed }
internal sealed record RecoveryExecutionRecord(Guid ActionId, int MemberIndex, RecoveryActionKind Kind,
    RecoveryActionState State, ToolResult? Result = null, ToolError Error = ToolError.None);

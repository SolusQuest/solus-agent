using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Tools;
using SolusAgent.Tools.Api;

namespace SolusAgent.Runtime.Context;

internal sealed record SavedContinuation(ProviderScope Scope, ProviderAttempt Origin, byte[] Bytes)
{
    public static SavedContinuation? From(ProviderContinuation? value) => value is null ? null : new(value.Scope, value.Origin, value.CopyReplayBytes());
    public ProviderContinuation Restore() => new(Scope, Origin, Bytes);
}
internal sealed record SavedFinal(ProviderScope Scope, ProviderAttempt Attempt, string Text, SavedContinuation? Continuation, ProviderExchangeBounds Bounds);
internal sealed record SavedToolModel(ProviderScope Scope, ProviderAttempt Attempt, string? Text, ToolCall[] Calls,
    SavedContinuation? Continuation, ProviderExchangeBounds Bounds);
internal sealed record SavedDescriptor(string Name, string Description, string InputSchema, string ResultSchema,
    string CapabilityId, ToolEffect Effect, int MaximumArgumentBytes, int MaximumResultBytes)
{
    internal static SavedDescriptor From(ToolDescriptor d) => new(d.Name, d.Description, d.InputSchema.NormalizedJson,
        d.ResultSchema.NormalizedJson, d.CapabilityId, d.Effect, d.MaximumArgumentBytes, d.MaximumResultBytes);
    internal ToolDescriptor Restore() => new(Name, Description, ToolSchema.Parse(InputSchema), ToolSchema.Parse(ResultSchema),
        CapabilityId, Effect, MaximumArgumentBytes, MaximumResultBytes);
}
internal sealed record SavedResult(ToolCall Call, ToolOutcome Outcome, ToolError Error, bool InvocationStarted, string? Json)
{
    internal static SavedResult? From(ToolResult? result) => result is null ? null
        : new(result.Call, result.Outcome, result.Error, result.InvocationStarted, result.Json);
}
internal sealed record SavedMember(ProviderAttempt ModelAttempt, int Ordinal, ToolCall Call, SavedDescriptor Descriptor,
    ToolMemberState State, SavedResult? Result, ToolError Error, bool ReservationHeld);
internal sealed record SavedInput(ProviderInputKind Kind, string? Text, SavedFinal? Final,
    SavedToolModel? ToolModel = null, int? ToolMember = null);
internal sealed record SavedProvider(ProviderScope Scope, ProviderAttempt Origin, int FormatVersion, byte[] Bytes);
internal sealed record SavedAttempt(ProviderAttempt Attempt, RuntimeStop Stop, RuntimeStop SettlementStop,
    ProviderOutcome? Outcome, ProviderError? Error, ProviderRetry? Retry, bool AcceptedFinal, bool ClosureAcknowledged,
    ProviderExchangeBounds Bounds, bool AcceptedTools = false);
internal sealed record SavedRound(Guid LogicalWorkId, Guid RoundId, AgentRunUsage Usage, AgentTerminationReason Reason,
    int Completed, RuntimeStop Stop, SavedAttempt[] Facts, ProviderAttempt? ToolCursor = null)
{
    public ContextRoundObservation Observe() => new(LogicalWorkId, RoundId, Usage, Reason, Completed);
}
internal sealed record OrdinaryCheckpoint(ContextCheckpointInfo Info, ProviderScope Scope, string Binding,
    SavedInput[] Records, SavedRound[] Rounds, ProviderExchangeBounds? OriginalBounds, SavedProvider? Provider,
    bool Final, bool RetryEligible, ProviderAttempt? Pending, TimeSpan Elapsed, SavedMember[] Members, ProviderAttempt? ToolCursor);
internal sealed record RestoredOrdinary(OrdinaryCheckpoint Checkpoint, IReadOnlyList<ProviderInput> Records,
    ProviderContinuation? Continuation, ProviderAttempt? Pending, Guid WorkId, Guid RoundId, int RetainedBytes,
    int ProspectiveBytes, bool Deferred = false, IReadOnlyList<ToolExecutionRecord>? Members = null)
{
    public IReadOnlyList<UsageCallLineage> Seeds { get; } = Pending is null ? [] : [new(Pending.LogicalCallId, Pending.ExecutionId, Pending.PhysicalAttemptId, Pending.AttemptNumber)];
    public ProviderExchangeBounds? OriginalBounds => Checkpoint.OriginalBounds;
    public ProviderRetry? Retry => Pending is null ? null : Checkpoint.Rounds.SelectMany(r => r.Facts).Single(f => f.Attempt.Matches(Pending)).Retry;
}

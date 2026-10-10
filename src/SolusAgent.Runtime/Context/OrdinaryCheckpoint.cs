using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Api.Exposure;

namespace SolusAgent.Runtime.Context;

internal sealed record SavedContinuation(ProviderScope Scope, ProviderAttempt Origin, byte[] Bytes)
{
    public static SavedContinuation? From(ProviderContinuation? value) => value is null ? null : new(value.Scope, value.Origin, value.CopyReplayBytes());
    public ProviderContinuation Restore() => new(Scope, Origin, Bytes);
}
internal sealed record SavedFinal(ProviderScope Scope, ProviderAttempt Attempt, string Text, SavedContinuation? Continuation);
internal sealed record SavedInput(ProviderInputKind Kind, string? Text, SavedFinal? Final);
internal sealed record SavedProvider(ProviderScope Scope, ProviderAttempt Origin, int FormatVersion, byte[] Bytes);
internal sealed record SavedAttempt(ProviderAttempt Attempt, RuntimeStop Stop, RuntimeStop SettlementStop,
    ProviderOutcome? Outcome, ProviderError? Error, ProviderRetry? Retry, bool AcceptedFinal);
internal sealed record SavedRound(Guid LogicalWorkId, Guid RoundId, AgentRunUsage Usage, AgentTerminationReason Reason,
    int Completed, RuntimeStop Stop, SavedAttempt[] Facts)
{
    public ContextRoundObservation Observe() => new(LogicalWorkId, RoundId, Usage, Reason, Completed);
}
internal sealed record OrdinaryCheckpoint(ContextCheckpointInfo Info, ProviderScope Scope, string Binding,
    SavedInput[] Records, SavedRound[] Rounds, ProviderExchangeBounds? OriginalBounds, SavedProvider? Provider,
    bool Final, bool RetryEligible, TimeSpan Elapsed);
internal sealed record RestoredOrdinary(OrdinaryCheckpoint Checkpoint, IReadOnlyList<ProviderInput> Records,
    ProviderContinuation? Continuation, ProviderAttempt? Pending, Guid WorkId, Guid RoundId, int RetainedBytes)
{
    public IReadOnlyList<UsageCallLineage> Seeds { get; } = Pending is null ? [] : [new(Pending.LogicalCallId, Pending.ExecutionId, Pending.PhysicalAttemptId, Pending.AttemptNumber)];
    public ProviderExchangeBounds? OriginalBounds => Checkpoint.OriginalBounds;
}

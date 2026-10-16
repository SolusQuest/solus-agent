using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Context;
using SolusAgent.Runtime.Tools;

namespace SolusAgent.Runtime.Execution;

internal sealed partial class RunState
{
    private readonly List<SavedAttempt> contextFacts = [];
    private readonly Guid contextRound = restored?.RoundId ?? Guid.NewGuid();
    private readonly Guid logicalWork = restored?.WorkId ?? Guid.NewGuid();
    private RuntimeStop? contextStop;
    private TimeSpan contextElapsed;
    internal int HistoricalAttemptCount => restored?.Checkpoint.Rounds.Sum(r => r.Usage.Attempts.Count) ?? 0;
    internal int RoundAttemptNumber(ProviderAttempt attempt) => attempt.AttemptNumber
        - (restored?.Seeds.SingleOrDefault(s => s.LogicalCallId == attempt.LogicalCallId)?.AttemptNumber ?? 0);
    private ProviderHistory? RequestHistory(ProviderAttempt attempt) => restored is null ? null : new(
        records.Where(r => r.Model is not null).Select(r => r.Model!.Attempt).ToArray(),
        restored.Pending?.LogicalCallId == attempt.LogicalCallId ? restored.Pending : null);
    internal void RecordAttempt(ProviderRequest providerRequest, AttemptExecution result) => contextFacts.Add(new(providerRequest.Attempt,
        result.Stop, result.SettlementStop, result.ProviderOutcome, result.ProviderError, result.Retry, CommittedFinish(providerRequest.Attempt) == ProviderFinish.Final,
        result.ClosureAcknowledged, providerRequest.Bounds, CommittedFinish(providerRequest.Attempt) == ProviderFinish.ToolCalls,
        CommittedFinish(providerRequest.Attempt) == ProviderFinish.ToolCorrection,
        producedResponses.TryGetValue(providerRequest.Attempt.PhysicalAttemptId, out var finish) ? finish : null));
    internal void FreezeContext(AgentOutcome outcome)
    {
        if (contextStop.HasValue) return;
        contextStop = outcome.Reason == AgentTerminationReason.Cancelled ? RuntimeStop.Cancelled
            : outcome.Reason == AgentTerminationReason.ResourceLimit && Cut.Check() == RuntimeStop.DurationLimit ? RuntimeStop.DurationLimit
            : AdmissionStop;
        var elapsed = Request.Bounds.MaximumDuration - Cut.Remaining;
        contextElapsed = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }
    internal IReadOnlyList<ContextRoundObservation> ContextHistory => restored?.Checkpoint.Rounds.Select(r => r.Observe()).ToArray() ?? [];
    internal AgentContextEnvelope? CaptureOrdinary(AgentOutcome outcome, out ContextCheckpointInfo? info,
        SavedCandidates? candidates = null, SavedCandidateRound? candidateRound = null)
    {
        info = null;
        FreezeContext(outcome);
        if (contextFacts.Count != attempts.Count || records.Count == 0) return null;
        var last = contextFacts.LastOrDefault();
        var facts = contextFacts.Concat(restored?.Checkpoint.Rounds.SelectMany(r => r.Facts) ?? []).ToArray();
        var members = SaveMembers();
        var inputs = SaveInputs(records, facts);
        SavedProvider? provider = latestRequest is null ? restored?.Checkpoint.Provider : null;
        if (latestRequest is not null && Configuration.Provider is IProviderContextPersistence persistence)
        {
            var exported = persistence.ExportContext(new(Configuration.Scope, latestRequest.Attempt, continuation));
            if (exported is not null)
            {
                if (!exported.Scope.Matches(Configuration.Scope) || !exported.Origin.Matches(latestRequest.Attempt)) return null;
                provider = new(exported.Scope, exported.Origin, exported.FormatVersion, exported.CopyRestrictedPayload());
            }
        }
        var toolCursor = last is null ? restored?.Checkpoint.ToolCursor
            : last is { AcceptedTools: true, Stop: RuntimeStop.None, SettlementStop: RuntimeStop.None, ClosureAcknowledged: true }
                ? last.Attempt : null;
        if (toolCursor is not null && (!ResumableBatch(toolCursor)
            || recoveryActions.Any(a => a.Kind == RecoveryActionKind.Query && a.State == RecoveryActionState.Returned
                && a.Result?.Outcome != SolusAgent.Tools.Api.ToolOutcome.Succeeded)
            || restored?.Checkpoint.Rounds.SelectMany(r => r.RecoveryActions ?? []).Any(a => a.Kind == RecoveryActionKind.Query
                && a.State == RecoveryActionState.Returned && a.Result?.Outcome != SolusAgent.Tools.Api.ToolOutcome.Succeeded
                && toolRecords[a.MemberIndex].ModelAttempt.Matches(toolCursor)) == true)) toolCursor = null;
        var round = new SavedRound(logicalWork, contextRound, Usage(), outcome.Reason, Completed,
            contextStop!.Value, contextFacts.ToArray(), toolCursor,
            recoveryActions.Select(a => new SavedRecoveryAction(a.ActionId, a.MemberIndex, a.Kind, a.State, SavedResult.From(a.Result), a.Error)).ToArray(),
            recoveryReleased.ToArray(), candidateRound);
        var history = restored?.Checkpoint.Rounds ?? [];
        if (history.Length >= 64) return null;
        var checkpoint = new ContextCheckpointInfo(Guid.NewGuid(), logicalWork, contextRound, Request.ExecutionId);
        var pending = last is null ? restored?.Pending
            : last is { Retry: not null, AcceptedFinal: false, SettlementStop: RuntimeStop.None, ClosureAcknowledged: true }
                && attempts[^1].Exposure == SolusAgent.Api.Usage.DispatchExposure.Dispatched ? last.Attempt : null;
        var value = new OrdinaryCheckpoint(checkpoint, Configuration.Scope, OrdinaryContextCodec.Binding(Configuration, Options), inputs,
            [.. history, round], latestRequest?.Bounds ?? restored?.OriginalBounds, provider,
            last is { AcceptedFinal: true, SettlementStop: RuntimeStop.None or RuntimeStop.HostStopped },
            pending is not null, pending,
            contextElapsed, members, toolCursor, candidates);
        var envelope = OrdinaryContextCodec.Encode(value, Options);
        info = checkpoint;
        return envelope;

        bool ResumableBatch(ProviderAttempt model)
        {
            var calls = records.Single(r => r.Model?.Attempt.Matches(model) == true).Model!.Calls;
            var batch = toolRecords.Where(m => m.ModelAttempt.Matches(model)).ToArray();
            return calls.Count == batch.Length && batch.All(m => m.State == ToolMemberState.Succeeded && m.Result is not null
                || m.State == ToolMemberState.Unstarted || m.Operation?.Recovery is not null);
        }
    }
    internal async ValueTask<bool> WaitForRestoredRetryAsync()
    {
        if (restored?.Pending is null) return true;
        var policy = Request.UsageLimits!.RetryPolicy!;
        var retry = restored.Retry!;
        var delay = policy.Backoff;
        if (policy.HonorRetryAfter && retry.RetryAfter is { } hint)
        {
            if (hint > policy.MaximumDelay) { Close(RuntimeStop.ResourceLimit); return false; }
            if (hint > delay) delay = hint;
        }
        if (!PreflightRetry() || !Cut.TryStart(async () => { await Task.Delay(delay, Cut.Clock, Cut.Token).ConfigureAwait(false); return true; }, out var pending)) return false;
        var result = await Cut.WaitAsync(pending!).ConfigureAwait(false);
        return result.Obtained;
    }
}

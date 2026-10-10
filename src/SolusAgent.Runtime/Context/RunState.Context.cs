using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Context;

namespace SolusAgent.Runtime.Execution;

internal sealed partial class RunState
{
    private readonly List<SavedAttempt> contextFacts = [];
    private readonly Guid contextRound = restored?.RoundId ?? Guid.NewGuid();
    private readonly Guid logicalWork = restored?.WorkId ?? Guid.NewGuid();
    internal int HistoricalAttemptCount => restored?.Checkpoint.Rounds.Sum(r => r.Usage.Attempts.Count) ?? 0;
    internal int RoundAttemptNumber(ProviderAttempt attempt) => attempt.AttemptNumber
        - (restored?.Seeds.SingleOrDefault(s => s.LogicalCallId == attempt.LogicalCallId)?.AttemptNumber ?? 0);
    private ProviderHistory? RequestHistory(ProviderAttempt attempt) => restored is null ? null : new(
        records.Where(r => r.Model is not null).Select(r => r.Model!.Attempt).ToArray(),
        restored.Pending?.LogicalCallId == attempt.LogicalCallId ? restored.Pending : null);
    internal void RecordAttempt(ProviderRequest providerRequest, AttemptExecution result) => contextFacts.Add(new(providerRequest.Attempt,
        result.Stop, result.SettlementStop, result.ProviderOutcome, result.ProviderError, result.Retry, result.Response?.Finish == ProviderFinish.Final,
        Configuration.Hooks is not null && result.SettlementStop is RuntimeStop.None or RuntimeStop.HostStopped));
    internal IReadOnlyList<ContextRoundObservation> ContextHistory => restored?.Checkpoint.Rounds.Select(r => r.Observe()).ToArray() ?? [];
    internal AgentContextEnvelope? CaptureOrdinary(AgentOutcome outcome, out ContextCheckpointInfo? info)
    {
        info = null;
        if (toolRecords.Count != 0 || records.Any(r => r.Kind == ProviderInputKind.ToolResultData || r.Model?.Finish == ProviderFinish.ToolCalls)
            || contextFacts.Count != attempts.Count || records.Count == 0) return null;
        var last = contextFacts.LastOrDefault();
        var inputs = records.Select(r => new SavedInput(r.Kind, r.Text, r.Model is { } model
            ? new(model.Scope, model.Attempt, model.Text!, SavedContinuation.From(model.Continuation)) : null)).ToArray();
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
        var round = new SavedRound(logicalWork, contextRound, Usage(), outcome.Reason, Completed,
            Cut.Check() != RuntimeStop.None ? Cut.Check() : AdmissionStop, contextFacts.ToArray());
        var history = restored?.Checkpoint.Rounds ?? [];
        if (history.Length >= 64) return null;
        var checkpoint = new ContextCheckpointInfo(Guid.NewGuid(), logicalWork, contextRound, Request.ExecutionId);
        var elapsed = Request.Bounds.MaximumDuration - Cut.Remaining;
        var pending = last is null ? restored?.Pending
            : last is { Retry: not null, AcceptedFinal: false, SettlementStop: RuntimeStop.None, ClosureAcknowledged: true }
                && attempts[^1].Exposure == SolusAgent.Api.Usage.DispatchExposure.Dispatched ? last.Attempt : null;
        var value = new OrdinaryCheckpoint(checkpoint, Configuration.Scope, OrdinaryContextCodec.Binding(Configuration, Options), inputs,
            [.. history, round], latestRequest?.Bounds ?? restored?.OriginalBounds, provider,
            last is { AcceptedFinal: true, SettlementStop: RuntimeStop.None or RuntimeStop.HostStopped },
            pending is not null, pending,
            elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed);
        var envelope = OrdinaryContextCodec.Encode(value, Options);
        info = checkpoint;
        return envelope;
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

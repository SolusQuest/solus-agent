using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Context;
using SolusAgent.Runtime.Execution;
using SolusAgent.Runtime.Startup;
using SolusAgent.Runtime.Tools;

namespace SolusAgent.Runtime.Candidates;

internal static class CandidateExecutionDriver
{
    public static async ValueTask<CandidateExecutionResult> ExecuteAsync(RuntimeConfiguration configuration, RuntimeOptions options,
        CandidateExecutionRequest request, ICandidateHost host, IProgress<AgentProgress>? progress, CancellationToken cancellationToken,
        RestoredOrdinary? restored = null, bool newRun = false,
        Action<RunState, CandidateExecutionResult, SavedCandidates, SavedCandidateRound>? terminal = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);
        var execution = request.Execution;
        var unsupported = execution.RequiredCapabilities & ~RuntimeAgentFactory.Support.SupportedCapabilities;
        if (unsupported != AgentCapability.None)
            return new(new(execution.ExecutionId, AgentTerminationReason.UnsupportedCapability, 0, unsupported,
                usage: new AgentRunUsage(execution.ExecutionId, UsageInventoryCoverage.Complete, [], toolInvocations: new(), toolCallAdmissions: new())),
                CandidateStopReason.UnsupportedCapability, []);

        using var cut = new RunCut(options.TimeProvider, execution.Bounds.MaximumDuration, cancellationToken);
        var state = new RunState(execution, configuration, options, cut, restored, terminal is not null);
        var saved = restored?.Checkpoint.Candidates;
        var episodes = saved?.Episodes.ToList() ?? [];
        var submissions = saved?.Submissions.ToList() ?? [];
        var cursor = newRun || saved is null ? CandidateCursor.Initial : saved.Cursor;
        if (newRun || saved is null) episodes.Add(new(Guid.NewGuid(), state.CandidateWorkId, null, null, null, null));
        var repairs = 0; var continuations = 0; var invokedSubmissions = 0;
        try
        {
            // Restored facts remain capturable even if the new round is cut before its first effect.
            if (restored is not null) state.Initialize();
            if (cut.Check() != RuntimeStop.None) return CutStop();
            if (restored is null) state.Initialize();
            state.InitializeCandidateRetention(saved ?? new(cursor, [], []));
            if (!Reserve()) return StateStop();
            if (restored?.Pending is not null && !await state.WaitForRestoredRetryAsync().ConfigureAwait(false)) return StateStop();
            if (cursor == CandidateCursor.ProducingAdmittedEpisode && state.RestoredToolCursor is { } tools
                && state.ToolRecords.Any(m => m.ModelAttempt.Matches(tools) && m.State != ToolMemberState.Succeeded))
            {
                // Complete the original admitted episode's dependencies before reserving another provider turn.
                if (!await PartialToolBatchOperation.ResumeAsync(state).ConfigureAwait(false)) return StateStop();
                if (!Report()) return cut.Check() != RuntimeStop.None ? CutStop() : Stop(CandidateStopReason.ProgressObserverFailed);
            }
            while (true)
            {
                if (cut.Check() != RuntimeStop.None) return CutStop();
                if (cursor == CandidateCursor.End)
                {
                    var ended = submissions[^1].Receipt!;
                    return Stop(ended.IsAccepted ? CandidateStopReason.Completed : CandidateStopReason.HostEnded);
                }
                if (cursor == CandidateCursor.UnresolvedFeedback) return Stop(FeedbackStop(submissions[^1].Receipt!.Acknowledgement));
                if (invokedSubmissions >= request.Bounds.MaximumSubmissions) return Stop(CandidateStopReason.SubmissionLimit);
                if (cursor != CandidateCursor.FinalReadyUnsubmitted)
                {
                    if (state.Completed >= execution.Bounds.MaximumWorkUnits) return Stop(CandidateStopReason.WorkUnitLimit);
                    var episode = episodes[^1];
                    var previous = episode.Predecessor is { } id ? submissions.Single(s => s.SubmissionId == id) : null;
                    if (episode.FirstAttempt is null && previous is not null)
                    {
                        if (previous.Receipt!.Decision == CandidateDecision.Reject)
                        {
                            if (repairs >= request.Bounds.MaximumRepairs) return Stop(CandidateStopReason.RepairLimit);
                        }
                        else if (continuations >= request.Bounds.MaximumContinuations) return Stop(CandidateStopReason.ContinuationLimit);
                    }
                    if (!state.PreflightTurn()) return StateStop();
                    if (episode.CorrectionIndex is null && previous?.Receipt?.Decision == CandidateDecision.Reject && previous.Correction is { } correction)
                    {
                        // Commit the correction and its marker under the same cut gate.
                        var corrected = episode with { CorrectionIndex = state.CandidateRecordCount };
                        if (!Reserve(corrected, additionalRecords: 1)) return StateStop();
                        if (!cut.TryCommit(() => { state.AppendCandidateCorrection(correction); episodes[^1] = corrected; })) return CutStop();
                        episode = corrected;
                    }
                    var admission = episode.FirstAttempt is null ? episode with
                    { FirstAttempt = new(execution.ExecutionId, Guid.NewGuid(), Guid.NewGuid(), 1), AdmissionRound = state.CandidateRoundId } : episode;
                    if (!Reserve(admission, additionalRecords: 1)) return StateStop();
                    ProviderRequest? providerRequest = null;
                    // An episode's admission and the first provider reservation share the cut gate. No async boundary intervenes.
                    if (!cut.TryCommit(() =>
                    {
                        providerRequest = state.AdmitTurn();
                        if (providerRequest is null || episode.FirstAttempt is not null) return;
                        episodes[^1] = episode with { FirstAttempt = providerRequest.Attempt, AdmissionRound = state.CandidateRoundId };
                        cursor = CandidateCursor.ProducingAdmittedEpisode;
                        if (previous is not null)
                        {
                            if (previous.Receipt!.Decision == CandidateDecision.Reject) repairs++;
                            else continuations++;
                        }
                    })) return CutStop();
                    if (providerRequest is null) return StateStop();
                    var call = await ProviderCallOperation.ExecuteAsync(state, providerRequest).ConfigureAwait(false);
                    if (call is null) return StateStop();
                    providerRequest = call.Request;
                    var attempt = call.Attempt;
                    if (attempt.Response is { Finish: ProviderFinish.Final, Accepted: true })
                    {
                        var ready = new SavedCandidateSubmission(episode.Id, execution.ExecutionId, Guid.NewGuid(),
                            state.CandidateRecordCount - 1, false, null, null, null);
                        if (!Reserve(additionalSubmission: ready)) return StateStop();
                        submissions.Add(ready);
                        cursor = CandidateCursor.FinalReadyUnsubmitted;
                    }
                    if (cut.Check() != RuntimeStop.None) return CutStop();
                    if (attempt.Stop != RuntimeStop.None) return StateStop();
                    if (attempt.SettlementStop is not (RuntimeStop.None or RuntimeStop.HostStopped)) return Stop(CandidateStopReason.ProductionFailed);
                    if (attempt.ProviderOutcome != ProviderOutcome.Succeeded || attempt.Response is null)
                        return Stop(attempt.ProviderError == ProviderError.LimitExceeded ? CandidateStopReason.RuntimeLimit : CandidateStopReason.ProductionFailed);
                    if (!state.CanContinue) return StateStop();
                    if (attempt.Response.Finish is ProviderFinish.ToolCalls or ProviderFinish.ToolCorrection)
                    {
                        if (!Reserve(additionalRecords: (attempt.Response.Correction?.Members.Count ?? attempt.Response.Calls.Count)
                            + (configuration.ToolArgumentCorrection is null ? 0 : 1))) return StateStop();
                        var batch = await ToolBatchOperation.ExecuteAsync(state, providerRequest, attempt.Response, terminal is null ? null : Snapshot()).ConfigureAwait(false);
                        // Release the original transient proposal before progress or another provider await.
                        attempt = attempt with { Response = null };
                        call = call with { Attempt = attempt };
                        if (cut.Check() != RuntimeStop.None) return CutStop();
                        if (batch.Stop != RuntimeStop.None || batch.Error != SolusAgent.Tools.Api.ToolError.None) return StateStop();
                        if (!Report()) return cut.Check() != RuntimeStop.None ? CutStop() : Stop(CandidateStopReason.ProgressObserverFailed);
                        continue;
                    }
                    if (attempt.Response.Finish != ProviderFinish.Final) return Stop(CandidateStopReason.ProductionFailed);
                    if (!Report()) return cut.Check() != RuntimeStop.None ? CutStop() : Stop(CandidateStopReason.ProgressObserverFailed);
                }

                var pendingSubmission = submissions[^1];
                var payload = state.Records[pendingSubmission.FinalIndex].Model!.Text!;
                if (System.Text.Encoding.UTF8.GetByteCount(payload) > request.Bounds.MaximumPayloadBytes)
                    return Stop(CandidateStopReason.RuntimeLimit);
                var predecessor = episodes.Single(e => e.Id == pendingSubmission.EpisodeId).Predecessor;
                var rejected = predecessor is { } predecessorId && submissions.Single(s => s.SubmissionId == predecessorId).Receipt!.Decision == CandidateDecision.Reject;
                var submission = new CandidateSubmission(pendingSubmission.ExecutionId, pendingSubmission.SubmissionId, payload,
                    rejected ? predecessor : null, request.Bounds.MaximumPayloadBytes);
                // Reserve worst-case safe receipt/cursor bookkeeping before invoking a potentially effecting Host.
                var prospective = Snapshot();
                prospective.Submissions[^1] = pendingSubmission with { Invoked = true, InvocationRound = state.CandidateRoundId,
                    Receipt = new(pendingSubmission.ExecutionId, pendingSubmission.SubmissionId, CandidateAcknowledgement.Acknowledged, CandidateDecision.Reject, CandidateContinuation.Continue),
                    Correction = OrdinaryContextCodec.MaximumCorrectionForReservation };
                prospective = prospective with { Episodes = [.. prospective.Episodes, new(Guid.NewGuid(), state.CandidateWorkId, pendingSubmission.SubmissionId, null, null, null)] };
                if (terminal is not null && !state.ReserveCandidateLedger(prospective)) return StateStop();
                CandidateFeedback? feedback;
                try
                {
                    if (!cut.TryStart(() =>
                    {
                        submissions[^1] = pendingSubmission with { Invoked = true, InvocationRound = state.CandidateRoundId };
                        invokedSubmissions++;
                        cursor = CandidateCursor.UnresolvedFeedback;
                        return host.SubmitAsync(submission, cut.Token).AsTask();
                    }, out var pending)) return CutStop();
                    var observed = await cut.WaitAsync(pending!, observeCompletedAtCut: true).ConfigureAwait(false);
                    if (!observed.Obtained) { Observe(CandidateAcknowledgement.Unknown); return CutStop(); }
                    feedback = observed.Value;
                }
                catch (OperationCanceledException) when (cut.Check() != RuntimeStop.None)
                { if (submissions[^1].Invoked) Observe(CandidateAcknowledgement.Unknown); return CutStop(); }
                catch (Exception exception) when (Recoverable(exception))
                {
                    if (submissions[^1].Invoked) Observe(CandidateAcknowledgement.Failed);
                    return cut.Check() != RuntimeStop.None ? CutStop() : Stop(CandidateStopReason.FailedAcknowledgement);
                }
                CandidateStopReason? feedbackStop = null;
                if (feedback is null) { Observe(CandidateAcknowledgement.Missing); feedbackStop = CandidateStopReason.MissingAcknowledgement; }
                else if (feedback.ExecutionId != submission.ExecutionId)
                { Observe(CandidateAcknowledgement.Mismatched); feedbackStop = CandidateStopReason.MismatchedFeedback; }
                else if (feedback.SubmissionId != submission.SubmissionId)
                {
                    var duplicate = submissions.Any(s => s.SubmissionId == feedback.SubmissionId && s.Receipt is not null);
                    Observe(duplicate ? CandidateAcknowledgement.Duplicate : CandidateAcknowledgement.Mismatched);
                    feedbackStop = duplicate ? CandidateStopReason.DuplicateFeedback : CandidateStopReason.MismatchedFeedback;
                }
                else if (feedback.Acknowledgement != CandidateAcknowledgement.Acknowledged)
                { Observe(feedback.Acknowledgement); feedbackStop = FeedbackStop(feedback.Acknowledgement); }
                else
                {
                    Observe(CandidateAcknowledgement.Acknowledged, feedback);
                    if (feedback.Continuation == CandidateContinuation.End) cursor = CandidateCursor.End;
                    else
                    {
                        episodes.Add(new(Guid.NewGuid(), state.CandidateWorkId, submission.SubmissionId, null, null, null));
                        cursor = CandidateCursor.FollowOnNotAdmitted;
                    }
                }
                if (cut.Check() != RuntimeStop.None) return CutStop();
                if (feedbackStop is { } stopped) return Stop(stopped);
                // The next iteration observes End before considering any new allowance.

                void Observe(CandidateAcknowledgement acknowledgement, CandidateFeedback? acknowledged = null) =>
                    submissions[^1] = submissions[^1] with
                    {
                        Receipt = new(submission.ExecutionId, submission.SubmissionId, acknowledgement, acknowledged?.Decision, acknowledged?.Continuation),
                        Correction = acknowledged?.CorrectionText,
                    };
            }
        }
        catch (ProviderContractException exception)
        { return cut.Check() != RuntimeStop.None ? CutStop() : Stop(exception.Error == ProviderError.LimitExceeded ? CandidateStopReason.RuntimeLimit : CandidateStopReason.ProductionFailed); }
        catch (Exception exception) when (Recoverable(exception))
        { return cut.Check() != RuntimeStop.None ? CutStop() : Stop(CandidateStopReason.ProductionFailed); }

        bool Reserve(SavedCandidateEpisode? replacement = null, int additionalRecords = 0,
            SavedCandidateSubmission? additionalSubmission = null, int additionalBytes = 0)
        {
            if (terminal is null) return true;
            var snapshot = Snapshot();
            if (replacement is not null) snapshot.Episodes[^1] = replacement;
            if (additionalSubmission is not null) snapshot = snapshot with { Submissions = [.. snapshot.Submissions, additionalSubmission] };
            return state.ReserveCandidateLedger(snapshot, additionalRecords, additionalBytes);
        }
        bool Report()
        {
            try { return cut.TryCommit(() => progress?.Report(new(execution.ExecutionId, state.Completed, state.Usage()))); }
            catch (Exception exception) when (Recoverable(exception)) { return false; }
        }
        SavedCandidates Snapshot() => new(cursor, episodes.ToArray(), submissions.ToArray());
        CandidateExecutionResult CutStop() => Stop(cut.Check() == RuntimeStop.Cancelled ? CandidateStopReason.Cancelled : CandidateStopReason.DurationLimit);
        CandidateExecutionResult StateStop() => cut.Check() != RuntimeStop.None ? CutStop()
            : Stop(state.AdmissionStop == RuntimeStop.ResourceLimit ? CandidateStopReason.RuntimeLimit
                : state.UsageAccountingUnavailable ? CandidateStopReason.UsageAccountingUnavailable
                : state.AdmissionStop is RuntimeStop.ExposureDenied or RuntimeStop.HostStopped ? CandidateStopReason.ProductionStopped : CandidateStopReason.ProductionFailed);
        CandidateExecutionResult Stop(CandidateStopReason stop)
        {
            var reason = stop switch
            {
                CandidateStopReason.Completed => AgentTerminationReason.Completed,
                CandidateStopReason.HostEnded or CandidateStopReason.MissingAcknowledgement or CandidateStopReason.UnknownAcknowledgement
                    or CandidateStopReason.ProductionStopped or CandidateStopReason.UsageAccountingUnavailable => AgentTerminationReason.Partial,
                CandidateStopReason.SubmissionLimit or CandidateStopReason.WorkUnitLimit or CandidateStopReason.RepairLimit
                    or CandidateStopReason.ContinuationLimit or CandidateStopReason.DurationLimit or CandidateStopReason.RuntimeLimit => AgentTerminationReason.ResourceLimit,
                CandidateStopReason.Cancelled => AgentTerminationReason.Cancelled,
                _ => AgentTerminationReason.Failed,
            };
            var receipts = submissions.Where(s => s.Receipt is not null).Select(s => s.Receipt!).ToArray();
            var result = new CandidateExecutionResult(state.Outcome(reason, reason == AgentTerminationReason.Failed
                ? stop == CandidateStopReason.ProgressObserverFailed ? AgentFailureCode.ProgressObserverFailed : AgentFailureCode.ExecutionFailed : AgentFailureCode.None),
                stop, receipts.Where(r => r.ExecutionId == execution.ExecutionId).ToArray(), repairs, continuations,
                receipts.Where(r => r.ExecutionId != execution.ExecutionId).ToArray());
            terminal?.Invoke(state, result, Snapshot(), new(stop, invokedSubmissions, repairs, continuations));
            return result;
        }
    }
    private static CandidateStopReason FeedbackStop(CandidateAcknowledgement value) => value switch
    {
        CandidateAcknowledgement.Missing => CandidateStopReason.MissingAcknowledgement,
        CandidateAcknowledgement.Failed => CandidateStopReason.FailedAcknowledgement,
        CandidateAcknowledgement.Mismatched => CandidateStopReason.MismatchedFeedback,
        CandidateAcknowledgement.Duplicate => CandidateStopReason.DuplicateFeedback,
        _ => CandidateStopReason.UnknownAcknowledgement,
    };
    private static bool Recoverable(Exception exception) => exception is not (OutOfMemoryException or StackOverflowException);
}

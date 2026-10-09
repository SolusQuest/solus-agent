using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Execution;
using SolusAgent.Runtime.Startup;
using SolusAgent.Runtime.Tools;

namespace SolusAgent.Runtime.Candidates;

internal static class CandidateExecutionDriver
{
    public static async ValueTask<CandidateExecutionResult> ExecuteAsync(RuntimeConfiguration configuration, RuntimeOptions options,
        CandidateExecutionRequest request, ICandidateHost host, IProgress<AgentProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);
        var execution = request.Execution;
        var unsupported = execution.RequiredCapabilities & ~RuntimeAgentFactory.Support.SupportedCapabilities;
        if (unsupported != AgentCapability.None)
            return new(new(execution.ExecutionId, AgentTerminationReason.UnsupportedCapability, 0, unsupported,
                usage: new AgentRunUsage(execution.ExecutionId, UsageInventoryCoverage.Complete, [], toolInvocations: new())),
                CandidateStopReason.UnsupportedCapability, []);

        using var cut = new RunCut(options.TimeProvider, execution.Bounds.MaximumDuration, cancellationToken);
        var state = new RunState(execution, configuration, options, cut);
        var receipts = new List<CandidateReceipt>();
        CandidateFeedback? previous = null;
        var repairs = 0;
        var continuations = 0;
        try
        {
            if (cut.Check() != RuntimeStop.None) return CutStop();
            state.Initialize();
            while (true)
            {
                if (cut.Check() != RuntimeStop.None) return CutStop();
                if (receipts.Count >= request.Bounds.MaximumSubmissions) return Stop(CandidateStopReason.SubmissionLimit);
                if (state.Completed >= execution.Bounds.MaximumWorkUnits) return Stop(CandidateStopReason.WorkUnitLimit);
                if (previous is not null)
                {
                    if (previous.Decision == CandidateDecision.Reject)
                    {
                        if (repairs >= request.Bounds.MaximumRepairs) return Stop(CandidateStopReason.RepairLimit);
                        if (previous.CorrectionText is { } correction
                            && !cut.TryCommit(() => state.AppendCandidateCorrection(correction))) return CutStop();
                    }
                    else if (continuations >= request.Bounds.MaximumContinuations) return Stop(CandidateStopReason.ContinuationLimit);
                }

                // One candidate-production episode admits its first provider turn, then reuses the same RunState,
                // provider attempt and tool batch operations on the same run cut across intermediate tool turns
                // until an accepted Final is produced. The follow-on correction and its repair/continuation counter
                // are charged once for the whole episode, never per intermediate turn.
                var episodeStart = true;
                while (true)
                {
                    // The literal work ceiling is checked before every model admission, intermediate turns included.
                    if (cut.Check() != RuntimeStop.None) return CutStop();
                    if (state.Completed >= execution.Bounds.MaximumWorkUnits) return Stop(CandidateStopReason.WorkUnitLimit);
                    var providerRequest = state.AdmitTurn();
                    if (providerRequest is null) return StateStop();
                    if (episodeStart)
                    {
                        episodeStart = false;
                        if (previous is not null)
                        {
                            if (previous.Decision == CandidateDecision.Reject) repairs++;
                            else continuations++;
                        }
                    }
                    var attempt = await ProviderAttemptOperation.ExecuteAsync(state, providerRequest).ConfigureAwait(false);
                    if (cut.Check() != RuntimeStop.None) return CutStop();
                    if (attempt.Stop != RuntimeStop.None) return StateStop();
                    if (attempt.SettlementStop is not (RuntimeStop.None or RuntimeStop.HostStopped))
                        return Stop(CandidateStopReason.ProductionFailed);
                    // A deliberate settlement Stop cannot replace an observed failed or rejected production.
                    if (attempt.ProviderOutcome != ProviderOutcome.Succeeded || attempt.Response is null)
                        return Stop(attempt.ProviderError == ProviderError.LimitExceeded
                            ? CandidateStopReason.RuntimeLimit : CandidateStopReason.ProductionFailed);
                    // Accepted alone grants neither tool execution, candidate delivery nor another turn.
                    if (!state.CanContinue) return StateStop();
                    if (attempt.Response.Finish == ProviderFinish.ToolCalls)
                    {
                        // The accepted tool turn executes the existing generic all-member batch operation.
                        // Tool turns consume work/attempt/record/retention limits but no submission or follow-on allowance.
                        var batch = await ToolBatchOperation.ExecuteAsync(state, providerRequest, attempt.Response).ConfigureAwait(false);
                        if (cut.Check() != RuntimeStop.None) return CutStop();
                        if (batch.Stop != RuntimeStop.None || batch.Error != SolusAgent.Tools.Api.ToolError.None) return StateStop();
                        try
                        {
                            if (!cut.TryCommit(() => progress?.Report(new(execution.ExecutionId, state.Completed, state.Usage()))))
                                return CutStop();
                        }
                        catch (Exception exception) when (Recoverable(exception))
                        { return cut.Check() != RuntimeStop.None ? CutStop() : Stop(CandidateStopReason.ProgressObserverFailed); }
                        continue;
                    }
                    if (attempt.Response.Finish != ProviderFinish.Final) return Stop(CandidateStopReason.ProductionFailed);

                    try
                    {
                        if (!cut.TryCommit(() => progress?.Report(new(execution.ExecutionId, state.Completed, state.Usage())))) return CutStop();
                    }
                    catch (Exception exception) when (Recoverable(exception))
                    { return cut.Check() != RuntimeStop.None ? CutStop() : Stop(CandidateStopReason.ProgressObserverFailed); }

                    var submission = new CandidateSubmission(execution.ExecutionId, Guid.NewGuid(), attempt.Response.Text!,
                        previous?.Decision == CandidateDecision.Reject ? previous.SubmissionId : null);
                    var invoked = false;
                    CandidateFeedback? feedback;
                    try
                    {
                        if (!cut.TryStart(() => { invoked = true; return host.SubmitAsync(submission, cut.Token).AsTask(); }, out var pending))
                            return CutStop();
                        var observed = await cut.WaitAsync(pending!, observeCompletedAtCut: true).ConfigureAwait(false);
                        if (!observed.Obtained)
                        {
                            Observe(CandidateAcknowledgement.Unknown);
                            return CutStop();
                        }
                        feedback = observed.Value;
                    }
                    catch (OperationCanceledException) when (cut.Check() != RuntimeStop.None)
                    {
                        if (invoked) Observe(CandidateAcknowledgement.Unknown);
                        return CutStop();
                    }
                    catch (Exception exception) when (Recoverable(exception))
                    {
                        if (invoked) Observe(CandidateAcknowledgement.Failed);
                        return cut.Check() != RuntimeStop.None ? CutStop() : Stop(CandidateStopReason.FailedAcknowledgement);
                    }

                    CandidateStopReason? feedbackStop = null;
                    if (feedback is null)
                    { Observe(CandidateAcknowledgement.Missing); feedbackStop = CandidateStopReason.MissingAcknowledgement; }
                    else if (feedback.ExecutionId != execution.ExecutionId)
                    { Observe(CandidateAcknowledgement.Mismatched); feedbackStop = CandidateStopReason.MismatchedFeedback; }
                    else if (feedback.SubmissionId != submission.SubmissionId)
                    {
                        var duplicate = receipts.Any(receipt => receipt.SubmissionId == feedback.SubmissionId);
                        Observe(duplicate ? CandidateAcknowledgement.Duplicate : CandidateAcknowledgement.Mismatched);
                        feedbackStop = duplicate ? CandidateStopReason.DuplicateFeedback : CandidateStopReason.MismatchedFeedback;
                    }
                    else if (feedback.Acknowledgement != CandidateAcknowledgement.Acknowledged)
                    {
                        Observe(feedback.Acknowledgement);
                        feedbackStop = feedback.Acknowledgement == CandidateAcknowledgement.Failed
                            ? CandidateStopReason.FailedAcknowledgement : CandidateStopReason.UnknownAcknowledgement;
                    }
                    else Observe(CandidateAcknowledgement.Acknowledged, feedback);

                    if (cut.Check() != RuntimeStop.None) return CutStop();
                    if (feedbackStop is { } stopped) return Stop(stopped);
                    if (feedback!.Continuation == CandidateContinuation.End)
                        return Stop(feedback.Decision == CandidateDecision.Accept ? CandidateStopReason.Completed : CandidateStopReason.HostEnded);
                    previous = feedback;
                    break;

                    void Observe(CandidateAcknowledgement acknowledgement, CandidateFeedback? acknowledged = null) =>
                        receipts.Add(new(execution.ExecutionId, submission.SubmissionId, acknowledgement, acknowledged?.Decision, acknowledged?.Continuation));
                }
            }
        }
        catch (ProviderContractException exception)
        { return cut.Check() != RuntimeStop.None ? CutStop() : Stop(exception.Error == ProviderError.LimitExceeded
            ? CandidateStopReason.RuntimeLimit : CandidateStopReason.ProductionFailed); }
        catch (Exception exception) when (Recoverable(exception))
        { return cut.Check() != RuntimeStop.None ? CutStop() : Stop(CandidateStopReason.ProductionFailed); }

        CandidateExecutionResult CutStop() => Stop(cut.Check() == RuntimeStop.Cancelled ? CandidateStopReason.Cancelled : CandidateStopReason.DurationLimit);
        CandidateExecutionResult StateStop() => cut.Check() != RuntimeStop.None ? CutStop()
            : Stop(state.AdmissionStop == RuntimeStop.ResourceLimit ? CandidateStopReason.RuntimeLimit
                : state.AdmissionStop is RuntimeStop.ExposureDenied or RuntimeStop.HostStopped ? CandidateStopReason.ProductionStopped
                : CandidateStopReason.ProductionFailed);
        CandidateExecutionResult Stop(CandidateStopReason stop)
        {
            var reason = stop switch
            {
                CandidateStopReason.Completed => AgentTerminationReason.Completed,
                CandidateStopReason.HostEnded or CandidateStopReason.MissingAcknowledgement or CandidateStopReason.UnknownAcknowledgement
                    or CandidateStopReason.ProductionStopped => AgentTerminationReason.Partial,
                CandidateStopReason.SubmissionLimit or CandidateStopReason.WorkUnitLimit or CandidateStopReason.RepairLimit
                    or CandidateStopReason.ContinuationLimit or CandidateStopReason.DurationLimit or CandidateStopReason.RuntimeLimit => AgentTerminationReason.ResourceLimit,
                CandidateStopReason.Cancelled => AgentTerminationReason.Cancelled,
                _ => AgentTerminationReason.Failed,
            };
            return new(state.Outcome(reason, reason == AgentTerminationReason.Failed
                ? stop == CandidateStopReason.ProgressObserverFailed ? AgentFailureCode.ProgressObserverFailed : AgentFailureCode.ExecutionFailed
                : AgentFailureCode.None), stop, receipts, repairs, continuations);
        }
    }
    private static bool Recoverable(Exception exception) => exception is not (OutOfMemoryException or StackOverflowException);
}

using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;

namespace SolusAgent.ApiOnlyConsumer.Candidates;

/// <summary>Test-only candidate production and feedback interpretation through Api alone.</summary>
public sealed class ScriptedCandidateAgent : ICandidateAgent
{
    private readonly Func<CandidateFeedback?, CancellationToken, ValueTask<string>>[] productions;
    private int totalProductionStarted;

    public ScriptedCandidateAgent(params Func<CandidateFeedback?, CancellationToken, ValueTask<string>>[] productions)
    {
        ArgumentNullException.ThrowIfNull(productions);
        if (productions.Length == 0 || productions.Any(production => production is null))
        {
            throw new ArgumentException("A nonempty production script is required.", nameof(productions));
        }
        this.productions = productions.ToArray();
    }

    public AgentCapability SupportedCapabilities => AgentCapability.WorkUnitLimit | AgentCapability.Cancellation;
    public int TotalProductionStarted => Volatile.Read(ref totalProductionStarted);

    public ValueTask<AgentOutcome> ExecuteAsync(AgentRequest request, IProgress<AgentProgress>? progress = null, CancellationToken cancellationToken = default) =>
        new SyntheticAgent(productions.Length).ExecuteAsync(request, progress, cancellationToken);

    public async ValueTask<CandidateExecutionResult> ExecuteCandidatesAsync(
        CandidateExecutionRequest request,
        ICandidateHost host,
        IProgress<AgentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);
        var execution = request.Execution;
        var unsupported = execution.RequiredCapabilities & ~SupportedCapabilities;
        var receipts = new List<CandidateReceipt>();
        var completed = 0;
        var repairs = 0;
        var continuations = 0;
        CandidateFeedback? previous = null;
        if (unsupported != AgentCapability.None)
        {
            return Stop(CandidateStopReason.UnsupportedCapability);
        }

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Stop(CandidateStopReason.Cancelled);
            }
            if (receipts.Count == request.Bounds.MaximumSubmissions)
            {
                return Stop(CandidateStopReason.SubmissionLimit);
            }
            if (completed == execution.Bounds.MaximumWorkUnits)
            {
                return Stop(CandidateStopReason.WorkUnitLimit);
            }
            if (previous is not null)
            {
                if (previous.Decision == CandidateDecision.Reject)
                {
                    if (repairs == request.Bounds.MaximumRepairs)
                    {
                        return Stop(CandidateStopReason.RepairLimit);
                    }
                    repairs++;
                }
                else
                {
                    if (continuations == request.Bounds.MaximumContinuations)
                    {
                        return Stop(CandidateStopReason.ContinuationLimit);
                    }
                    continuations++;
                }
            }

            CandidateSubmission submission;
            try
            {
                Interlocked.Increment(ref totalProductionStarted);
                var payload = await productions[completed](previous, cancellationToken).AsTask().WaitAsync(cancellationToken);
                submission = new CandidateSubmission(execution.ExecutionId, Guid.NewGuid(), payload,
                    previous?.Decision == CandidateDecision.Reject ? previous.SubmissionId : null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Stop(CandidateStopReason.Cancelled);
            }
            catch (Exception)
            {
                return Stop(CandidateStopReason.ProductionFailed);
            }

            completed++;
            try
            {
                progress?.Report(new AgentProgress(execution.ExecutionId, completed));
            }
            catch (Exception)
            {
                return Stop(CandidateStopReason.ProgressObserverFailed);
            }
            if (cancellationToken.IsCancellationRequested)
            {
                return Stop(CandidateStopReason.Cancelled);
            }

            CandidateFeedback? feedback;
            try
            {
                // Cancel observation even if this fake Host ignores the token; never cancel/replay its effects.
                feedback = await host.SubmitAsync(submission, cancellationToken).AsTask().WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Observe(CandidateAcknowledgement.Unknown);
                return Stop(CandidateStopReason.Cancelled);
            }
            catch (Exception)
            {
                Observe(CandidateAcknowledgement.Failed);
                return Stop(CandidateStopReason.FailedAcknowledgement);
            }

            if (feedback is null)
            {
                Observe(CandidateAcknowledgement.Missing);
                return Stop(CandidateStopReason.MissingAcknowledgement);
            }
            if (feedback.ExecutionId != execution.ExecutionId)
            {
                Observe(CandidateAcknowledgement.Mismatched);
                return Stop(CandidateStopReason.MismatchedFeedback);
            }
            if (feedback.SubmissionId != submission.SubmissionId)
            {
                var duplicate = receipts.Any(receipt => receipt.SubmissionId == feedback.SubmissionId);
                Observe(duplicate ? CandidateAcknowledgement.Duplicate : CandidateAcknowledgement.Mismatched);
                return Stop(duplicate ? CandidateStopReason.DuplicateFeedback : CandidateStopReason.MismatchedFeedback);
            }
            if (feedback.Acknowledgement != CandidateAcknowledgement.Acknowledged)
            {
                Observe(feedback.Acknowledgement);
                return Stop(feedback.Acknowledgement == CandidateAcknowledgement.Failed
                    ? CandidateStopReason.FailedAcknowledgement : CandidateStopReason.UnknownAcknowledgement);
            }

            Observe(CandidateAcknowledgement.Acknowledged, feedback);
            // A delivered acknowledgement survives cancellation. Completion and Host End precede another admission.
            if (completed == productions.Length && feedback.Decision == CandidateDecision.Accept)
            {
                return Stop(CandidateStopReason.Completed);
            }
            if (feedback.Continuation == CandidateContinuation.End)
            {
                return Stop(CandidateStopReason.HostEnded);
            }
            if (completed == productions.Length)
            {
                return Stop(CandidateStopReason.ProductionExhausted);
            }
            previous = feedback;

            void Observe(CandidateAcknowledgement acknowledgement, CandidateFeedback? acknowledged = null) =>
                receipts.Add(new CandidateReceipt(execution.ExecutionId, submission.SubmissionId, acknowledgement, acknowledged?.Decision, acknowledged?.Continuation));
        }

        CandidateExecutionResult Stop(CandidateStopReason stop)
        {
            var reason = stop switch
            {
                CandidateStopReason.Completed => AgentTerminationReason.Completed,
                CandidateStopReason.HostEnded or CandidateStopReason.ProductionExhausted or CandidateStopReason.MissingAcknowledgement or CandidateStopReason.UnknownAcknowledgement => AgentTerminationReason.Partial,
                CandidateStopReason.SubmissionLimit or CandidateStopReason.WorkUnitLimit or CandidateStopReason.RepairLimit or CandidateStopReason.ContinuationLimit => AgentTerminationReason.ResourceLimit,
                CandidateStopReason.Cancelled => AgentTerminationReason.Cancelled,
                CandidateStopReason.UnsupportedCapability => AgentTerminationReason.UnsupportedCapability,
                _ => AgentTerminationReason.Failed,
            };
            var failure = reason == AgentTerminationReason.Failed
                ? stop == CandidateStopReason.ProgressObserverFailed ? AgentFailureCode.ProgressObserverFailed : AgentFailureCode.ExecutionFailed
                : AgentFailureCode.None;
            return new CandidateExecutionResult(new AgentOutcome(execution.ExecutionId, reason, completed,
                reason == AgentTerminationReason.UnsupportedCapability ? unsupported : AgentCapability.None, failure), stop, receipts, repairs, continuations);
        }
    }
}

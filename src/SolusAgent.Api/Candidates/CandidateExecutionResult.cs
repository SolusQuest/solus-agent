using SolusAgent.Api.Execution;

namespace SolusAgent.Api.Candidates;

/// <summary>A safe per-submission observation that retains no candidate, correction text or exception.</summary>
public sealed class CandidateReceipt
{
    /// <summary>Creates coherent safe metadata for the submitted identity, never the identity of mismatched feedback.</summary>
    /// <exception cref="ArgumentException">An identity or decision combination is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An enumeration value is undefined.</exception>
    public CandidateReceipt(Guid executionId, Guid submissionId, CandidateAcknowledgement acknowledgement, CandidateDecision? decision = null, CandidateContinuation? continuation = null)
    {
        CandidateValidation.Identity(executionId, nameof(executionId));
        CandidateValidation.Identity(submissionId, nameof(submissionId));
        if (!Enum.IsDefined(acknowledgement))
        {
            throw new ArgumentOutOfRangeException(nameof(acknowledgement));
        }
        FeedbackValidation.Decision(acknowledgement, decision, continuation);
        ExecutionId = executionId;
        SubmissionId = submissionId;
        Acknowledgement = acknowledgement;
        Decision = decision;
        Continuation = continuation;
    }

    /// <summary>Gets the original execution association.</summary>
    public Guid ExecutionId { get; }
    /// <summary>Gets the identity of the submitted candidate whose feedback was observed.</summary>
    public Guid SubmissionId { get; }
    /// <summary>Gets an explicit observed acknowledgement state.</summary>
    public CandidateAcknowledgement Acknowledgement { get; }
    /// <summary>Gets a decision only for correlated acknowledged feedback.</summary>
    public CandidateDecision? Decision { get; }
    /// <summary>Gets the Host instruction only for correlated acknowledged feedback.</summary>
    public CandidateContinuation? Continuation { get; }
    /// <summary>Gets whether this submission was independently acknowledged as accepted.</summary>
    public bool IsAccepted => Acknowledgement == CandidateAcknowledgement.Acknowledged && Decision == CandidateDecision.Accept;
}

/// <summary>Closed candidate-protocol stop categories without raw diagnostics.</summary>
public enum CandidateStopReason
{
    /// <summary>The synthetic or implementation-specific candidate goal completed.</summary>
    Completed,
    /// <summary>The Host ended execution with work incomplete.</summary>
    HostEnded,
    /// <summary>A required correction had no further production available.</summary>
    ProductionExhausted,
    /// <summary>The total submission bound prevented more work.</summary>
    SubmissionLimit,
    /// <summary>The supported execution work-unit bound prevented more work.</summary>
    WorkUnitLimit,
    /// <summary>The repair allowance prevented a correction.</summary>
    RepairLimit,
    /// <summary>The continuation allowance prevented more accepted-result work.</summary>
    ContinuationLimit,
    /// <summary>The Host returned no feedback.</summary>
    MissingAcknowledgement,
    /// <summary>The feedback exchange failed.</summary>
    FailedAcknowledgement,
    /// <summary>The Host explicitly reported an unknown decision.</summary>
    UnknownAcknowledgement,
    /// <summary>Feedback association did not match the pending submission.</summary>
    MismatchedFeedback,
    /// <summary>Feedback repeated a prior submission.</summary>
    DuplicateFeedback,
    /// <summary>Caller cancellation was observed; pending acknowledgement may remain unknown.</summary>
    Cancelled,
    /// <summary>Candidate production failed.</summary>
    ProductionFailed,
    /// <summary>An ordinary progress observer failed before Host submission.</summary>
    ProgressObserverFailed,
    /// <summary>Required execution guarantees were rejected before production or submission.</summary>
    UnsupportedCapability,
}

/// <summary>An immutable safe completion observation retaining independent receipts without payloads or effect claims.</summary>
/// <remarks>These are in-process observations, not a durable Host ledger. Implementations must derive acceptance only from correlated acknowledged feedback; constructors cannot authenticate that provenance.</remarks>
public sealed class CandidateExecutionResult
{
    /// <summary>Creates a consistent terminal result and snapshots its safe per-submission observations.</summary>
    /// <exception cref="ArgumentNullException">The outcome or receipt list is null.</exception>
    /// <exception cref="ArgumentException">Outcome, receipt association, counts or stop metadata is incoherent.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A stop reason or count is invalid.</exception>
    public CandidateExecutionResult(AgentOutcome outcome, CandidateStopReason stopReason, IReadOnlyList<CandidateReceipt> receipts, int repairsAdmitted = 0, int continuationsAdmitted = 0)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(receipts);
        if (!Enum.IsDefined(stopReason))
        {
            throw new ArgumentOutOfRangeException(nameof(stopReason));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(repairsAdmitted);
        ArgumentOutOfRangeException.ThrowIfNegative(continuationsAdmitted);
        var snapshot = receipts.ToArray();
        if (snapshot.Any(receipt => receipt is null || receipt.ExecutionId != outcome.ExecutionId)
            || snapshot.Select(receipt => receipt.SubmissionId).Distinct().Count() != snapshot.Length
            || (outcome.Reason == AgentTerminationReason.UnsupportedCapability && snapshot.Length != 0)
            || repairsAdmitted > snapshot.Count(receipt => receipt.Decision == CandidateDecision.Reject && receipt.Continuation == CandidateContinuation.Continue)
            || continuationsAdmitted > snapshot.Count(receipt => receipt.IsAccepted && receipt.Continuation == CandidateContinuation.Continue))
        {
            throw new ArgumentException("Receipt association and counts must be coherent.", nameof(receipts));
        }

        var expectedReason = stopReason switch
        {
            CandidateStopReason.Completed => AgentTerminationReason.Completed,
            CandidateStopReason.HostEnded or CandidateStopReason.ProductionExhausted or CandidateStopReason.MissingAcknowledgement or CandidateStopReason.UnknownAcknowledgement => AgentTerminationReason.Partial,
            CandidateStopReason.SubmissionLimit or CandidateStopReason.WorkUnitLimit or CandidateStopReason.RepairLimit or CandidateStopReason.ContinuationLimit => AgentTerminationReason.ResourceLimit,
            CandidateStopReason.Cancelled => AgentTerminationReason.Cancelled,
            CandidateStopReason.UnsupportedCapability => AgentTerminationReason.UnsupportedCapability,
            _ => AgentTerminationReason.Failed,
        };
        if (outcome.Reason != expectedReason
            || (outcome.Reason == AgentTerminationReason.Failed && outcome.FailureCode != (stopReason == CandidateStopReason.ProgressObserverFailed ? AgentFailureCode.ProgressObserverFailed : AgentFailureCode.ExecutionFailed)))
        {
            throw new ArgumentException("The stop reason must agree with the ordinary outcome.", nameof(stopReason));
        }

        Outcome = outcome;
        StopReason = stopReason;
        Receipts = Array.AsReadOnly(snapshot);
        RepairsAdmitted = repairsAdmitted;
        ContinuationsAdmitted = continuationsAdmitted;
    }

    /// <summary>Gets the unchanged safe outer execution outcome, independent of acceptance and effects.</summary>
    public AgentOutcome Outcome { get; }
    /// <summary>Gets the closed protocol-specific stopping reason.</summary>
    public CandidateStopReason StopReason { get; }
    /// <summary>Gets the defensive ordered snapshot of independently observed submissions.</summary>
    public IReadOnlyList<CandidateReceipt> Receipts { get; }
    /// <summary>Gets follow-on production admissions after rejected candidates, not completed repairs.</summary>
    public int RepairsAdmitted { get; }
    /// <summary>Gets follow-on production admissions after accepted candidates, not completed work or effects.</summary>
    public int ContinuationsAdmitted { get; }
    /// <summary>Gets the count of independently acknowledged accepted submissions.</summary>
    public int AcceptedCount => Receipts.Count(receipt => receipt.IsAccepted);
}

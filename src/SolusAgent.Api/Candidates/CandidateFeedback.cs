namespace SolusAgent.Api.Candidates;

/// <summary>The observed acknowledgement state, independent of runtime completion or product effects.</summary>
public enum CandidateAcknowledgement
{
    /// <summary>The Host delivered a correlated decision.</summary>
    Acknowledged,
    /// <summary>The Host returned no feedback.</summary>
    Missing,
    /// <summary>The feedback exchange failed; this does not establish rejection or effect rollback.</summary>
    Failed,
    /// <summary>A decision is unknown, including cancellation while feedback remains pending.</summary>
    Unknown,
    /// <summary>Feedback belongs to a different execution or unknown submission.</summary>
    Mismatched,
    /// <summary>Feedback repeats an earlier submission in the same execution.</summary>
    Duplicate,
}

/// <summary>A Host-owned candidate decision, never an instruction to publish or retry an effect.</summary>
public enum CandidateDecision
{
    /// <summary>The Host accepted this candidate under its domain policy.</summary>
    Accept,
    /// <summary>The Host rejected this candidate under its domain policy.</summary>
    Reject,
}

/// <summary>The Host's execution instruction, separate from its candidate decision.</summary>
public enum CandidateContinuation
{
    /// <summary>More work may be admitted within the existing bounds.</summary>
    Continue,
    /// <summary>End candidate execution without further production or submission.</summary>
    End,
}

/// <summary>Host-facing feedback whose correlation must be checked before using its decision or instruction.</summary>
/// <remarks>Correction text stays in the Host channel. Failed and Unknown exchanges contain no decision, instruction or correction. Missing feedback is represented by a null callback result; mismatch and duplicate are classified by the observing agent.</remarks>
public sealed class CandidateFeedback
{
    /// <summary>The maximum UTF-8 correction-text size admitted by this draft.</summary>
    public const int MaximumCorrectionBytes = 16_384;

    /// <summary>Creates coherent acknowledgement metadata and optional bounded Host-facing correction data.</summary>
    /// <exception cref="ArgumentException">Identity, metadata or text is incoherent or invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An enumeration value is undefined.</exception>
    public CandidateFeedback(
        Guid executionId,
        Guid submissionId,
        CandidateAcknowledgement acknowledgement,
        CandidateDecision? decision = null,
        CandidateContinuation? continuation = null,
        string? correctionText = null)
    {
        CandidateValidation.Identity(executionId, nameof(executionId));
        CandidateValidation.Identity(submissionId, nameof(submissionId));
        if (acknowledgement is not (CandidateAcknowledgement.Acknowledged or CandidateAcknowledgement.Failed or CandidateAcknowledgement.Unknown))
        {
            throw new ArgumentOutOfRangeException(nameof(acknowledgement));
        }

        FeedbackValidation.Decision(acknowledgement, decision, continuation);
        if (acknowledgement != CandidateAcknowledgement.Acknowledged && correctionText is not null)
        {
            throw new ArgumentException("Unacknowledged feedback cannot supply correction data.", nameof(correctionText));
        }

        if (correctionText is not null)
        {
            CandidateValidation.Text(correctionText, MaximumCorrectionBytes, nameof(correctionText));
        }

        ExecutionId = executionId;
        SubmissionId = submissionId;
        Acknowledgement = acknowledgement;
        Decision = decision;
        Continuation = continuation;
        CorrectionText = correctionText;
    }

    /// <summary>Gets the execution association asserted by the Host.</summary>
    public Guid ExecutionId { get; }
    /// <summary>Gets the submission association asserted by the Host.</summary>
    public Guid SubmissionId { get; }
    /// <summary>Gets the exchange state, distinct from the domain decision.</summary>
    public CandidateAcknowledgement Acknowledgement { get; }
    /// <summary>Gets a domain decision only when acknowledged.</summary>
    public CandidateDecision? Decision { get; }
    /// <summary>Gets an execution instruction only when acknowledged.</summary>
    public CandidateContinuation? Continuation { get; }
    /// <summary>Gets optional Host-facing correction data, excluded from ordinary observations.</summary>
    public string? CorrectionText { get; }
    /// <summary>Returns the type name without feedback data.</summary>
    public override string ToString() => nameof(CandidateFeedback);
}

internal static class FeedbackValidation
{
    internal static void Decision(CandidateAcknowledgement acknowledgement, CandidateDecision? decision, CandidateContinuation? continuation)
    {
        if (decision is CandidateDecision value && !Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(nameof(decision));
        }
        if (continuation is CandidateContinuation instruction && !Enum.IsDefined(instruction))
        {
            throw new ArgumentOutOfRangeException(nameof(continuation));
        }
        if (acknowledgement == CandidateAcknowledgement.Acknowledged
            ? decision is null || continuation is null
            : decision is not null || continuation is not null)
        {
            throw new ArgumentException("Only acknowledged feedback has a decision and instruction.", nameof(acknowledgement));
        }
    }
}

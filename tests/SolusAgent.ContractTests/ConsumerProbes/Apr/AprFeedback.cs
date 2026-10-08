using SolusAgent.Api.Candidates;

namespace SolusAgent.ContractTests.ConsumerProbes.Apr;

/// <summary>Test-only Host feedback channel that records every submission it receives.</summary>
internal sealed class ScriptedAprFeedback(
    Func<CandidateSubmission, int, CancellationToken, ValueTask<CandidateFeedback?>> exchange) : ICandidateHost
{
    private readonly object gate = new();
    private readonly List<CandidateSubmission> submissions = [];

    /// <summary>Gets the defensive ordered record of received submissions.</summary>
    public IReadOnlyList<CandidateSubmission> Submissions
    {
        get
        {
            lock (gate)
            {
                return submissions.ToArray();
            }
        }
    }

    public ValueTask<CandidateFeedback?> SubmitAsync(CandidateSubmission submission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        int index;
        lock (gate)
        {
            submissions.Add(submission);
            index = submissions.Count - 1;
        }

        return exchange(submission, index, cancellationToken);
    }
}

/// <summary>Fixed synthetic Host decisions used by the finite APR scenario policies.</summary>
internal static class AprDecisions
{
    /// <summary>Accepts the correlated candidate with an explicit instruction.</summary>
    public static CandidateFeedback Accept(Guid executionId, Guid submissionId,
        CandidateContinuation continuation = CandidateContinuation.Continue) =>
        new(executionId, submissionId, CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept, continuation);

    /// <summary>Rejects the correlated candidate with a bounded correction consumed as data.</summary>
    public static CandidateFeedback RejectWithCorrection(Guid executionId, Guid submissionId, long expectedValue) =>
        new(executionId, submissionId, CandidateAcknowledgement.Acknowledged, CandidateDecision.Reject,
            CandidateContinuation.Continue, $"{AprScenarioAgent.CorrectionPrefix}{expectedValue}");

    /// <summary>Reports an explicit unknown exchange with no decision or instruction.</summary>
    public static CandidateFeedback Unknown(Guid executionId, Guid submissionId) =>
        new(executionId, submissionId, CandidateAcknowledgement.Unknown);

    /// <summary>Reports a failed exchange with no decision or instruction.</summary>
    public static CandidateFeedback Failed(Guid executionId, Guid submissionId) =>
        new(executionId, submissionId, CandidateAcknowledgement.Failed);

    /// <summary>Reports an acknowledged decision carrying the wrong execution association.</summary>
    public static CandidateFeedback WrongExecution(Guid wrongExecutionId, Guid submissionId) =>
        new(wrongExecutionId, submissionId, CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept,
            CandidateContinuation.Continue);

    /// <summary>Reports an acknowledged decision carrying another submission's association.</summary>
    public static CandidateFeedback WrongSubmission(Guid executionId, Guid otherSubmissionId) =>
        new(executionId, otherSubmissionId, CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept,
            CandidateContinuation.Continue);
}

using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;

namespace SolusAgent.Api.Context;

/// <summary>Optional combined context and candidate protocol, with one current Host request.</summary>
public interface IContextCandidateAgent : IContextAgent, ICandidateAgent
{
    /// <summary>Executes candidate work using explicit context intent and transfers restricted state separately.</summary>
    /// <exception cref="NotSupportedException">The implementation's documented combined integration is unavailable.</exception>
    ValueTask<ContextCandidateExecutionResult> ExecuteCandidatesWithContextAsync(ContextExecutionRequest request,
        CandidateExecutionBounds bounds, ICandidateHost host, IRestrictedContextSink? contextSink = null,
        IProgress<AgentProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Safe combined observations; restricted submissions, corrections and context are never retained here.</summary>
public sealed class ContextCandidateExecutionResult
{
    /// <summary>Checks current outcome identity and the ancestry of historical receipt origins.</summary>
    public ContextCandidateExecutionResult(ContextExecutionResult context, CandidateExecutionResult? candidates = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Admission == ContextAdmission.Rejected ? candidates is not null
            : candidates is null || !ReferenceEquals(context.Outcome, candidates.Outcome)
                || candidates.HistoricalReceipts.Any(r => !context.History.Any(h => h.Usage.ExecutionId == r.ExecutionId)))
            throw new ArgumentException("Candidate observations must belong to this context execution.", nameof(candidates));
        Context = context;
        Candidates = candidates;
    }

    /// <summary>Gets safe current admission, capture and historical usage observations.</summary>
    public ContextExecutionResult Context { get; }
    /// <summary>Gets candidate observations, or null when supplied context was rejected before work.</summary>
    public CandidateExecutionResult? Candidates { get; }
}

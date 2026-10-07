using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;

namespace AprHost;

/// <summary>
/// Minimized Host result for one coordinated APR business run. Context admission, candidate
/// exchange, Host acceptance, product effects and usage remain distinct members; this type merges
/// none of them and carries no candidate, correction, restricted or credential payload.
/// </summary>
public sealed class AprHostRun
{
    internal AprHostRun(ContextExecutionResult? context, CandidateExecutionResult? candidates, AprHostAcceptance acceptance)
    {
        Context = context;
        Candidates = candidates;
        Acceptance = acceptance;
    }

    /// <summary>Gets the context observation, or null when this run requested no context seam.</summary>
    public ContextExecutionResult? Context { get; }

    /// <summary>Gets the candidate observation, or null when context admission prevented candidate work.</summary>
    public CandidateExecutionResult? Candidates { get; }

    /// <summary>Gets the live Host-owned acceptance and effect state.</summary>
    public AprHostAcceptance Acceptance { get; }

    /// <summary>Returns only structural metadata.</summary>
    public override string ToString() => $"AprHostRun {{ Context = {(Context is null ? "absent" : "present")}, Candidates = {(Candidates is null ? "absent" : "present")} }}";
}

/// <summary>
/// APR-shaped synthetic business Host. It coordinates the existing separate outer seams for one
/// business execution, records product-owned acceptance through its own candidate channel, and
/// keeps product effects on an explicit separate operation. This class composes no runtime loop,
/// provider, tool registry, restoration codec, domain evidence model or platform authority, and
/// the coordination below is business orchestration rather than a combined public request type.
/// </summary>
public sealed class AprBusinessHost
{
    private readonly IAgent agent;
    private readonly ICandidateAgent? candidateAgent;
    private readonly IContextAgent? contextAgent;

    /// <summary>
    /// Creates the business Host over the outer agent seam and its optional candidate and context
    /// seams. The same implementation object may supply all three; a missing optional seam is
    /// rejected before any work instead of silently falling back to another seam.
    /// </summary>
    /// <exception cref="ArgumentNullException">The outer agent seam is null.</exception>
    public AprBusinessHost(IAgent agent, ICandidateAgent? candidateAgent = null, IContextAgent? contextAgent = null)
    {
        this.agent = agent ?? throw new ArgumentNullException(nameof(agent));
        this.candidateAgent = candidateAgent;
        this.contextAgent = contextAgent;
    }

    /// <summary>Gets the Host-owned synthetic acceptance and product-effect state of this Host.</summary>
    public AprHostAcceptance Acceptance { get; } = new();

    /// <summary>Executes the ordinary outer seam without candidate or context behavior.</summary>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    public ValueTask<AgentOutcome> ExecuteAsync(AgentRequest request, IProgress<AgentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return agent.ExecuteAsync(request, progress, cancellationToken);
    }

    /// <summary>
    /// Executes the separate context seam, or rejects the run before work when that optional seam
    /// was not supplied. A rejected or not-attempted admission is returned as observed, without a
    /// fresh fallback, work outcome, restricted capture or candidate submission.
    /// </summary>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="InvalidOperationException">The context seam is absent and the request requires no guarantee to reject.</exception>
    public ValueTask<ContextExecutionResult> ExecuteWithContextAsync(ContextExecutionRequest request,
        IRestrictedContextSink? contextSink = null, IProgress<AgentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (contextAgent is null)
        {
            var unavailable = contextSink is null ? ContextCaptureStatus.NotRequested : ContextCaptureStatus.Unavailable;
            if (request.Intent != ContextExecutionIntent.Fresh)
            {
                return ValueTask.FromResult(new ContextExecutionResult(request.Request.ExecutionId, request.Intent,
                    ContextAdmission.Rejected, rejectionCode: ContextRejectionCode.UnsupportedContext, captureStatus: unavailable));
            }

            var unsupported = request.Request.RequiredCapabilities;
            if (unsupported == AgentCapability.None)
            {
                throw new InvalidOperationException("A context run needs a supplied context seam or a rejectable required guarantee.");
            }

            return ValueTask.FromResult(new ContextExecutionResult(request.Request.ExecutionId, request.Intent,
                ContextAdmission.NotAttempted,
                new AgentOutcome(request.Request.ExecutionId, AgentTerminationReason.UnsupportedCapability, 0, unsupported),
                captureStatus: unavailable));
        }

        return contextAgent.ExecuteWithContextAsync(request, contextSink, progress, cancellationToken);
    }

    /// <summary>
    /// Executes the separate candidate seam over the supplied Host channel, recording correlated
    /// Host acceptance of parseable synthetic items in <see cref="Acceptance"/> independently of the
    /// agent's receipts and terminal outcome. The channel receives every submission unchanged and
    /// is never replayed automatically.
    /// </summary>
    /// <exception cref="ArgumentNullException">The request or feedback channel is null.</exception>
    /// <exception cref="InvalidOperationException">The candidate seam is absent and the request requires no guarantee to reject.</exception>
    public ValueTask<CandidateExecutionResult> ExecuteCandidatesAsync(CandidateExecutionRequest request,
        ICandidateHost feedback, IProgress<AgentProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(feedback);
        if (candidateAgent is null)
        {
            var unsupported = request.Execution.RequiredCapabilities;
            if (unsupported == AgentCapability.None)
            {
                throw new InvalidOperationException("A candidate run needs a supplied candidate seam or a rejectable required guarantee.");
            }

            return ValueTask.FromResult(new CandidateExecutionResult(
                new AgentOutcome(request.Execution.ExecutionId, AgentTerminationReason.UnsupportedCapability, 0, unsupported),
                CandidateStopReason.UnsupportedCapability, [], 0, 0));
        }

        return candidateAgent.ExecuteCandidatesAsync(request, new RecordingCandidateHost(feedback, Acceptance), progress, cancellationToken);
    }

    /// <summary>
    /// Coordinates one business run over the separate existing seams: context admission runs first,
    /// and candidate production or submission never starts when admission was rejected or not
    /// attempted. Both requests must carry the same execution association, which is checked before
    /// any context execution, capture, provider/tool effect or candidate submission. The two API
    /// calls keep their own request and result types; this method adds no combined request, durable
    /// restoration or fresh fallback after rejection.
    /// </summary>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The context and candidate requests disagree on their execution association.</exception>
    public async ValueTask<AprHostRun> RunAsync(ContextExecutionRequest contextRequest, IRestrictedContextSink? contextSink,
        CandidateExecutionRequest candidateRequest, ICandidateHost feedback, IProgress<AgentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contextRequest);
        ArgumentNullException.ThrowIfNull(candidateRequest);
        ArgumentNullException.ThrowIfNull(feedback);
        if (contextRequest.Request.ExecutionId != candidateRequest.Execution.ExecutionId)
        {
            throw new ArgumentException("Context and candidate requests must share one execution association.", nameof(candidateRequest));
        }

        var context = await ExecuteWithContextAsync(contextRequest, contextSink, progress, cancellationToken).ConfigureAwait(false);
        if (context.Admission is ContextAdmission.Rejected or ContextAdmission.NotAttempted)
        {
            return new AprHostRun(context, null, Acceptance);
        }

        var candidates = await ExecuteCandidatesAsync(candidateRequest, feedback, progress, cancellationToken).ConfigureAwait(false);
        return new AprHostRun(context, candidates, Acceptance);
    }

    /// <summary>
    /// Deliberately supplied Host channel decorator that records Host-owned acceptance from the
    /// unchanged correlated feedback exchange. Acceptance requires acknowledged Accept feedback
    /// correlated to the submitted identity and a parseable synthetic item payload.
    /// </summary>
    private sealed class RecordingCandidateHost(ICandidateHost inner, AprHostAcceptance acceptance) : ICandidateHost
    {
        public async ValueTask<CandidateFeedback?> SubmitAsync(CandidateSubmission submission, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(submission);
            var feedback = await inner.SubmitAsync(submission, cancellationToken).ConfigureAwait(false);
            if (feedback is { Acknowledgement: CandidateAcknowledgement.Acknowledged, Decision: CandidateDecision.Accept }
                && feedback.ExecutionId == submission.ExecutionId
                && feedback.SubmissionId == submission.SubmissionId
                && AprItem.TryParse(submission.Payload, out var item))
            {
                acceptance.RecordAccepted(submission.ExecutionId, submission.SubmissionId, submission.RepairsSubmissionId, item!);
            }

            return feedback;
        }
    }
}

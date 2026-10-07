using SolusAgent.Api.Execution;

namespace SolusAgent.Api.Candidates;

/// <summary>Finite Host-selected limits for submissions and separately admitted repairs and continuations.</summary>
public sealed class CandidateExecutionBounds
{
    /// <summary>Creates a positive total submission limit and nonnegative follow-on allowances.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A bound is invalid.</exception>
    public CandidateExecutionBounds(int maximumSubmissions, int maximumRepairs, int maximumContinuations)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSubmissions);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRepairs);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumContinuations);
        MaximumSubmissions = maximumSubmissions;
        MaximumRepairs = maximumRepairs;
        MaximumContinuations = maximumContinuations;
    }

    /// <summary>Gets the maximum number of individually identified Host submissions.</summary>
    public int MaximumSubmissions { get; }
    /// <summary>Gets the maximum follow-on productions admitted after rejection and Continue.</summary>
    public int MaximumRepairs { get; }
    /// <summary>Gets the maximum follow-on productions admitted after acceptance and Continue.</summary>
    public int MaximumContinuations { get; }
}

/// <summary>Composes the outer Host execution request with candidate-specific bounds.</summary>
public sealed class CandidateExecutionRequest
{
    /// <summary>Creates a candidate request without adding candidates to ordinary execution inputs.</summary>
    /// <exception cref="ArgumentNullException">A request or bounds argument is null.</exception>
    public CandidateExecutionRequest(AgentRequest execution, CandidateExecutionBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(bounds);
        Execution = execution;
        Bounds = bounds;
    }

    /// <summary>Gets immutable Host control and separately classified data; its content remains sensitive.</summary>
    public AgentRequest Execution { get; }
    /// <summary>Gets immutable candidate-specific limits.</summary>
    public CandidateExecutionBounds Bounds { get; }
    /// <summary>Returns the type name without request data.</summary>
    public override string ToString() => nameof(CandidateExecutionRequest);
}

/// <summary>A deliberately supplied Host candidate channel, separate from ordinary progress.</summary>
public interface ICandidateHost
{
    /// <summary>Receives one individually correlated submission and returns its decision or an explicit uncertain exchange.</summary>
    /// <param name="submission">Untrusted candidate data for Host-owned validation.</param>
    /// <param name="cancellationToken">Cancellation of observation; the Host documents whether work actually stops.</param>
    /// <returns>Feedback to be checked for association, or null for missing feedback.</returns>
    /// <remarks>The agent must not retry this call automatically. Failure or cancelled observation can follow Host effects; correlation is not a transaction, authentication or rollback guarantee.</remarks>
    ValueTask<CandidateFeedback?> SubmitAsync(CandidateSubmission submission, CancellationToken cancellationToken = default);
}

/// <summary>An optional outer agent contract for incremental candidate and Host-feedback exchange.</summary>
/// <remarks>Implementations honor required execution guarantees before work and document production units and bound enforcement. This interface supplies neither a runtime loop nor domain validation.</remarks>
public interface ICandidateAgent : IAgent
{
    /// <summary>Executes bounded candidate exchange and retains independent acknowledgement observations across stops.</summary>
    /// <param name="request">Host-selected execution controls and candidate limits.</param>
    /// <param name="host">The separate Host-facing payload/feedback channel.</param>
    /// <param name="progress">Optional ordinary production observations without candidate or feedback data.</param>
    /// <param name="cancellationToken">Caller cancellation, cooperatively observed when advertised.</param>
    /// <returns>A safe terminal result; acceptance, completion and external effects remain distinct.</returns>
    /// <exception cref="ArgumentNullException">The request or Host is null.</exception>
    ValueTask<CandidateExecutionResult> ExecuteCandidatesAsync(
        CandidateExecutionRequest request,
        ICandidateHost host,
        IProgress<AgentProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

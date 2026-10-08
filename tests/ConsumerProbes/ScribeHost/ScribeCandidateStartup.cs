using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;

namespace SolusAgent.ConsumerProbes.ScribeHost;

/// <summary>The test-only startup adapter that consumes one explicit context choice before deriving candidate execution.</summary>
/// <remarks>Only the explicit Fresh choice with no supplied envelope is supported here. Supplied-context intent is unsupported rather than silently downgraded to fresh, and this adapter restores, stores or captures nothing.</remarks>
public sealed class ScribeCandidateStartup
{
    /// <summary>Creates the adapter with the candidate limits the Host selected for its exchanges.</summary>
    /// <exception cref="ArgumentNullException">The candidate bounds are null.</exception>
    public ScribeCandidateStartup(CandidateExecutionBounds candidateBounds)
    {
        ArgumentNullException.ThrowIfNull(candidateBounds);
        CandidateBounds = candidateBounds;
    }

    /// <summary>Gets the candidate limits applied when the fresh request is adopted.</summary>
    public CandidateExecutionBounds CandidateBounds { get; }

    /// <summary>Consumes the exact current context request and derives its candidate request.</summary>
    /// <param name="contextRequest">The Host-constructed explicit context choice.</param>
    /// <returns>The candidate request over the consumed request's current Host control and data.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="NotSupportedException">The intent is not Fresh or a context envelope is present.</exception>
    public CandidateExecutionRequest Adopt(ContextExecutionRequest contextRequest)
    {
        ArgumentNullException.ThrowIfNull(contextRequest);
        if (contextRequest.Intent != ContextExecutionIntent.Fresh || contextRequest.Context is not null)
        {
            throw new NotSupportedException("This startup supports only the explicit Fresh choice without supplied context.");
        }

        return new CandidateExecutionRequest(contextRequest.Request, CandidateBounds);
    }
}

using System.Text;

namespace SolusAgent.Api.Candidates;

/// <summary>A single candidate delivered exclusively to the Host, distinct from ordinary progress and completion.</summary>
/// <remarks>Payload is untrusted domain data. Matching identities do not authenticate the sender or authorize effects. Do not log or serialize this object as ordinary diagnostics.</remarks>
public sealed class CandidateSubmission
{
    /// <summary>The maximum UTF-8 payload size admitted by this draft.</summary>
    public const int MaximumPayloadBytes = 65_536;

    /// <summary>Creates an immutable individually correlated submission; each repair requires a new identity.</summary>
    /// <exception cref="ArgumentException">An identity, predecessor or UTF-8 payload is invalid.</exception>
    /// <exception cref="ArgumentNullException">The payload is null.</exception>
    public CandidateSubmission(Guid executionId, Guid submissionId, string payload, Guid? repairsSubmissionId = null)
    {
        CandidateValidation.Identity(executionId, nameof(executionId));
        CandidateValidation.Identity(submissionId, nameof(submissionId));
        if (repairsSubmissionId is Guid predecessor && (predecessor == Guid.Empty || predecessor == submissionId))
        {
            throw new ArgumentException("A repair requires a distinct nonempty predecessor identity.", nameof(repairsSubmissionId));
        }

        CandidateValidation.Text(payload, MaximumPayloadBytes, nameof(payload));
        ExecutionId = executionId;
        SubmissionId = submissionId;
        Payload = payload;
        RepairsSubmissionId = repairsSubmissionId;
    }

    /// <summary>Gets the original Host-selected execution identity.</summary>
    public Guid ExecutionId { get; }

    /// <summary>Gets this submission's unique identity within the execution.</summary>
    public Guid SubmissionId { get; }

    /// <summary>Gets bounded untrusted candidate data for Host-owned domain validation only.</summary>
    public string Payload { get; }

    /// <summary>Gets the rejected submission this candidate repairs, when applicable.</summary>
    public Guid? RepairsSubmissionId { get; }

    /// <summary>Returns the type name without candidate data.</summary>
    public override string ToString() => nameof(CandidateSubmission);
}

internal static class CandidateValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static void Identity(Guid value, string name)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A nonempty identity is required.", name);
        }
    }

    internal static void Text(string value, int maximumBytes, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        try
        {
            if (StrictUtf8.GetByteCount(value) > maximumBytes)
            {
                throw new ArgumentException("Text exceeds the draft byte bound.", name);
            }
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Text must be valid UTF-8 data.", name);
        }
    }
}

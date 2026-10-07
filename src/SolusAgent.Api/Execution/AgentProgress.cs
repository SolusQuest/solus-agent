namespace SolusAgent.Api.Execution;

/// <summary>An ordinary observation containing only correlation and an implementation-defined completed-work count.</summary>
public sealed class AgentProgress
{
    /// <summary>Creates an associated nonnegative work observation.</summary>
    /// <exception cref="ArgumentException">The execution identity is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The work count is negative.</exception>
    public AgentProgress(Guid executionId, int completedWorkUnits)
    {
        if (executionId == Guid.Empty)
        {
            throw new ArgumentException("An execution identity is required.", nameof(executionId));
        }

        if (completedWorkUnits < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completedWorkUnits));
        }

        ExecutionId = executionId;
        CompletedWorkUnits = completedWorkUnits;
    }

    /// <summary>Gets the original Host-supplied execution identity.</summary>
    public Guid ExecutionId { get; }

    /// <summary>Gets completed implementation-defined work units, monotonic within one execution.</summary>
    public int CompletedWorkUnits { get; }
}

using SolusAgent.Api.Usage;
namespace SolusAgent.Api.Execution;

/// <summary>An ordinary observation containing correlation, an implementation-defined completed-work count and optional safe usage.</summary>
public sealed class AgentProgress
{
    /// <summary>Creates an associated nonnegative work observation.</summary>
    /// <exception cref="ArgumentException">The execution identity is empty or the usage belongs to another execution.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The work count is negative.</exception>
    public AgentProgress(Guid executionId, int completedWorkUnits, AgentRunUsage? usage = null)
    {
        if (executionId == Guid.Empty)
        {
            throw new ArgumentException("An execution identity is required.", nameof(executionId));
        }

        if (completedWorkUnits < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completedWorkUnits));
        }

        if (usage is not null && usage.ExecutionId != executionId)
            throw new ArgumentException("Usage must belong to the same execution.", nameof(usage));

        ExecutionId = executionId;
        CompletedWorkUnits = completedWorkUnits;
        Usage = usage;
    }

    /// <summary>Gets an optional immutable usage snapshot, independent of task completion and measurement availability.</summary>
    public AgentRunUsage? Usage { get; }

    /// <summary>Gets the original Host-supplied execution identity.</summary>
    public Guid ExecutionId { get; }

    /// <summary>Gets completed implementation-defined work units, monotonic within one execution.</summary>
    public int CompletedWorkUnits { get; }
}

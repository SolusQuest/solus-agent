using SolusAgent.Api.Usage;
using SolusAgent.Api.Capabilities;

namespace SolusAgent.Api.Execution;

/// <summary>The reason an execution ended, distinct from product acceptance or publication.</summary>
public enum AgentTerminationReason
{
    /// <summary>The implementation's requested task work completed.</summary>
    Completed,

    /// <summary>The implementation deliberately stopped with incomplete work.</summary>
    Partial,

    /// <summary>A supported resource bound stopped execution with work still incomplete.</summary>
    ResourceLimit,

    /// <summary>Caller cancellation was observed; this does not prove remote work stopped.</summary>
    Cancelled,

    /// <summary>A controllable failure stopped execution.</summary>
    Failed,

    /// <summary>At least one required capability was rejected before work or progress.</summary>
    UnsupportedCapability,
}

/// <summary>Safe, implementation-independent failure categories without raw exception or content diagnostics.</summary>
public enum AgentFailureCode
{
    /// <summary>No failure occurred.</summary>
    None,

    /// <summary>An implementation's work operation failed.</summary>
    ExecutionFailed,

    /// <summary>An ordinary progress observer threw while reporting an ordinary observation.</summary>
    ProgressObserverFailed,
}

/// <summary>A closed ordinary terminal surface with no candidate, context, credential, input or exception payload.</summary>
public sealed class AgentOutcome
{
    /// <summary>Creates a correlated terminal outcome with coherent rejection and failure metadata.</summary>
    /// <exception cref="ArgumentException">The identity, reason/metadata combination or usage association is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A count, reason, failure code or capability flags are invalid.</exception>
    public AgentOutcome(
        Guid executionId,
        AgentTerminationReason reason,
        int completedWorkUnits,
        AgentCapability unsupportedCapabilities = AgentCapability.None,
        AgentFailureCode failureCode = AgentFailureCode.None,
        AgentRunUsage? usage = null)
    {
        if (executionId == Guid.Empty)
        {
            throw new ArgumentException("An execution identity is required.", nameof(executionId));
        }

        if (completedWorkUnits < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completedWorkUnits));
        }

        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        if (!Enum.IsDefined(failureCode))
        {
            throw new ArgumentOutOfRangeException(nameof(failureCode));
        }

        CapabilityValidation.Validate(unsupportedCapabilities, nameof(unsupportedCapabilities));
        if ((reason == AgentTerminationReason.UnsupportedCapability) != (unsupportedCapabilities != AgentCapability.None)
            || (reason == AgentTerminationReason.UnsupportedCapability && completedWorkUnits != 0))
        {
            throw new ArgumentException("Unsupported capability rejection must be pre-work and identify its requirements.", nameof(unsupportedCapabilities));
        }

        if ((reason == AgentTerminationReason.Failed) != (failureCode != AgentFailureCode.None))
        {
            throw new ArgumentException("Failure reason and code must agree.", nameof(failureCode));
        }

        if (reason == AgentTerminationReason.UnsupportedCapability && usage is not null && (usage.Coverage != UsageInventoryCoverage.Complete || usage.Attempts.Count != 0))
            throw new ArgumentException("Pre-work rejection cannot assert execution attempts.", nameof(usage));

        if (usage is not null && usage.ExecutionId != executionId)
            throw new ArgumentException("Usage must belong to the same execution.", nameof(usage));

        ExecutionId = executionId;
        Reason = reason;
        CompletedWorkUnits = completedWorkUnits;
        Usage = usage;
        UnsupportedCapabilities = unsupportedCapabilities;
        FailureCode = failureCode;
    }

    /// <summary>Gets an optional immutable usage snapshot, independent of task completion and measurement availability.</summary>
    public AgentRunUsage? Usage { get; }

    /// <summary>Gets the original Host-supplied execution identity.</summary>
    public Guid ExecutionId { get; }

    /// <summary>Gets the terminal reason, preserving resource, cancellation, partial and failure distinctions.</summary>
    public AgentTerminationReason Reason { get; }

    /// <summary>Gets work completed before the terminal outcome, including incomplete runs.</summary>
    public int CompletedWorkUnits { get; }

    /// <summary>Gets required capabilities rejected before work; otherwise none.</summary>
    public AgentCapability UnsupportedCapabilities { get; }

    /// <summary>Gets a fixed failure category for Failed outcomes; otherwise none.</summary>
    public AgentFailureCode FailureCode { get; }

    /// <summary>Gets whether execution ended with successful task completion; this is not product acceptance.</summary>
    public bool IsCompleted => Reason == AgentTerminationReason.Completed;

    /// <summary>Gets whether completed work was preserved in a non-completed execution.</summary>
    public bool HasPartialProgress => !IsCompleted && CompletedWorkUnits > 0;
}

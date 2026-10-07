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

    /// <summary>An ordinary progress observer threw while reporting completed work.</summary>
    ProgressObserverFailed,
}

/// <summary>A closed ordinary terminal surface with no candidate, context, credential, input or exception payload.</summary>
public sealed class AgentOutcome
{
    /// <summary>Creates a correlated terminal outcome with coherent rejection and failure metadata.</summary>
    /// <exception cref="ArgumentException">The identity or reason/metadata combination is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A count, reason, failure code or capability flags are invalid.</exception>
    public AgentOutcome(
        Guid executionId,
        AgentTerminationReason reason,
        int completedWorkUnits,
        AgentCapability unsupportedCapabilities = AgentCapability.None,
        AgentFailureCode failureCode = AgentFailureCode.None)
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

        ExecutionId = executionId;
        Reason = reason;
        CompletedWorkUnits = completedWorkUnits;
        UnsupportedCapabilities = unsupportedCapabilities;
        FailureCode = failureCode;
    }

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

    /// <summary>Gets whether the implementation's task work completed; this is not product acceptance.</summary>
    public bool IsCompleted => Reason == AgentTerminationReason.Completed;

    /// <summary>Gets whether completed work remains in an incomplete execution.</summary>
    public bool HasPartialProgress => !IsCompleted && CompletedWorkUnits > 0;
}

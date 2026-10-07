using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;

namespace SolusAgent.Api.Context;

/// <summary>Context admission independent of task work or product acceptance.</summary>
public enum ContextAdmission
{
    /// <summary>Fresh execution was admitted.</summary>
    Fresh,
    /// <summary>Supplied state was admitted.</summary>
    Supplied,
    /// <summary>Current required guarantees or caller cancellation prevented admission.</summary>
    NotAttempted,
    /// <summary>Supplied state was rejected before work, progress or restricted capture.</summary>
    Rejected,
}

/// <summary>Closed safe context rejection categories without payload diagnostics.</summary>
public enum ContextRejectionCode
{
    /// <summary>No context rejection occurred.</summary>
    None,
    /// <summary>The implementation does not support supplied context.</summary>
    UnsupportedContext,
    /// <summary>The state belongs to a different implementation.</summary>
    ImplementationMismatch,
    /// <summary>The implementation does not accept this format.</summary>
    UnsupportedFormat,
    /// <summary>Required implementation compatibility was not established.</summary>
    IncompatibleContext,
    /// <summary>The implementation-owned payload is invalid.</summary>
    InvalidContext,
    /// <summary>The supplied state does not admit the selected transition.</summary>
    InvalidRunTransition,
}

/// <summary>Observation of the separate restricted transfer, not Host persistence or integrity.</summary>
public enum ContextCaptureStatus
{
    /// <summary>No restricted sink was requested.</summary>
    NotRequested,
    /// <summary>No state was transferred to the requested sink.</summary>
    Unavailable,
    /// <summary>The restricted callback returned normally; durable storage is not implied.</summary>
    Delivered,
    /// <summary>Restricted capture failed; the Host may already have received or stored state.</summary>
    Failed,
}

/// <summary>Closed ordinary metadata separating context rejection, work outcome and restricted transfer.</summary>
/// <remarks>A rejected context has no work outcome. Constructors check structure, not authentic provenance. No context, request, exception or raw diagnostic text is retained.</remarks>
public sealed class ContextExecutionResult
{
    /// <summary>Creates coherent same-call observations without fabricating a work failure for rejected context.</summary>
    /// <exception cref="ArgumentException">Identity, association, admission, rejection or capture observations disagree.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An enumeration is undefined.</exception>
    public ContextExecutionResult(Guid executionId, ContextExecutionIntent intent, ContextAdmission admission,
        AgentOutcome? outcome = null, ContextRejectionCode rejectionCode = ContextRejectionCode.None,
        ContextCaptureStatus captureStatus = ContextCaptureStatus.NotRequested)
    {
        if (executionId == Guid.Empty) throw new ArgumentException("An execution identity is required.", nameof(executionId));
        if (!Enum.IsDefined(intent)) throw new ArgumentOutOfRangeException(nameof(intent));
        if (!Enum.IsDefined(admission)) throw new ArgumentOutOfRangeException(nameof(admission));
        if (!Enum.IsDefined(rejectionCode)) throw new ArgumentOutOfRangeException(nameof(rejectionCode));
        if (!Enum.IsDefined(captureStatus)) throw new ArgumentOutOfRangeException(nameof(captureStatus));
        var coherent = admission switch
        {
            ContextAdmission.Rejected => intent != ContextExecutionIntent.Fresh && rejectionCode != ContextRejectionCode.None && outcome is null,
            ContextAdmission.Fresh => intent == ContextExecutionIntent.Fresh && AdmittedOutcome(),
            ContextAdmission.Supplied => intent != ContextExecutionIntent.Fresh && AdmittedOutcome(),
            ContextAdmission.NotAttempted => rejectionCode == ContextRejectionCode.None && outcome is not null
                && outcome.ExecutionId == executionId && outcome.CompletedWorkUnits == 0
                && outcome.Reason is AgentTerminationReason.Cancelled or AgentTerminationReason.UnsupportedCapability
                && (outcome.Usage is null || (outcome.Usage.Coverage == UsageInventoryCoverage.Complete && outcome.Usage.Attempts.Count == 0)),
            _ => false,
        };
        if (!coherent || (admission is ContextAdmission.Rejected or ContextAdmission.NotAttempted
            && captureStatus is ContextCaptureStatus.Delivered or ContextCaptureStatus.Failed))
            throw new ArgumentException("Context observations must agree.", nameof(admission));
        ExecutionId = executionId;
        Intent = intent;
        Admission = admission;
        Outcome = outcome;
        RejectionCode = rejectionCode;
        CaptureStatus = captureStatus;

        bool AdmittedOutcome() => rejectionCode == ContextRejectionCode.None && outcome is not null
            && outcome.ExecutionId == executionId && outcome.Reason != AgentTerminationReason.UnsupportedCapability;
    }

    /// <summary>Gets Host correlation; this does not create a retained-state lookup address.</summary>
    public Guid ExecutionId { get; }
    /// <summary>Gets the requested explicit lifecycle choice.</summary>
    public ContextExecutionIntent Intent { get; }
    /// <summary>Gets admission independently of task completion.</summary>
    public ContextAdmission Admission { get; }
    /// <summary>Gets the actual work outcome, or null when supplied context was rejected before work.</summary>
    public AgentOutcome? Outcome { get; }
    /// <summary>Gets a closed reason only for rejected supplied state.</summary>
    public ContextRejectionCode RejectionCode { get; }
    /// <summary>Gets the separate restricted transfer observation, never a Host storage/effect claim.</summary>
    public ContextCaptureStatus CaptureStatus { get; }
    /// <summary>Returns only safe closed metadata.</summary>
    public override string ToString() => $"ContextExecutionResult {{ Intent = {Intent}, Admission = {Admission}, RejectionCode = {RejectionCode}, CaptureStatus = {CaptureStatus} }}";
}

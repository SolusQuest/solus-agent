using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Providers;

namespace SolusAgent.Runtime.Api.Exposure;

/// <summary>Host-reported acknowledgement strength, never library certification of storage.</summary>
public enum ExposureStrength
{
    /// <summary>Host acknowledges in memory.</summary>
    Volatile,
    /// <summary>Host asserts its required durable-exposure condition has been satisfied.</summary>
    Durable,
}

/// <summary>Receipt delivery status, independent from accounting settlement or provider dispatch.</summary>
public enum RuntimeHookStatus
{
    /// <summary>The Host returned a correlated decision, subject to consumer verification.</summary>
    Acknowledged,
    /// <summary>The Host reports failure; storage/effects may already exist.</summary>
    Failed,
    /// <summary>The Host decision is unknown, not an implicit denial or permission.</summary>
    Unknown,
}

/// <summary>A Host pre-dispatch decision, not descriptive tool metadata or model authority.</summary>
public enum ExposureDecision
{
    /// <summary>Permission still requires full association, sufficient strength and final cancellation checks.</summary>
    Permit,
    /// <summary>The Host deliberately denies this dispatch.</summary>
    Deny,
}

/// <summary>A correlated Host instruction after closure; does not grant automatic retry or new effects.</summary>
public enum RuntimeContinuation
{
    /// <summary>Subsequent explicit attempts may be newly admitted under current controls.</summary>
    Continue,
    /// <summary>Stop further admission without rewriting earlier evidence.</summary>
    Stop,
}

/// <summary>Closed admission/integration stops; none proves rollback, durable accounting or remote stop.</summary>
public enum RuntimeStop
{
    /// <summary>No integration stop.</summary>
    None,
    /// <summary>A required guarantee is unsupported.</summary>
    UnsupportedCapability,
    /// <summary>A required Host integration is absent.</summary>
    MissingHooks,
    /// <summary>Configuration/request/inventory association is invalid.</summary>
    InvalidAssociation,
    /// <summary>A finite count/payload or consumer capacity prevents admission.</summary>
    ResourceLimit,
    /// <summary>Caller cancellation observed; not remote-stop proof.</summary>
    Cancelled,
    /// <summary>The Host deliberately denied dispatch.</summary>
    ExposureDenied,
    /// <summary>An attempted exposure callback returned no receipt.</summary>
    ExposureMissing,
    /// <summary>The exposure exchange failed; no dispatch permission exists.</summary>
    ExposureFailed,
    /// <summary>The exposure decision is unknown; no dispatch permission exists.</summary>
    ExposureUnknown,
    /// <summary>The exposure receipt belongs to different scope/attempt/requirement.</summary>
    ExposureMismatch,
    /// <summary>A volatile acknowledgement cannot satisfy required durable acknowledgement.</summary>
    DurableAcknowledgementRequired,
    /// <summary>The settlement callback returned no receipt.</summary>
    SettlementMissing,
    /// <summary>The settlement exchange failed; retained attempt evidence remains.</summary>
    SettlementFailed,
    /// <summary>The settlement decision is unknown; retained attempt evidence remains.</summary>
    SettlementUnknown,
    /// <summary>A settlement receipt belongs to a different exposure.</summary>
    SettlementMismatch,
    /// <summary>A valid Host settlement decision stopped further admission.</summary>
    HostStopped,
}

/// <summary>Host exposure intent before dispatch, identified by existing scope and full physical-attempt identity.</summary>
public sealed class RuntimeExposure
{
    /// <summary>Creates immutable restricted Host association; no payload, endpoint, credential or new attempt identity.</summary>
    public RuntimeExposure(ProviderScope scope, ProviderAttempt attempt, ExposureStrength requiredAcknowledgement)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Attempt = attempt ?? throw new ArgumentNullException(nameof(attempt));
        if (!Enum.IsDefined(requiredAcknowledgement)) throw new ArgumentOutOfRangeException(nameof(requiredAcknowledgement));
        RequiredAcknowledgement = requiredAcknowledgement;
    }
    /// <summary>Gets exact scope in the trusted Host channel, excluded from ordinary diagnostics.</summary>
    public ProviderScope Scope { get; }
    /// <summary>Gets existing full execution/logical/physical/ordinal association.</summary>
    public ProviderAttempt Attempt { get; }
    /// <summary>Gets the required Host-reported strength.</summary>
    public ExposureStrength RequiredAcknowledgement { get; }
    /// <summary>Compares every scope/attempt/requirement field without normalization.</summary>
    public bool Matches(RuntimeExposure other) => other is not null && Scope.Matches(other.Scope) && Attempt.Matches(other.Attempt)
        && RequiredAcknowledgement == other.RequiredAcknowledgement;
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(RuntimeExposure);
}

/// <summary>Host exposure receipt; missing delivery is represented by a null callback result.</summary>
public sealed class ExposureAcknowledgement
{
    /// <summary>Creates coherent closed feedback. Failed/Unknown carry no decision or strength; acknowledged Permit requires strength.</summary>
    public ExposureAcknowledgement(RuntimeExposure exposure, RuntimeHookStatus status, ExposureDecision? decision = null, ExposureStrength? strength = null)
    {
        Exposure = exposure ?? throw new ArgumentNullException(nameof(exposure));
        if (!Enum.IsDefined(status) || (decision.HasValue && !Enum.IsDefined(decision.Value)) || (strength.HasValue && !Enum.IsDefined(strength.Value)))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (status == RuntimeHookStatus.Acknowledged ? decision is null || (decision == ExposureDecision.Permit && strength is null) : decision is not null || strength is not null)
            throw new ArgumentException("The exposure receipt is incoherent.");
        Status = status; Decision = decision; Strength = strength;
    }
    /// <summary>Gets asserted original exposure, verified by the consumer.</summary>
    public RuntimeExposure Exposure { get; }
    /// <summary>Gets receipt status, not storage or dispatch knowledge.</summary>
    public RuntimeHookStatus Status { get; }
    /// <summary>Gets a Host decision only after acknowledgement.</summary>
    public ExposureDecision? Decision { get; }
    /// <summary>Gets Host-reported strength, when supplied.</summary>
    public ExposureStrength? Strength { get; }
    /// <summary>Checks full correlation and permission/strength; the caller must still perform its final cancellation/admission cut.</summary>
    public RuntimeStop Assess(RuntimeExposure expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!expected.Matches(Exposure)) return RuntimeStop.ExposureMismatch;
        if (Status == RuntimeHookStatus.Failed) return RuntimeStop.ExposureFailed;
        if (Status == RuntimeHookStatus.Unknown) return RuntimeStop.ExposureUnknown;
        if (Decision == ExposureDecision.Deny) return RuntimeStop.ExposureDenied;
        return expected.RequiredAcknowledgement == ExposureStrength.Durable && Strength != ExposureStrength.Durable
            ? RuntimeStop.DurableAcknowledgementRequired : RuntimeStop.None;
    }
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(ExposureAcknowledgement);
}

/// <summary>Same-attempt closure retaining honest observations independently of Host receipt delivery.</summary>
public sealed class RuntimeSettlement
{
    /// <summary>Validates full association, original admission/provider phase and optional exchange outcome; does not implement accounting.</summary>
    public RuntimeSettlement(RuntimeExposure exposure, UsageAttemptObservation observation, RuntimeStop stop,
        ProviderOutcome? providerOutcome = null, ProviderError? providerError = null)
    {
        Exposure = exposure ?? throw new ArgumentNullException(nameof(exposure));
        Observation = observation ?? throw new ArgumentNullException(nameof(observation));
        if (!Enum.IsDefined(stop) || (providerOutcome.HasValue && !Enum.IsDefined(providerOutcome.Value)) || (providerError.HasValue && !Enum.IsDefined(providerError.Value)))
            throw new ArgumentOutOfRangeException(nameof(stop));
        var attempt = exposure.Attempt;
        if (attempt.ExecutionId != observation.ExecutionId || attempt.LogicalCallId != observation.LogicalCallId
            || attempt.PhysicalAttemptId != observation.PhysicalAttemptId || attempt.AttemptNumber != observation.AttemptNumber)
            throw new ArgumentException("The settlement association is invalid.");
        var deliveryStop = stop is RuntimeStop.SettlementMissing or RuntimeStop.SettlementFailed or RuntimeStop.SettlementUnknown
            or RuntimeStop.SettlementMismatch or RuntimeStop.HostStopped;
        if (deliveryStop || (providerOutcome.HasValue && stop is not (RuntimeStop.None or RuntimeStop.Cancelled))
            || providerOutcome.HasValue != providerError.HasValue
            || (providerOutcome.HasValue && ((providerOutcome == Providers.ProviderOutcome.Succeeded) != (providerError == Providers.ProviderError.None)))
            || (!providerOutcome.HasValue && (observation.Exposure != DispatchExposure.NotDispatched || stop == RuntimeStop.None
                || observation.Usage.Completeness != UsageCompleteness.Unavailable)))
            throw new ArgumentException("The settlement outcome is incoherent.");
        Stop = stop; ProviderOutcome = providerOutcome; ProviderError = providerError;
    }
    /// <summary>Gets original restricted Host exposure association.</summary>
    public RuntimeExposure Exposure { get; }
    /// <summary>Gets existing normalized exposure/measurement/accounting, never implicit zero or rollback.</summary>
    public UsageAttemptObservation Observation { get; }
    /// <summary>Gets prior admission/cancellation state; later settlement delivery and HostStopped cannot inhabit this field.</summary>
    public RuntimeStop Stop { get; }
    /// <summary>Gets actual provider outcome only when its exchange was invoked.</summary>
    public ProviderOutcome? ProviderOutcome { get; }
    /// <summary>Gets actual provider error only when its exchange was invoked.</summary>
    public ProviderError? ProviderError { get; }
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(RuntimeSettlement);
}

/// <summary>Host settlement receipt, separate from UsageAccounting.Settlement and the original provider result.</summary>
public sealed class SettlementAcknowledgement
{
    /// <summary>Creates a closed optional continuation decision; failed/unknown feedback has no instruction.</summary>
    public SettlementAcknowledgement(RuntimeExposure exposure, RuntimeHookStatus status, RuntimeContinuation? continuation = null)
    {
        Exposure = exposure ?? throw new ArgumentNullException(nameof(exposure));
        if (!Enum.IsDefined(status) || (continuation.HasValue && !Enum.IsDefined(continuation.Value))) throw new ArgumentOutOfRangeException(nameof(status));
        if ((status == RuntimeHookStatus.Acknowledged) != continuation.HasValue) throw new ArgumentException("The settlement receipt is incoherent.");
        Status = status; Continuation = continuation;
    }
    /// <summary>Gets asserted exposure whose full identity must match before using the instruction.</summary>
    public RuntimeExposure Exposure { get; }
    /// <summary>Gets hook acknowledgement, not durable business settlement.</summary>
    public RuntimeHookStatus Status { get; }
    /// <summary>Gets Continue/Stop only on acknowledged delivery; neither automatically retries anything.</summary>
    public RuntimeContinuation? Continuation { get; }
    /// <summary>Checks full correlation and classifies continuation without rewriting attempt evidence.</summary>
    public RuntimeStop Assess(RuntimeExposure expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!expected.Matches(Exposure)) return RuntimeStop.SettlementMismatch;
        if (Status == RuntimeHookStatus.Failed) return RuntimeStop.SettlementFailed;
        if (Status == RuntimeHookStatus.Unknown) return RuntimeStop.SettlementUnknown;
        return Continuation == RuntimeContinuation.Stop ? RuntimeStop.HostStopped : RuntimeStop.None;
    }
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(SettlementAcknowledgement);
}

/// <summary>Narrow trusted Host integration; it receives no model/provider/tool payload or arbitrary service authority.</summary>
public interface IRuntimeExposureHooks
{
    /// <summary>Reports exposure intent before dispatch. Null/failed/unknown/denied/mismatched/weak receipts grant no authority.</summary>
    ValueTask<ExposureAcknowledgement?> BeforeDispatchAsync(RuntimeExposure exposure, CancellationToken cancellationToken);
    /// <summary>Reports same-attempt closure, including acknowledged but NotDispatched cancellation. Failure cannot erase usage; no storage/retry/liveness guarantee is implemented here.</summary>
    ValueTask<SettlementAcknowledgement?> AfterAttemptAsync(RuntimeSettlement settlement, CancellationToken cancellationToken);
}

/// <summary>Explicit ordinary diagnostic projection, omitting scope labels, live objects and all restricted payload.</summary>
public sealed record RuntimeAttemptDiagnostic(Guid ExecutionId, Guid LogicalCallId, Guid PhysicalAttemptId, int AttemptNumber,
    RuntimeStop AdmissionStop, RuntimeStop SettlementStop, DispatchExposure Exposure, UsageCompleteness UsageCompleteness,
    ProviderOutcome? ProviderOutcome, ProviderError? ProviderError);

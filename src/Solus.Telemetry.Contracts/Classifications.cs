namespace Solus.Telemetry.Contracts;

/// <summary>Knowledge of the supplied current membership, independent of token measurements.</summary>
public enum TelemetryInventoryCoverage
{
    /// <summary>Complete classification.</summary>
    Complete,
    /// <summary>Partial classification.</summary>
    Partial,
    /// <summary>Unavailable classification.</summary>
    Unavailable,
}

/// <summary>Knowledge of a measured count, never an accounting charge.</summary>
public enum TelemetryMeasurementCoverage
{
    /// <summary>Complete classification.</summary>
    Complete,
    /// <summary>Partial classification.</summary>
    Partial,
    /// <summary>Unavailable classification.</summary>
    Unavailable,
    /// <summary>Overflow classification.</summary>
    Overflow,
}

/// <summary>Product outcome classification, independent of telemetry delivery.</summary>
public enum TelemetryOutcomeKind
{
    /// <summary>Succeeded classification.</summary>
    Succeeded,
    /// <summary>Partial classification.</summary>
    Partial,
    /// <summary>Failed classification.</summary>
    Failed,
    /// <summary>Cancelled classification.</summary>
    Cancelled,
    /// <summary>Rejected classification.</summary>
    Rejected,
    /// <summary>Unknown classification.</summary>
    Unknown,
}

/// <summary>Provider-reported counter kind; no missing partition is inferred.</summary>
public enum TelemetryCounterKind
{
    /// <summary>CacheRead classification.</summary>
    CacheRead,
    /// <summary>CacheWrite classification.</summary>
    CacheWrite,
    /// <summary>UncachedInput classification.</summary>
    UncachedInput,
    /// <summary>Reasoning classification.</summary>
    Reasoning,
}

/// <summary>Producer-declared relationship to core tokens; overlapping facts are not additive.</summary>
public enum TelemetryCounterRelationship
{
    /// <summary>IncludedInInput classification.</summary>
    IncludedInInput,
    /// <summary>IncludedInOutput classification.</summary>
    IncludedInOutput,
    /// <summary>Independent classification.</summary>
    Independent,
}

/// <summary>Origin of an accounting summary, separate from token observations.</summary>
public enum TelemetryAccountingSource
{
    /// <summary>ProviderClaim classification.</summary>
    ProviderClaim,
    /// <summary>RunLedger classification.</summary>
    RunLedger,
}

/// <summary>Meaning of an accounting amount; only remaining allowance may be negative.</summary>
public enum TelemetryAccountingKind
{
    /// <summary>Reservation classification.</summary>
    Reservation,
    /// <summary>Released classification.</summary>
    Released,
    /// <summary>Measured classification.</summary>
    Measured,
    /// <summary>ConservativeCharge classification.</summary>
    ConservativeCharge,
    /// <summary>Unresolved classification.</summary>
    Unresolved,
    /// <summary>Allowance classification.</summary>
    Allowance,
    /// <summary>RemainingAllowance classification.</summary>
    RemainingAllowance,
}

/// <summary>Knowledge of a balance or estimate, independent of measurement coverage.</summary>
public enum TelemetryAccountingKnowledge
{
    /// <summary>Known classification.</summary>
    Known,
    /// <summary>Provisional classification.</summary>
    Provisional,
    /// <summary>Unknown classification.</summary>
    Unknown,
    /// <summary>Unavailable classification.</summary>
    Unavailable,
    /// <summary>Overflow classification.</summary>
    Overflow,
}

/// <summary>Provider settlement summary; mixed retains differing reported settlement classes.</summary>
public enum TelemetrySettlement
{
    /// <summary>Unknown classification.</summary>
    Unknown,
    /// <summary>Unsettled classification.</summary>
    Unsettled,
    /// <summary>Settled classification.</summary>
    Settled,
    /// <summary>Mixed classification.</summary>
    Mixed,
}

/// <summary>Safe bounded telemetry admission result, containing no producer input.</summary>
public enum TelemetryDiagnostic
{
    /// <summary>None classification.</summary>
    None,
    /// <summary>InvalidInput classification.</summary>
    InvalidInput,
    /// <summary>UnsupportedSchema classification.</summary>
    UnsupportedSchema,
    /// <summary>RecordTooLarge classification.</summary>
    RecordTooLarge,
    /// <summary>ExtensionsTooLarge classification.</summary>
    ExtensionsTooLarge,
}

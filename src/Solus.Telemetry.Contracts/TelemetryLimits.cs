namespace Solus.Telemetry.Contracts;

/// <summary>Observable v1 admission limits, measured on compact serialized UTF-8 JSON.</summary>
public static class TelemetryLimits
{
    /// <summary>Maximum complete record bytes.</summary>
    public const int MaximumRecordBytes = 256 * 1024;
    /// <summary>Maximum combined extension object bytes, including namespace keys and delimiters.</summary>
    public const int MaximumExtensionBytes = 32 * 1024;
    /// <summary>Maximum properties in every extension object.</summary>
    public const int MaximumObjectProperties = 64;
    /// <summary>Maximum nested extension containers; the extensions root is level one.</summary>
    public const int MaximumExtensionDepth = 8;
    /// <summary>Maximum distinct current run summaries.</summary>
    public const int MaximumRuns = 256;
    /// <summary>Maximum provider kind/relationship groups per run.</summary>
    public const int MaximumProviderCounters = 12;
    /// <summary>Maximum source/classification accounting groups per run.</summary>
    public const int MaximumAccountingObservations = 14;
    /// <summary>Maximum independently classified currency estimates per run.</summary>
    public const int MaximumCostEstimates = 16;
}

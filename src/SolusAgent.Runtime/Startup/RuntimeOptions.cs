using SolusAgent.Runtime.Api.Providers;

namespace SolusAgent.Runtime.Startup;

/// <summary>Finite memory and closure policy for the managed runtime; AgentRequest remains the run-limit authority.</summary>
public sealed class RuntimeOptions
{
    /// <summary>Creates finite implementation capacities and an explicit post-cut closure allowance.</summary>
    public RuntimeOptions(TimeProvider? timeProvider = null, int maximumAttempts = 64, int maximumRecords = 64,
        int maximumRetainedBytes = ProviderLimits.RequestBytes + ProviderLimits.ResponseBytes,
        TimeSpan? settlementGrace = null, bool requireContinuation = false)
    {
        if (maximumAttempts is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        if (maximumRecords is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maximumRecords));
        if (maximumRetainedBytes is < 1 or > ProviderLimits.RequestBytes + ProviderLimits.ResponseBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumRetainedBytes));
        var grace = settlementGrace ?? TimeSpan.FromSeconds(1);
        if (grace <= TimeSpan.Zero || grace > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(settlementGrace));
        TimeProvider = timeProvider ?? TimeProvider.System;
        MaximumAttempts = maximumAttempts; MaximumRecords = maximumRecords; MaximumRetainedBytes = maximumRetainedBytes;
        SettlementGrace = grace; RequireContinuation = requireContinuation;
    }
    /// <summary>Gets the clock used for elapsed execution time and bounded asynchronous waiting.</summary>
    public TimeProvider TimeProvider { get; }
    /// <summary>Gets the finite per-run attempt inventory capacity, independent of completed work.</summary>
    public int MaximumAttempts { get; }
    /// <summary>Gets the total classified-input and accepted-model record capacity.</summary>
    public int MaximumRecords { get; }
    /// <summary>Gets the total variable retained input/definition/response bytes.</summary>
    public int MaximumRetainedBytes { get; }
    /// <summary>Gets the maximum single settlement wait, also allowed after an execution cut. It grants no new execution authority.</summary>
    public TimeSpan SettlementGrace { get; }
    /// <summary>Gets whether this Host-selected profile requires exact continuation support from its first request.</summary>
    public bool RequireContinuation { get; }
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(RuntimeOptions);
}

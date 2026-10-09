namespace SolusAgent.Api.Usage;

/// <summary>Requested count admission limits and post-response token thresholds; enforcement requires advertised capabilities.</summary>
public sealed class AgentUsageLimits
{
    /// <summary>Creates positive optional limits. Null means no configured limit in that dimension.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A configured value is not positive.</exception>
    public AgentUsageLimits(int? maximumLogicalCalls = null, int? maximumPhysicalDispatches = null, long? inputTokenThreshold = null, long? outputTokenThreshold = null,
        AgentAccountingPolicy? accountingPolicy = null)
    {
        if (maximumLogicalCalls is <= 0) throw new ArgumentOutOfRangeException(nameof(maximumLogicalCalls));
        if (maximumPhysicalDispatches is <= 0) throw new ArgumentOutOfRangeException(nameof(maximumPhysicalDispatches));
        if (inputTokenThreshold is <= 0) throw new ArgumentOutOfRangeException(nameof(inputTokenThreshold));
        if (outputTokenThreshold is <= 0) throw new ArgumentOutOfRangeException(nameof(outputTokenThreshold));
        MaximumLogicalCalls = maximumLogicalCalls;
        MaximumPhysicalDispatches = maximumPhysicalDispatches;
        InputTokenThreshold = inputTokenThreshold;
        OutputTokenThreshold = outputTokenThreshold;
        if (accountingPolicy is { UnknownUsage: not UnknownUsagePolicy.Stop } && maximumPhysicalDispatches is null)
            throw new ArgumentException("Unknown continuation requires an explicit finite physical-dispatch limit.", nameof(maximumPhysicalDispatches));
        AccountingPolicy = accountingPolicy;
    }

    /// <summary>Gets the requested maximum admitted distinct logical calls, including calls that fail or never dispatch.</summary>
    public int? MaximumLogicalCalls { get; }
    /// <summary>Gets the requested maximum possible physical dispatches, including retries, reserved before exposure.</summary>
    /// <remarks>Confirmed NotDispatched releases its slot once; Dispatched or Unknown retains it. Attempt inventory remains independently bounded.</remarks>
    public int? MaximumPhysicalDispatches { get; }
    /// <summary>Gets the cumulative observed input threshold for stopping subsequent work, not a strict ceiling.</summary>
    public long? InputTokenThreshold { get; }
    /// <summary>Gets the cumulative observed output threshold for stopping subsequent work, not a strict ceiling.</summary>
    public long? OutputTokenThreshold { get; }
    /// <summary>Gets optional Host accounting policy, distinct from observed thresholds and authoritative only in supporting implementations.</summary>
    public AgentAccountingPolicy? AccountingPolicy { get; }
}

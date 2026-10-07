namespace SolusAgent.Api.Usage;

/// <summary>Requested count admission limits and post-response token thresholds; enforcement requires advertised capabilities.</summary>
public sealed class AgentUsageLimits
{
    /// <summary>Creates positive optional limits. Null means no configured limit in that dimension.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A configured value is not positive.</exception>
    public AgentUsageLimits(int? maximumLogicalCalls = null, int? maximumPhysicalDispatches = null, long? inputTokenThreshold = null, long? outputTokenThreshold = null)
    {
        if (maximumLogicalCalls is <= 0) throw new ArgumentOutOfRangeException(nameof(maximumLogicalCalls));
        if (maximumPhysicalDispatches is <= 0) throw new ArgumentOutOfRangeException(nameof(maximumPhysicalDispatches));
        if (inputTokenThreshold is <= 0) throw new ArgumentOutOfRangeException(nameof(inputTokenThreshold));
        if (outputTokenThreshold is <= 0) throw new ArgumentOutOfRangeException(nameof(outputTokenThreshold));
        MaximumLogicalCalls = maximumLogicalCalls;
        MaximumPhysicalDispatches = maximumPhysicalDispatches;
        InputTokenThreshold = inputTokenThreshold;
        OutputTokenThreshold = outputTokenThreshold;
    }

    /// <summary>Gets the requested pre-dispatch maximum distinct logical calls.</summary>
    public int? MaximumLogicalCalls { get; }
    /// <summary>Gets the requested pre-dispatch maximum physical attempts, including retries.</summary>
    public int? MaximumPhysicalDispatches { get; }
    /// <summary>Gets the cumulative observed input threshold for stopping subsequent work, not a strict ceiling.</summary>
    public long? InputTokenThreshold { get; }
    /// <summary>Gets the cumulative observed output threshold for stopping subsequent work, not a strict ceiling.</summary>
    public long? OutputTokenThreshold { get; }
}

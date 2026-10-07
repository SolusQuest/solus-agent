namespace SolusAgent.Api.Usage;

/// <summary>Availability of core measurements, independent of inventory coverage and settlement.</summary>
public enum UsageCompleteness
{
    /// <summary>Both input and output counts are known; optional details may still be missing.</summary>
    Complete,
    /// <summary>Some measurement is known, but at least one core count is unknown.</summary>
    Partial,
    /// <summary>No core or detail measurement is known.</summary>
    Unavailable,
}

/// <summary>A provider-specific token fact whose relationship is declared by its producer.</summary>
public enum ProviderTokenCounterKind
{
    /// <summary>Provider-reported cache-read tokens.</summary>
    CacheRead,
    /// <summary>Provider-reported cache-write tokens.</summary>
    CacheWrite,
    /// <summary>Independently provider-reported uncached input; never inferred from a missing peer.</summary>
    UncachedInput,
    /// <summary>Provider-reported reasoning tokens; no reasoning content.</summary>
    Reasoning,
}

/// <summary>How one detail relates to core counts according to the actual provider semantics.</summary>
public enum TokenCounterRelationship
{
    /// <summary>The detail is included in the input count when that parent is known.</summary>
    IncludedInInput,
    /// <summary>The detail is included in the output count when that parent is known.</summary>
    IncludedInOutput,
    /// <summary>No subset relationship to a core count is asserted.</summary>
    Independent,
}

/// <summary>An immutable independently available detail counter, without provider wire data.</summary>
public sealed class ProviderTokenCounter
{
    /// <summary>Creates a nonnegative or explicitly unknown counter.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An enum is undefined or the count is negative.</exception>
    /// <exception cref="ArgumentException">An intrinsically input counter is declared as an output subset.</exception>
    public ProviderTokenCounter(ProviderTokenCounterKind kind, long? value, TokenCounterRelationship relationship)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(relationship)) throw new ArgumentOutOfRangeException(nameof(relationship));
        if (value is < 0) throw new ArgumentOutOfRangeException(nameof(value));
        if (kind == ProviderTokenCounterKind.UncachedInput && relationship == TokenCounterRelationship.IncludedInOutput)
            throw new ArgumentException("An uncached input counter cannot be an output subset.", nameof(relationship));
        Kind = kind;
        Value = value;
        Relationship = relationship;
    }
    /// <summary>Gets the closed provider-detail kind.</summary>
    public ProviderTokenCounterKind Kind { get; }
    /// <summary>Gets the count, including known zero, or null for unknown.</summary>
    public long? Value { get; }
    /// <summary>Gets the producer-declared relationship; overlapping details must not be summed.</summary>
    public TokenCounterRelationship Relationship { get; }
}

/// <summary>Immutable measurements preserving missing values, optional provider facts and known zero.</summary>
public sealed class UsageObservation
{
    /// <summary>Copies detail counters and validates each known declared subset against a known parent.</summary>
    /// <remarks>No missing peer or total is inferred, and overlapping details are never added to core counts.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">A core count is negative.</exception>
    /// <exception cref="ArgumentException">A detail is null, duplicated or exceeds its known parent.</exception>
    public UsageObservation(long? inputTokens = null, long? outputTokens = null, IReadOnlyList<ProviderTokenCounter>? providerCounters = null)
    {
        if (inputTokens is < 0) throw new ArgumentOutOfRangeException(nameof(inputTokens));
        if (outputTokens is < 0) throw new ArgumentOutOfRangeException(nameof(outputTokens));
        var snapshot = providerCounters?.ToArray() ?? [];
        var kinds = new HashSet<ProviderTokenCounterKind>();
        foreach (var counter in snapshot)
        {
            if (counter is null || !kinds.Add(counter.Kind)) throw new ArgumentException("Detail kinds must be non-null and unique.", nameof(providerCounters));
            var parent = counter.Relationship switch
            {
                TokenCounterRelationship.IncludedInInput => inputTokens,
                TokenCounterRelationship.IncludedInOutput => outputTokens,
                _ => null,
            };
            if (counter.Value is long value && parent is long total && value > total)
                throw new ArgumentException("A known detail cannot exceed its declared known parent.", nameof(providerCounters));
        }
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        ProviderCounters = Array.AsReadOnly(snapshot);
        Completeness = inputTokens.HasValue && outputTokens.HasValue ? UsageCompleteness.Complete
            : inputTokens.HasValue || outputTokens.HasValue || snapshot.Any(counter => counter.Value.HasValue) ? UsageCompleteness.Partial
            : UsageCompleteness.Unavailable;
    }
    /// <summary>Gets actual input consumption, including known zero, or null for unknown.</summary>
    public long? InputTokens { get; }
    /// <summary>Gets actual output consumption, including known zero, or null for unknown.</summary>
    public long? OutputTokens { get; }
    /// <summary>Gets copied optional facts; absence does not assert zero.</summary>
    public IReadOnlyList<ProviderTokenCounter> ProviderCounters { get; }
    /// <summary>Gets derived core measurement completeness, independent of optional detail availability.</summary>
    public UsageCompleteness Completeness { get; }
    internal bool HasPositiveConsumption => InputTokens is > 0 || OutputTokens is > 0 || ProviderCounters.Any(counter => counter.Value is > 0);
}

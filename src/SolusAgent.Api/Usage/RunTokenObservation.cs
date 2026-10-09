namespace SolusAgent.Api.Usage;

/// <summary>Coverage of one core token dimension across a validated attempt inventory.</summary>
public enum TokenObservationCoverage
{
    /// <summary>The sum covers the complete inventory, including known absence of dispatch.</summary>
    Complete,
    /// <summary>A known subtotal exists, but inventory or measurements are incomplete.</summary>
    Partial,
    /// <summary>No sum is known; this is not zero consumption.</summary>
    Unavailable,
    /// <summary>The known measurements overflow Int64, even if other measurements are missing.</summary>
    Overflow,
}

/// <summary>An immutable checked core-token sum with independent measurement coverage.</summary>
/// <remarks>Provider details, reservations and charges never contribute to this sum.</remarks>
public sealed class RunTokenObservation
{
    private RunTokenObservation(long? observedTokens, TokenObservationCoverage coverage)
    { ObservedTokens = observedTokens; Coverage = coverage; }

    /// <summary>Gets the known sum, or a subtotal when Partial; null means unavailable or overflow, never a saturated value.</summary>
    public long? ObservedTokens { get; }
    /// <summary>Gets coverage of this dimension. Overflow takes precedence over missing inventory or measurements.</summary>
    public TokenObservationCoverage Coverage { get; }

    internal static RunTokenObservation Aggregate(UsageInventoryCoverage inventory,
        IReadOnlyList<UsageAttemptObservation> attempts, Func<UsageObservation, long?> read)
    {
        long sum = 0;
        var complete = inventory == UsageInventoryCoverage.Complete;
        var known = complete && attempts.Count == 0;
        foreach (var attempt in attempts)
        {
            // No dispatch proves no consumption, without replacing the producer's missing measurement.
            var value = attempt.Exposure == DispatchExposure.NotDispatched ? 0 : read(attempt.Usage);
            if (value is not long measured) { complete = false; continue; }
            known = true;
            try { sum = checked(sum + measured); }
            catch (OverflowException) { return new(null, TokenObservationCoverage.Overflow); }
        }
        return new(known ? sum : null, complete ? TokenObservationCoverage.Complete
            : known ? TokenObservationCoverage.Partial : TokenObservationCoverage.Unavailable);
    }
}

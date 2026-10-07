namespace SolusAgent.Api.Usage;

/// <summary>Settlement classification, independent of dispatch exposure and measurement availability.</summary>
public enum UsageSettlement
{
    /// <summary>Settlement is not known.</summary>
    Unknown,
    /// <summary>Accounting is explicitly still unsettled.</summary>
    Unsettled,
    /// <summary>The producer reports settlement; this does not imply complete provider measurement.</summary>
    Settled,
}

/// <summary>A nonnegative pair for an explicitly named reservation or charge, not implicit measured usage.</summary>
public sealed class UsageTokenAmounts
{
    /// <summary>Creates a pair of nonnegative amounts.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An amount is negative.</exception>
    public UsageTokenAmounts(long inputTokens, long outputTokens)
    {
        if (inputTokens < 0) throw new ArgumentOutOfRangeException(nameof(inputTokens));
        if (outputTokens < 0) throw new ArgumentOutOfRangeException(nameof(outputTokens));
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
    }
    /// <summary>Gets the amount in the input dimension.</summary>
    public long InputTokens { get; }
    /// <summary>Gets the amount in the output dimension.</summary>
    public long OutputTokens { get; }
    internal bool IsPositive => InputTokens > 0 || OutputTokens > 0;
}

/// <summary>A disclosed estimate supplied by the producer, never a price calculation or invoice.</summary>
public sealed class UsageCostEstimate
{
    /// <summary>Creates a nonnegative estimate with a bounded three-uppercase-letter currency label.</summary>
    /// <remarks>The format does not establish support for a currency or rate policy.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The amount is negative.</exception>
    /// <exception cref="ArgumentException">The currency label has an invalid format.</exception>
    public UsageCostEstimate(decimal amount, string currency)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (currency is null || currency.Length != 3 || currency.Any(character => character is < 'A' or > 'Z'))
            throw new ArgumentException("A three-uppercase-letter currency label is required.", nameof(currency));
        Amount = amount;
        Currency = currency;
    }
    /// <summary>Gets the estimated amount, not settled billing.</summary>
    public decimal Amount { get; }
    /// <summary>Gets the bounded currency label.</summary>
    public string Currency { get; }
}

/// <summary>Producer-reported accounting claims kept separate from actual consumption.</summary>
public sealed class UsageAccounting
{
    /// <summary>Creates independent settlement, reservation, unobserved charge and estimate metadata.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Settlement is undefined.</exception>
    /// <exception cref="ArgumentException">Settled accounting still carries an in-flight reservation.</exception>
    public UsageAccounting(UsageSettlement settlement = UsageSettlement.Unknown, UsageTokenAmounts? inFlightReservation = null, UsageTokenAmounts? conservativeUnobservedCharge = null, UsageCostEstimate? estimatedCost = null)
    {
        if (!Enum.IsDefined(settlement)) throw new ArgumentOutOfRangeException(nameof(settlement));
        if (settlement == UsageSettlement.Settled && inFlightReservation is not null)
            throw new ArgumentException("Settled accounting cannot retain an in-flight reservation.", nameof(inFlightReservation));
        Settlement = settlement;
        InFlightReservation = inFlightReservation;
        ConservativeUnobservedCharge = conservativeUnobservedCharge;
        EstimatedCost = estimatedCost;
    }
    /// <summary>Gets reported settlement, not remote-stop or measurement proof.</summary>
    public UsageSettlement Settlement { get; }
    /// <summary>Gets an optional in-flight reservation; null means unreported.</summary>
    public UsageTokenAmounts? InFlightReservation { get; }
    /// <summary>Gets a conservative claim for unobserved exposure, excluding known consumption; never fabricated measured usage.</summary>
    public UsageTokenAmounts? ConservativeUnobservedCharge { get; }
    /// <summary>Gets an optional estimate independent of counters and charge.</summary>
    public UsageCostEstimate? EstimatedCost { get; }
}

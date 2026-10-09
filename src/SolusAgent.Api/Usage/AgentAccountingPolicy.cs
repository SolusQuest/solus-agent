namespace SolusAgent.Api.Usage;

/// <summary>Explicit Host treatment of missing core measurements, never invented measured consumption.</summary>
public enum UnknownUsagePolicy
{
    /// <summary>Stop when a configured comparison requires missing usage.</summary>
    Stop,
    /// <summary>Charge the reservation for each unobserved dimension, excluding known consumption.</summary>
    ConservativeCharge,
    /// <summary>Keep unresolved exposure and continue only within explicit count and duration bounds.</summary>
    ContinueUnknown,
}

/// <summary>Immutable Host estimates and accounting allowances, separate from observed token thresholds.</summary>
/// <remarks>Neither reservations nor allowances assert a physical-token or billing ceiling.</remarks>
public sealed class AgentAccountingPolicy
{
    /// <summary>Requires an explicit finite reservation for both dimensions; zero is an explicit estimate, not proof of no consumption.</summary>
    public AgentAccountingPolicy(UsageTokenAmounts reservation, long? inputAllowance = null, long? outputAllowance = null,
        UnknownUsagePolicy unknownUsage = UnknownUsagePolicy.Stop)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        if (inputAllowance is <= 0) throw new ArgumentOutOfRangeException(nameof(inputAllowance));
        if (outputAllowance is <= 0) throw new ArgumentOutOfRangeException(nameof(outputAllowance));
        if (!Enum.IsDefined(unknownUsage)) throw new ArgumentOutOfRangeException(nameof(unknownUsage));
        Reservation = reservation; InputAllowance = inputAllowance; OutputAllowance = outputAllowance; UnknownUsage = unknownUsage;
    }
    /// <summary>Gets the fixed Host estimate reserved for each admitted physical attempt.</summary>
    public UsageTokenAmounts Reservation { get; }
    /// <summary>Gets optional input accounting allowance, not a measurement threshold.</summary>
    public long? InputAllowance { get; }
    /// <summary>Gets optional output accounting allowance, not a measurement threshold.</summary>
    public long? OutputAllowance { get; }
    /// <summary>Gets explicit missing-measurement treatment.</summary>
    public UnknownUsagePolicy UnknownUsage { get; }
    /// <summary>Compares all policy values without object-identity requirements.</summary>
    public bool Matches(AgentAccountingPolicy other) => other is not null && Reservation.InputTokens == other.Reservation.InputTokens
        && Reservation.OutputTokens == other.Reservation.OutputTokens && InputAllowance == other.InputAllowance
        && OutputAllowance == other.OutputAllowance && UnknownUsage == other.UnknownUsage;
}

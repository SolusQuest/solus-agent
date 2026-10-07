namespace SolusAgent.Api.Execution;

/// <summary>Finite positive limits requested by the trusted Host for one execution.</summary>
/// <remarks>Require the corresponding capability when enforcement is necessary. Work-unit meaning belongs to the implementation.</remarks>
public sealed class AgentExecutionBounds
{
    /// <summary>Creates validated requested limits.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A requested limit is not positive.</exception>
    public AgentExecutionBounds(int maximumWorkUnits, TimeSpan maximumDuration)
    {
        if (maximumWorkUnits <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumWorkUnits));
        }

        if (maximumDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        }

        MaximumWorkUnits = maximumWorkUnits;
        MaximumDuration = maximumDuration;
    }

    /// <summary>Gets the positive maximum implementation-defined work units.</summary>
    public int MaximumWorkUnits { get; }

    /// <summary>Gets the positive finite requested duration; it may be advisory if enforcement is not required.</summary>
    public TimeSpan MaximumDuration { get; }
}

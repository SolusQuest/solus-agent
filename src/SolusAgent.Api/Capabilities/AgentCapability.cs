namespace SolusAgent.Api.Capabilities;

/// <summary>Guarantees an implementation advertises and a Host can require before execution.</summary>
/// <remarks>A requested bound alone does not imply any of these guarantees.</remarks>
[Flags]
public enum AgentCapability
{
    /// <summary>No guarantee is required or advertised.</summary>
    None = 0,

    /// <summary>The implementation enforces the requested maximum implementation-defined work units.</summary>
    WorkUnitLimit = 1,

    /// <summary>The implementation enforces the requested maximum execution duration.</summary>
    DurationLimit = 2,

    /// <summary>The implementation cooperatively observes caller cancellation.</summary>
    /// <remarks>Observation does not prove that remote work has stopped.</remarks>
    Cancellation = 4,
}

internal static class CapabilityValidation
{
    private const AgentCapability All = AgentCapability.WorkUnitLimit | AgentCapability.DurationLimit | AgentCapability.Cancellation;

    internal static void Validate(AgentCapability capabilities, string parameterName)
    {
        if ((capabilities & ~All) != AgentCapability.None)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

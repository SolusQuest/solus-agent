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

    /// <summary>Reports truthful nullable usage and retains observations across later failures; complete provider metrics are not promised.</summary>
    UsageReporting = 8,

    /// <summary>Enforces configured logical-call and physical-dispatch limits before dispatch.</summary>
    DispatchLimits = 16,

    /// <summary>Checks configured token thresholds after responses to stop subsequent work; not a strict pre-dispatch ceiling.</summary>
    UsageThresholds = 32,

    /// <summary>Enforces explicit Host reservation/accounting policy with ordered permission and truthful run-local settlement.</summary>
    UsageAccounting = 128,
}

internal static class CapabilityValidation
{
    private const AgentCapability All = AgentCapability.WorkUnitLimit | AgentCapability.DurationLimit | AgentCapability.Cancellation | AgentCapability.UsageReporting | AgentCapability.DispatchLimits | AgentCapability.UsageThresholds | AgentCapability.UsageAccounting;

    internal static void Validate(AgentCapability capabilities, string parameterName)
    {
        if ((capabilities & ~All) != AgentCapability.None)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

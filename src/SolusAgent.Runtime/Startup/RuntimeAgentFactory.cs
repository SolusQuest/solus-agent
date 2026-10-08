using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Execution;

namespace SolusAgent.Runtime.Startup;

/// <summary>Safe construction metadata without live objects, provider labels or restricted content.</summary>
public sealed record RuntimeStartupDisclosure(RuntimeSupport Support, RuntimeConfigurationDisclosure Configuration,
    int MaximumAttempts, int MaximumRecords, int MaximumRetainedBytes, TimeSpan SettlementGrace, bool RequireContinuation);

/// <summary>Constructs the managed runtime while business orchestration receives only IAgent.</summary>
public static class RuntimeAgentFactory
{
    /// <summary>Gets exactly the implemented run and runtime-specific guarantees. Usage limits beyond work/duration remain advisory.</summary>
    public static RuntimeSupport Support { get; } = new(
        AgentCapability.WorkUnitLimit | AgentCapability.DurationLimit | AgentCapability.Cancellation | AgentCapability.UsageReporting,
        RuntimeGuarantee.OrderedExposure | RuntimeGuarantee.ProviderBounds);

    /// <summary>Validates required startup integration before effects and returns an agent with isolated per-call state.</summary>
    /// <remarks>No tool invocation, candidate delivery, context restoration or provider retry is implemented by this path.</remarks>
    public static IAgent Create(RuntimeConfiguration configuration, RuntimeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        options ??= new();
        bool unsupported;
        try
        {
            unsupported = configuration.CheckSupport(Support) != RuntimeStop.None
                || (configuration.Hooks is null && configuration.RequiredAcknowledgement == ExposureStrength.Durable)
                || (options.RequireContinuation && !configuration.Provider.Capabilities.HasFlag(ProviderCapabilities.Continuation));
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        { throw new ArgumentException("The required runtime integration is unavailable.", nameof(configuration)); }
        if (unsupported)
            throw new ArgumentException("The required runtime integration is unavailable.", nameof(configuration));
        return new RuntimeAgent(configuration, options);
    }

    /// <summary>Projects explicit closed metadata; does not certify Host durability or reveal its live bindings.</summary>
    public static RuntimeStartupDisclosure Describe(RuntimeConfiguration configuration, RuntimeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        options ??= new();
        return new(Support, configuration.Describe(), options.MaximumAttempts, options.MaximumRecords,
            options.MaximumRetainedBytes, options.SettlementGrace, options.RequireContinuation);
    }
}

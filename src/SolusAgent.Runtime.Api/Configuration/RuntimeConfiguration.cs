using SolusAgent.Api.Capabilities;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;

namespace SolusAgent.Runtime.Api.Configuration;

/// <summary>New runtime-specific guarantees; existing run guarantees remain AgentCapability values.</summary>
[Flags]
public enum RuntimeGuarantee
{
    /// <summary>No runtime-specific guarantee is required or disclosed.</summary>
    None = 0,
    /// <summary>Checks correlated Host permission and required acknowledgement strength before dispatch.</summary>
    OrderedExposure = 1,
    /// <summary>Validates configured provider payload/count ceilings before accepting exchange data.</summary>
    ProviderBounds = 2,
}

/// <summary>An implementation's disclosure, not Host policy or proof that an implementation enforces it.</summary>
public sealed class RuntimeSupport
{
    /// <summary>Creates a closed disclosure supplied by the consuming implementation.</summary>
    public RuntimeSupport(AgentCapability supportedCapabilities, RuntimeGuarantee guarantees)
    {
        const AgentCapability all = AgentCapability.WorkUnitLimit | AgentCapability.DurationLimit | AgentCapability.Cancellation
            | AgentCapability.UsageReporting | AgentCapability.DispatchLimits | AgentCapability.UsageThresholds | AgentCapability.UsageAccounting;
        if ((supportedCapabilities & ~all) != 0) throw new ArgumentOutOfRangeException(nameof(supportedCapabilities));
        Validate(guarantees);
        SupportedCapabilities = supportedCapabilities; Guarantees = guarantees;
    }
    /// <summary>Gets existing per-execution guarantees; the current AgentRequest supplies requirements.</summary>
    public AgentCapability SupportedCapabilities { get; }
    /// <summary>Gets this implementation's new runtime-specific guarantees.</summary>
    public RuntimeGuarantee Guarantees { get; }
    internal static void Validate(RuntimeGuarantee value)
    { if ((value & ~(RuntimeGuarantee.OrderedExposure | RuntimeGuarantee.ProviderBounds)) != 0) throw new ArgumentOutOfRangeException(nameof(value)); }
}

/// <summary>A live Host binding through the reusable interface, independent of how a tool was authored.</summary>
/// <remarks>Metadata matching supplies no concrete-type authority. The actual tool must validate its invocation capability and prepared call. Never save these live objects or use them as ordinary diagnostics.</remarks>
public sealed class RuntimeToolRegistration
{
    /// <summary>Snapshots descriptor association with an explicitly injected narrow capability.</summary>
    public RuntimeToolRegistration(IFunctionTool tool, IToolCapability capability)
    {
        Tool = tool ?? throw new ArgumentNullException(nameof(tool));
        Capability = capability ?? throw new ArgumentNullException(nameof(capability));
        try
        {
            Descriptor = tool.Descriptor;
            if (Descriptor is null || capability.CapabilityId != Descriptor.CapabilityId)
                throw new ArgumentException();
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        { throw new ArgumentException("The Host tool binding is invalid."); }
    }
    /// <summary>Gets the Host-installed tool interface; its metadata must remain stable for this binding.</summary>
    public IFunctionTool Tool { get; }
    /// <summary>Gets the live narrow Host capability; data cannot manufacture or replace it.</summary>
    public IToolCapability Capability { get; }
    /// <summary>Gets the immutable descriptor captured during Host registration.</summary>
    public ToolDescriptor Descriptor { get; }
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(RuntimeToolRegistration);
}

/// <summary>Explicit ordinary configuration metadata containing no live objects, scope labels or run-policy inputs.</summary>
public sealed record RuntimeConfigurationDisclosure(int Tools, int MaximumInputs, int MaximumTools, int MaximumToolCalls,
    int MaximumRequestBytes, int MaximumResponseBytes, int MaximumContinuationBytes,
    ExposureStrength RequiredAcknowledgement, RuntimeGuarantee RequiredGuarantees);

/// <summary>Live Host-supplied self-owned extension configuration, separate from the current AgentRequest.</summary>
/// <remarks>Run bounds, optional usage limits and existing capability requirements remain solely on AgentRequest. Never place this container, provider, tools or hooks in model/saved/ordinary data; use Describe for ordinary metadata. This is not startup, storage, a scheduler or a hostile-code sandbox.</remarks>
public sealed class RuntimeConfiguration
{
    /// <summary>Copies a finite registry and retains only Host-installed extension authority and configuration ceilings.</summary>
    public RuntimeConfiguration(IModelProvider provider, IReadOnlyList<RuntimeToolRegistration> tools,
        IRuntimeExposureHooks? hooks, ProviderExchangeBounds? bounds = null,
        ExposureStrength requiredAcknowledgement = ExposureStrength.Volatile,
        RuntimeGuarantee requiredGuarantees = RuntimeGuarantee.OrderedExposure | RuntimeGuarantee.ProviderBounds)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Scope = provider.Scope ?? throw new ArgumentException("The Host provider scope is missing.");
        ProviderRequest.ValidateCapabilities(provider.Capabilities);
        if (!Enum.IsDefined(requiredAcknowledgement)) throw new ArgumentOutOfRangeException(nameof(requiredAcknowledgement));
        RuntimeSupport.Validate(requiredGuarantees);
        Bounds = bounds ?? new();
        var snapshot = ProviderBoundary.Copy(tools, Bounds.MaximumTools);
        ProviderBoundary.Require(snapshot.Select(binding => binding.Descriptor.Name).Distinct(StringComparer.Ordinal).Count() == snapshot.Length,
            ProviderError.InvalidAssociation);
        Tools = Array.AsReadOnly(snapshot); Hooks = hooks;
        RequiredAcknowledgement = requiredAcknowledgement; RequiredGuarantees = requiredGuarantees;
    }
    /// <summary>Gets the live Host provider; data never selects its transport.</summary>
    public IModelProvider Provider { get; }
    /// <summary>Gets captured exact provider/model scope for the restricted Host channel.</summary>
    public ProviderScope Scope { get; }
    /// <summary>Gets the bounded copied live interface bindings, never a model-selected registry.</summary>
    public IReadOnlyList<RuntimeToolRegistration> Tools { get; }
    /// <summary>Gets the narrow Host hooks, or absent integration to reject when required.</summary>
    public IRuntimeExposureHooks? Hooks { get; }
    /// <summary>Gets configuration-specific provider payload/count ceilings, not per-run policy.</summary>
    public ProviderExchangeBounds Bounds { get; }
    /// <summary>Gets required Host-reported acknowledgement strength; Durable is not library persistence proof.</summary>
    public ExposureStrength RequiredAcknowledgement { get; }
    /// <summary>Gets requirements for new runtime-specific semantics.</summary>
    public RuntimeGuarantee RequiredGuarantees { get; }
    /// <summary>Checks an implementation's declared support without executing work or certifying its claims.</summary>
    public RuntimeStop CheckSupport(RuntimeSupport support)
    {
        ArgumentNullException.ThrowIfNull(support);
        if ((RequiredGuarantees & ~support.Guarantees) != 0) return RuntimeStop.UnsupportedCapability;
        return (RequiredGuarantees & RuntimeGuarantee.OrderedExposure) != 0 && Hooks is null ? RuntimeStop.MissingHooks : RuntimeStop.None;
    }
    /// <summary>Constructs a Host-authorized value using installed descriptive metadata and finite bounds; performs no dispatch.</summary>
    public ProviderRequest CreateRequest(ProviderAttempt attempt, IReadOnlyList<ProviderInput> inputs,
        ProviderContinuation? continuation = null, ProviderCapabilities requiredCapabilities = ProviderCapabilities.None) =>
        new(Scope, attempt, inputs, Tools.Select(binding => binding.Descriptor).ToArray(), continuation, requiredCapabilities, Bounds);
    /// <summary>Projects explicitly safe metadata; does not disclose provider/client/tool/hook instances or credentials.</summary>
    public RuntimeConfigurationDisclosure Describe() => new(Tools.Count, Bounds.MaximumInputs, Bounds.MaximumTools, Bounds.MaximumToolCalls,
        Bounds.MaximumRequestBytes, Bounds.MaximumResponseBytes, Bounds.MaximumContinuationBytes, RequiredAcknowledgement, RequiredGuarantees);
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(RuntimeConfiguration);
}

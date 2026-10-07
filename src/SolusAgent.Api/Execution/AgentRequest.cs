using SolusAgent.Api.Usage;
using SolusAgent.Api.Capabilities;

namespace SolusAgent.Api.Execution;

/// <summary>Immutable Host control inputs and separately classified data for one execution.</summary>
/// <remarks>The caller must establish trusted instruction origin; this in-process contract does not authenticate the Host. Credentials and restricted state do not belong here.</remarks>
public sealed class AgentRequest
{
    /// <summary>Validates Host control inputs and takes a defensive snapshot of the data list before work can begin.</summary>
    /// <exception cref="ArgumentException">The identity is empty, trusted instructions are blank or a data member is null.</exception>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Required capabilities contain undefined flags.</exception>
    public AgentRequest(
        Guid executionId,
        string instructions,
        IReadOnlyList<AgentInput> data,
        AgentExecutionBounds bounds,
        AgentCapability requiredCapabilities,
        AgentUsageLimits? usageLimits = null)
    {
        if (executionId == Guid.Empty)
        {
            throw new ArgumentException("An execution identity is required.", nameof(executionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(instructions);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(bounds);
        CapabilityValidation.Validate(requiredCapabilities, nameof(requiredCapabilities));

        var snapshot = data.ToArray();
        if (snapshot.Any(item => item is null))
        {
            throw new ArgumentException("Data members must be non-null.", nameof(data));
        }

        ExecutionId = executionId;
        Instructions = instructions;
        Data = Array.AsReadOnly(snapshot);
        Bounds = bounds;
        RequiredCapabilities = requiredCapabilities;
        UsageLimits = usageLimits;
    }

    /// <summary>Gets the Host-supplied association retained by every progress observation and terminal outcome.</summary>
    public Guid ExecutionId { get; }

    /// <summary>Gets instructions obtained from the Host's trusted source, never inferred from data.</summary>
    public string Instructions { get; }

    /// <summary>Gets the copied read-only data inputs, which cannot redefine control inputs.</summary>
    public IReadOnlyList<AgentInput> Data { get; }

    /// <summary>Gets the Host-requested finite bounds, distinct from advertised enforcement guarantees.</summary>
    public AgentExecutionBounds Bounds { get; }

    /// <summary>Gets guarantees that must be supported or explicitly rejected before work or progress.</summary>
    public AgentCapability RequiredCapabilities { get; }

    /// <summary>Gets optional requested usage limits; their presence alone does not promise enforcement.</summary>
    public AgentUsageLimits? UsageLimits { get; }

    /// <summary>Returns only structural information; excludes instructions and data text.</summary>
    public override string ToString() => $"AgentRequest {{ DataCount = {Data.Count}, RequiredCapabilities = {RequiredCapabilities} }}";
}

namespace SolusAgent.Api.Context;

/// <summary>Restricted opaque state owned by one agent implementation, outside ordinary observations.</summary>
/// <remarks>Metadata is not authentication or a portable transcript. Producers exclude credentials, live clients, transports, delegates and tool instances. The Host owns integrity, protection, storage and retention; implementations validate their grammar and applicable compatibility before use.</remarks>
public sealed class AgentContextEnvelope
{
    private readonly byte[] payload;

    /// <summary>Validates structural metadata and copies restricted bytes without interpreting their grammar.</summary>
    /// <exception cref="ArgumentException">The implementation identity is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A format or compatibility discriminator is not positive.</exception>
    /// <exception cref="ArgumentNullException">Payload is null.</exception>
    public AgentContextEnvelope(Guid implementationId, int formatVersion, int compatibilityVersion, byte[] payload)
    {
        if (implementationId == Guid.Empty)
            throw new ArgumentException("An implementation identity is required.", nameof(implementationId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(formatVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(compatibilityVersion);
        ArgumentNullException.ThrowIfNull(payload);
        ImplementationId = implementationId;
        FormatVersion = formatVersion;
        CompatibilityVersion = compatibilityVersion;
        this.payload = payload.ToArray();
    }

    /// <summary>Gets the owning implementation discriminator, not an execution address or proof of origin.</summary>
    public Guid ImplementationId { get; }
    /// <summary>Gets the implementation-owned format discriminator, not a package support version.</summary>
    public int FormatVersion { get; }
    /// <summary>Gets the implementation-owned compatibility discriminator; runtime-specific checks remain required.</summary>
    public int CompatibilityVersion { get; }

    /// <summary>Explicitly copies restricted bytes for authorized storage or admission; never use as ordinary diagnostics.</summary>
    public byte[] CopyRestrictedPayload() => payload.ToArray();

    /// <summary>Returns structural metadata without any restricted bytes or text.</summary>
    public override string ToString() => $"AgentContextEnvelope {{ ImplementationId = {ImplementationId}, FormatVersion = {FormatVersion}, CompatibilityVersion = {CompatibilityVersion} }}";
}

namespace SolusAgent.Runtime.Api.Providers;

/// <summary>Exact restricted checkpoint binding supplied to provider-owned persistence admission, never dispatch authority.</summary>
public sealed record ProviderContextBinding(ProviderScope Scope, ProviderAttempt Origin, ProviderContinuation? Continuation);

/// <summary>Immutable bounded provider-owned state; only the provider interprets its format.</summary>
public sealed class ProviderSavedState
{
    private readonly byte[] payload;
    /// <summary>Copies bounded opaque bytes and exact original scope/attempt.</summary>
    public ProviderSavedState(ProviderScope scope, ProviderAttempt origin, int formatVersion, byte[] payload)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Origin = origin ?? throw new ArgumentNullException(nameof(origin));
        ArgumentNullException.ThrowIfNull(payload);
        if (formatVersion < 1 || payload.Length is < 1 or > ProviderLimits.ContinuationBytes) throw new ArgumentException("Invalid saved provider state.");
        FormatVersion = formatVersion; this.payload = payload.ToArray();
    }
    /// <summary>Gets original scope.</summary>
    public ProviderScope Scope { get; }
    /// <summary>Gets original attempt.</summary>
    public ProviderAttempt Origin { get; }
    /// <summary>Gets the provider-owned format.</summary>
    public int FormatVersion { get; }
    /// <summary>Gets bounded length.</summary>
    public int PayloadByteCount => payload.Length;
    /// <summary>Explicitly copies restricted bytes for authorized persistence.</summary>
    public byte[] CopyRestrictedPayload() => payload.ToArray();
    /// <summary>Returns no restricted content.</summary>
    public override string ToString() => nameof(ProviderSavedState);
}

/// <summary>Optional provider-owned effect-free export/import; continuation exchange support alone does not imply persistence support.</summary>
public interface IProviderContextPersistence
{
    /// <summary>Exports valid replay state or null when unsupported/uncertain. Must exclude credentials and live objects.</summary>
    ProviderSavedState? ExportContext(ProviderContextBinding binding);
    /// <summary>Validates scoped retained replay against current configuration without dispatch, storage or other external effects.</summary>
    bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state);
}

/// <summary>Exact Host-admitted historical attempts and optional unfinished-operation predecessor; not authentication.</summary>
public sealed class ProviderHistory
{
    /// <summary>Copies bounded exact accepted history origins; callers remain responsible for trusted provenance.</summary>
    public ProviderHistory(IReadOnlyList<ProviderAttempt> acceptedOrigins, ProviderAttempt? operationOrigin = null)
    {
        ArgumentNullException.ThrowIfNull(acceptedOrigins);
        var copy = acceptedOrigins.ToArray();
        if (copy.Length > 64 || copy.Any(a => a is null) || copy.Select(a => a.PhysicalAttemptId).Distinct().Count() != copy.Length
            || copy.Select(a => a.LogicalCallId).Distinct().Count() != copy.Length) throw new ArgumentException("Invalid history origins.");
        AcceptedOrigins = Array.AsReadOnly(copy); OperationOrigin = operationOrigin;
    }
    /// <summary>Gets exactly admitted accepted-turn origins.</summary>
    public IReadOnlyList<ProviderAttempt> AcceptedOrigins { get; }
    /// <summary>Gets the pending operation predecessor across rounds.</summary>
    public ProviderAttempt? OperationOrigin { get; }
    /// <summary>Returns no restricted state.</summary>
    public override string ToString() => nameof(ProviderHistory);
}

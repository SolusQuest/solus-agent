using SolusAgent.Api.Usage;

namespace SolusAgent.Runtime.Api.Providers;

/// <summary>Exact provider/model labels, without endpoint or credentials.</summary>
public sealed class ProviderScope
{
    /// <summary>Admits nonempty bounded labels without normalization.</summary>
    public ProviderScope(string provider, string model) { Provider = Label(provider); Model = Label(model); }
    /// <summary>Gets the restricted provider label.</summary>
    public string Provider { get; }
    /// <summary>Gets the restricted model label.</summary>
    public string Model { get; }
    /// <summary>Compares both labels ordinal-exactly.</summary>
    public bool Matches(ProviderScope other) => other is not null && Provider == other.Provider && Model == other.Model;
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(ProviderScope);
    internal int Bytes => ProviderBoundary.Bytes(Provider) + ProviderBoundary.Bytes(Model);
    private static string Label(string value)
    {
        ProviderBoundary.Text(value, ProviderLimits.ScopeBytes);
        ProviderBoundary.Require(value.Length != 0 && !value.Any(char.IsControl), ProviderError.InvalidInput);
        return value;
    }
}

/// <summary>A physical attempt distinct from logical retries and tool calls.</summary>
public sealed class ProviderAttempt
{
    /// <summary>Creates nonempty correlations and a positive ordinal.</summary>
    public ProviderAttempt(Guid executionId, Guid logicalCallId, Guid physicalAttemptId, int attemptNumber = 1)
    {
        ProviderBoundary.Require(executionId != Guid.Empty && logicalCallId != Guid.Empty && physicalAttemptId != Guid.Empty && attemptNumber > 0, ProviderError.InvalidAssociation);
        ExecutionId = executionId; LogicalCallId = logicalCallId; PhysicalAttemptId = physicalAttemptId; AttemptNumber = attemptNumber;
    }
    /// <summary>Gets the host execution correlation.</summary>
    public Guid ExecutionId { get; }
    /// <summary>Gets the stable logical-call correlation.</summary>
    public Guid LogicalCallId { get; }
    /// <summary>Gets the distinct physical-attempt correlation.</summary>
    public Guid PhysicalAttemptId { get; }
    /// <summary>Gets the positive logical-call ordinal.</summary>
    public int AttemptNumber { get; }
    /// <summary>Compares every correlation field.</summary>
    public bool Matches(ProviderAttempt other) => other is not null && ExecutionId == other.ExecutionId
        && LogicalCallId == other.LogicalCallId && PhysicalAttemptId == other.PhysicalAttemptId && AttemptNumber == other.AttemptNumber;
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(ProviderAttempt);
    internal UsageAttemptObservation Observe(DispatchExposure exposure, UsageObservation usage, UsageAccounting? accounting = null) =>
        new(ExecutionId, LogicalCallId, PhysicalAttemptId, AttemptNumber, exposure, usage, accounting);
}

/// <summary>Restricted opaque replay bytes scoped and anchored to their originating turn.</summary>
public sealed class ProviderContinuation
{
    private readonly byte[] bytes;
    /// <summary>Checks size before copying; never decodes or infers reasoning.</summary>
    public ProviderContinuation(ProviderScope scope, ProviderAttempt origin, ReadOnlySpan<byte> replayBytes)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Origin = origin ?? throw new ArgumentNullException(nameof(origin));
        ProviderBoundary.Require(replayBytes.Length <= ProviderLimits.ContinuationBytes, ProviderError.LimitExceeded);
        ProviderBoundary.Require(replayBytes.Length != 0, ProviderError.ContinuationMismatch);
        bytes = replayBytes.ToArray();
    }
    /// <summary>Gets the exact provider/model scope.</summary>
    public ProviderScope Scope { get; }
    /// <summary>Gets the originating turn.</summary>
    public ProviderAttempt Origin { get; }
    /// <summary>Gets replay length, without contents.</summary>
    public int ByteCount => bytes.Length;
    /// <summary>Explicitly copies restricted bytes for an authorized replay consumer.</summary>
    public byte[] CopyReplayBytes() => (byte[])bytes.Clone();
    /// <summary>Compares scope, full origin and exact bytes.</summary>
    public bool Matches(ProviderContinuation other) => other is not null && Scope.Matches(other.Scope)
        && Origin.Matches(other.Origin) && bytes.AsSpan().SequenceEqual(other.bytes);
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(ProviderContinuation);
}

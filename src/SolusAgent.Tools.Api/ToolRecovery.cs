namespace SolusAgent.Tools.Api;

/// <summary>Closed Host guarantees for one exact operation and target, independent of effect metadata.</summary>
public enum ToolRecoveryGuarantee
{
    /// <summary>Repeating the same keyed operation is safe even while the original may still execute.</summary>
    ConcurrentSafeRepeat,
    /// <summary>A query can retrieve a terminal prior outcome; an unresolved query never authorizes repetition.</summary>
    QueryOutcome,
}

/// <summary>Trusted Host-selected recovery policy. Saved copies are evidence, never live authority.</summary>
public sealed class ToolRecoveryBinding
{
    /// <summary>Declares a bounded binding identity, actual target snapshot and applicable guarantee.</summary>
    public ToolRecoveryBinding(string bindingId, string targetSnapshot, ToolRecoveryGuarantee guarantee)
    {
        BindingId = ToolBoundary.Identifier(bindingId);
        TargetSnapshot = ToolBoundary.CallId(targetSnapshot, ToolLimits.RecoveryTargetBytes);
        if (!Enum.IsDefined(guarantee)) throw new ArgumentOutOfRangeException(nameof(guarantee));
        Guarantee = guarantee;
    }
    /// <summary>Gets the Host binding identity, without supplying authority.</summary>
    public string BindingId { get; }
    /// <summary>Gets restricted original target evidence; the live producer must verify the actual target.</summary>
    public string TargetSnapshot { get; }
    /// <summary>Gets the precise Host guarantee, which must remain applicable to the original operation.</summary>
    public ToolRecoveryGuarantee Guarantee { get; }
    /// <summary>Compares all saved evidence with the current Host declaration.</summary>
    public bool Matches(ToolRecoveryBinding? other) => other is not null && BindingId == other.BindingId
        && TargetSnapshot == other.TargetSnapshot && Guarantee == other.Guarantee;
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(ToolRecoveryBinding);
}

/// <summary>Restricted stable identity supplied before the first possible effect and retained across physical attempts.</summary>
public sealed class ToolOperation
{
    /// <summary>Creates original operation evidence. A missing binding permits no recovery of an entered operation.</summary>
    public ToolOperation(Guid operationId, ToolCall call, ToolRecoveryBinding? recovery = null)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation identity is required.", nameof(operationId));
        OperationId = operationId; Call = call ?? throw new ArgumentNullException(nameof(call)); Recovery = recovery;
    }
    /// <summary>Gets the runtime-created logical operation key; it is not an invocation permission.</summary>
    public Guid OperationId { get; }
    /// <summary>Gets the exact original call and arguments.</summary>
    public ToolCall Call { get; }
    /// <summary>Gets original restricted recovery evidence.</summary>
    public ToolRecoveryBinding? Recovery { get; }
    /// <summary>Compares original identity, call, target and guarantee.</summary>
    public bool Matches(ToolOperation? other) => other is not null && OperationId == other.OperationId && Call.Matches(other.Call)
        && (Recovery is null ? other.Recovery is null : Recovery.Matches(other.Recovery));
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(ToolOperation);
}

/// <summary>Bounded producer evidence, not an authenticated result or a current invocation result.</summary>
public sealed class ToolRecoveryReceipt
{
    private ToolRecoveryReceipt(ToolOperation operation, ToolCall? recordedCall, ToolOutcome? outcome,
        ToolError? error, bool? invocationStarted, string? json)
    { Operation = operation; RecordedCall = recordedCall; Outcome = outcome; Error = error; InvocationStarted = invocationStarted; Json = json; }
    /// <summary>Reports absence of a known terminal outcome. This does not establish nonexecution.</summary>
    public static ToolRecoveryReceipt Unresolved(ToolOperation operation) => new(operation ?? throw new ArgumentNullException(nameof(operation)), null, null, null, null, null);
    /// <summary>Supplies prior facts. The consumer must authenticate association and use RestoreHistorical with the original descriptor.</summary>
    public static ToolRecoveryReceipt Found(ToolOperation operation, ToolCall recordedCall, ToolOutcome outcome,
        ToolError error, bool invocationStarted, string? json = null)
    {
        ArgumentNullException.ThrowIfNull(operation); ArgumentNullException.ThrowIfNull(recordedCall);
        if (!Enum.IsDefined(outcome) || !Enum.IsDefined(error)) throw new ToolContractException(ToolError.InvalidResult);
        if (json is not null) ToolBoundary.Text(json, ToolLimits.MaximumPayloadBytes);
        return new(operation, recordedCall, outcome, error, invocationStarted, json);
    }
    /// <summary>Gets the producer's original operation association.</summary>
    public ToolOperation Operation { get; }
    /// <summary>Gets the recorded call, or null for an unresolved query.</summary>
    public ToolCall? RecordedCall { get; }
    /// <summary>Gets the original outcome, if known.</summary>
    public ToolOutcome? Outcome { get; }
    /// <summary>Gets the original fixed error, if known.</summary>
    public ToolError? Error { get; }
    /// <summary>Gets original invocation-start evidence, independent of the query's physical entry.</summary>
    public bool? InvocationStarted { get; }
    /// <summary>Gets restricted bounded prior result JSON.</summary>
    public string? Json { get; }
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(ToolRecoveryReceipt);
}

/// <summary>Optional producer seam receiving stable identity before effects. Host binding and current capability remain mandatory.</summary>
/// <remarks>ValidateOperation is effect-free and checks current permission, actual target and applicability of the exact guarantee.
/// InvokeOperationAsync must preserve prepared-owner, exact-call, single-use and current-capability guards.
/// QueryOutcomeAsync must query only the original key/target and return terminal original facts or unresolved; it must never retry the original tool.
/// A concurrent-safe repeat guarantee must cover an original invocation that ignores cancellation or completes late.</remarks>
public interface IRecoverableFunctionTool : IFunctionTool
{
    /// <summary>Checks current capability, target and guarantee binding without creating an operation or invoking effects.</summary>
    ToolError ValidateRecoveryBinding(ToolRecoveryBinding binding, IToolCapability? capability);
    /// <summary>Revalidates current permission and exact original recovery evidence without effects.</summary>
    ToolError ValidateOperation(ToolOperation operation, IToolCapability? capability);
    /// <summary>Invokes through the normal guarded path with a fresh single-use handle and stable original operation key.</summary>
    ValueTask<ToolResult> InvokeOperationAsync(PreparedToolInvocation prepared, ToolOperation operation,
        IToolCapability? capability, CancellationToken cancellationToken = default);
    /// <summary>Performs one separately admitted and charged query; unresolved grants no repeat permission.</summary>
    ValueTask<ToolRecoveryReceipt> QueryOutcomeAsync(ToolOperation operation, IToolCapability? capability,
        CancellationToken cancellationToken = default);
}

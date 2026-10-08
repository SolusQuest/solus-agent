namespace SolusAgent.Tools.Api;

/// <summary>Descriptive effects; neither value grants permission.</summary>
public enum ToolEffect
{
    /// <summary>Describes a read-only operation, without asserting safety or granting authority.</summary>
    ReadOnly,
    /// <summary>Describes an operation that can mutate host state.</summary>
    Mutating,
}

/// <summary>Immutable normalized metadata and the complete supported input/result/capability semantics.</summary>
public sealed class ToolDescriptor
{
    /// <summary>Creates one bounded descriptor. Unsupported effects or invalid metadata reject explicitly.</summary>
    public ToolDescriptor(string name, string description, ToolSchema inputSchema, ToolSchema resultSchema,
        string capabilityId, ToolEffect effect, int maximumArgumentBytes = ToolLimits.PayloadBytes,
        int maximumResultBytes = ToolLimits.PayloadBytes)
    {
        Name = ToolBoundary.Identifier(name);
        // Admit the supplied value before normalization can allocate a second unbounded string.
        ToolBoundary.Text(description, ToolLimits.DescriptionBytes);
        Description = description.Trim();
        if (Description.Length == 0) { throw new ToolContractException(ToolError.InvalidMetadata); }
        InputSchema = inputSchema ?? throw new ArgumentNullException(nameof(inputSchema));
        ResultSchema = resultSchema ?? throw new ArgumentNullException(nameof(resultSchema));
        CapabilityId = ToolBoundary.Identifier(capabilityId);
        if (!Enum.IsDefined(effect)) { throw new ToolContractException(ToolError.UnsupportedCapability); }
        if (maximumArgumentBytes is < 1 or > ToolLimits.PayloadBytes) { throw new ArgumentOutOfRangeException(nameof(maximumArgumentBytes)); }
        if (maximumResultBytes is < 1 or > ToolLimits.PayloadBytes) { throw new ArgumentOutOfRangeException(nameof(maximumResultBytes)); }
        Effect = effect;
        MaximumArgumentBytes = maximumArgumentBytes;
        MaximumResultBytes = maximumResultBytes;
    }
    /// <summary>Canonical tool identifier.</summary>
    public string Name { get; }
    /// <summary>Bounded description with outer whitespace removed.</summary>
    public string Description { get; }
    /// <summary>The full supported argument semantics.</summary>
    public ToolSchema InputSchema { get; }
    /// <summary>The full supported success-result semantics.</summary>
    public ToolSchema ResultSchema { get; }
    /// <summary>The required explicitly supplied narrow capability semantic identifier.</summary>
    public string CapabilityId { get; }
    /// <summary>Descriptive effect metadata, never authorization.</summary>
    public ToolEffect Effect { get; }
    /// <summary>Maximum argument UTF-8 bytes.</summary>
    public int MaximumArgumentBytes { get; }
    /// <summary>Maximum result UTF-8 bytes.</summary>
    public int MaximumResultBytes { get; }
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(ToolDescriptor);
}

/// <summary>An owned, bounded call association. Arguments are validated by preparation, not construction.</summary>
public sealed class ToolCall
{
    /// <summary>Creates a call; malformed encoding and oversized values reject before retention.</summary>
    public ToolCall(string callId, string toolName, string argumentsJson)
    {
        CallId = ToolBoundary.CallId(callId);
        ToolName = ToolBoundary.Identifier(toolName);
        ArgumentsJson = ToolBoundary.Text(argumentsJson, ToolLimits.PayloadBytes);
    }
    /// <summary>Creates a call from admitted strict UTF-8 bytes, owning an immutable copy.</summary>
    public static ToolCall FromUtf8(string callId, string toolName, ReadOnlySpan<byte> argumentsUtf8) =>
        new(callId, toolName, ToolBoundary.Decode(argumentsUtf8, ToolLimits.PayloadBytes));
    /// <summary>Opaque exact, bounded call identifier; comparison never folds case or trims.</summary>
    public string CallId { get; }
    /// <summary>Canonical tool identifier.</summary>
    public string ToolName { get; }
    /// <summary>Owned raw arguments; preparation and invocation preserve these exact characters.</summary>
    public string ArgumentsJson { get; }
    /// <summary>Checks the full call association, including exact arguments.</summary>
    public bool Matches(ToolCall other) => other is not null && CallId == other.CallId
        && ToolName == other.ToolName && ArgumentsJson == other.ArgumentsJson;
    /// <summary>Returns only the type name, without dumping arguments.</summary>
    public override string ToString() => nameof(ToolCall);
}

/// <summary>A host-supplied narrow capability. Data or descriptive metadata must never manufacture one.</summary>
public interface IToolCapability
{
    /// <summary>The canonical semantic identifier; the guard checks it and the required concrete type.</summary>
    string CapabilityId { get; }
}

/// <summary>Reusable tool preparation and explicit invocation, independent of any agent runtime.</summary>
public interface IFunctionTool
{
    /// <summary>Complete metadata; consumers must reject unsupported schema/capability semantics.</summary>
    ToolDescriptor Descriptor { get; }
    /// <summary>Bounded effect-free actual argument validation; supplies no invocation authority.</summary>
    ToolPreparation Prepare(ToolCall call);
    /// <summary>Checks owner, full call, concrete Host capability and unused handle without effects or claiming invocation.</summary>
    /// <remarks>Batch consumers check every member before the first effect. Invocation must revalidate; this check grants no lasting authority.</remarks>
    ToolError ValidateInvocation(PreparedToolInvocation prepared, ToolCall expectedCall, IToolCapability? capability);
    /// <summary>Explicit invocation with original call association, host capability and cancellation.</summary>
    ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation prepared, ToolCall expectedCall,
        IToolCapability? capability, CancellationToken cancellationToken = default);
}

/// <summary>Effect-free admission result, containing either a prepared call or a fixed rejection.</summary>
public sealed class ToolPreparation
{
    internal ToolPreparation(PreparedToolInvocation? prepared, ToolError error) { Prepared = prepared; Error = error; }
    /// <summary>The bounded, owner-bound handle, or null on rejection.</summary>
    public PreparedToolInvocation? Prepared { get; }
    /// <summary>The preparation rejection, or None on acceptance.</summary>
    public ToolError Error { get; }
    /// <summary>Whether actual argument preparation succeeded.</summary>
    public bool Accepted => Prepared is not null;
}

/// <summary>A stable prepared call bound to one tool instance. It supplies no authority and dispatches at most once.</summary>
public sealed class PreparedToolInvocation
{
    private int claimed;
    internal PreparedToolInvocation(object owner, ToolCall call) { Owner = owner; Call = call; }
    internal object Owner { get; }
    internal bool IsClaimed => Volatile.Read(ref claimed) != 0;
    internal bool TryClaim() => Interlocked.CompareExchange(ref claimed, 1, 0) == 0;
    /// <summary>The original immutable call association and arguments.</summary>
    public ToolCall Call { get; }
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(PreparedToolInvocation);
}

/// <summary>An implementation's output before guarded association and result-schema acceptance.</summary>
public sealed class ToolOutput
{
    private ToolOutput(ToolCall call, string? json, ToolError error) { Call = call; Json = json; Error = error; }
    /// <summary>Creates a byte-bounded success candidate. The guard still checks association, per-tool size and schema.</summary>
    public static ToolOutput Success(ToolCall call, string json) =>
        new(call ?? throw new ArgumentNullException(nameof(call)), ToolBoundary.Text(json, ToolLimits.PayloadBytes), ToolError.None);
    /// <summary>Admits output bytes before decoding; association and result-schema acceptance still belong to the guard.</summary>
    public static ToolOutput FromUtf8(ToolCall call, ReadOnlySpan<byte> utf8Json) =>
        Success(call, ToolBoundary.Decode(utf8Json, ToolLimits.PayloadBytes));
    /// <summary>Creates a fixed implementation failure; arbitrary failure codes and None reject.</summary>
    public static ToolOutput Failure(ToolCall call, ToolError error = ToolError.InvocationFailed)
    {
        if (!Enum.IsDefined(error) || error is ToolError.None or ToolError.Cancelled) { throw new ArgumentOutOfRangeException(nameof(error)); }
        return new(call ?? throw new ArgumentNullException(nameof(call)), null, error);
    }
    /// <summary>Producer-supplied original call association, checked by the guard.</summary>
    public ToolCall Call { get; }
    /// <summary>Byte-bounded success JSON, or null on failure. Producers must also bound their own pre-output allocations.</summary>
    public string? Json { get; }
    /// <summary>Fixed implementation failure, or None for a success candidate.</summary>
    public ToolError Error { get; }
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(ToolOutput);
}

/// <summary>Guarded invocation outcomes.</summary>
public enum ToolOutcome
{
    /// <summary>Admission or result acceptance rejected the operation; inspect InvocationStarted.</summary>
    Rejected,
    /// <summary>Associated output passed the declared result boundary.</summary>
    Succeeded,
    /// <summary>Matching cancellation was observed; this does not promise rollback.</summary>
    Cancelled,
    /// <summary>Implementation failure; this does not promise rollback.</summary>
    Failed,
}

/// <summary>A bounded result associated with the requested call. No failure implies rollback or retry permission.</summary>
public sealed class ToolResult
{
    internal ToolResult(ToolCall call, ToolOutcome outcome, ToolError error, bool started, string? json = null)
    { Call = call; Outcome = outcome; Error = error; InvocationStarted = started; Json = json; }
    /// <summary>The requested call, including its exact validated argument association.</summary>
    public ToolCall Call { get; }
    /// <summary>The guarded outcome.</summary>
    public ToolOutcome Outcome { get; }
    /// <summary>Fixed rejection/failure classification.</summary>
    public ToolError Error { get; }
    /// <summary>Whether dispatch reached the implementation; true means effects may have happened, even on rejection.</summary>
    public bool InvocationStarted { get; }
    /// <summary>Accepted success JSON; absent on every non-success outcome.</summary>
    public string? Json { get; }
    /// <summary>Returns only the type name, without output or exception details.</summary>
    public override string ToString() => nameof(ToolResult);
}

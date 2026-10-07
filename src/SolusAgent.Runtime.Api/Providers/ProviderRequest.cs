using SolusAgent.Tools.Api;

namespace SolusAgent.Runtime.Api.Providers;

/// <summary>Required supported semantics, independent of actual measurement completeness.</summary>
[Flags]
public enum ProviderCapabilities
{
    /// <summary>No optional requirement.</summary>
    None = 0,
    /// <summary>The complete current tool profile and multi-call exchange.</summary>
    ToolCalls = 1,
    /// <summary>Exact restricted continuation retention and replay.</summary>
    Continuation = 2,
    /// <summary>Honest nullable observation retained across later failure.</summary>
    UsageReporting = 4,
}

/// <summary>Finite positive per-exchange bounds within the draft ceilings.</summary>
public sealed class ProviderExchangeBounds
{
    /// <summary>Creates bounds; invalid or above-ceiling values reject.</summary>
    public ProviderExchangeBounds(int maximumInputs = ProviderLimits.Inputs, int maximumTools = ProviderLimits.Tools,
        int maximumToolCalls = ProviderLimits.Tools, int maximumRequestBytes = ProviderLimits.RequestBytes,
        int maximumResponseBytes = ProviderLimits.ResponseBytes, int maximumContinuationBytes = ProviderLimits.ContinuationBytes)
    {
        MaximumInputs = Limit(maximumInputs, ProviderLimits.Inputs);
        MaximumTools = Limit(maximumTools, ProviderLimits.Tools);
        MaximumToolCalls = Limit(maximumToolCalls, ProviderLimits.Tools);
        MaximumRequestBytes = Limit(maximumRequestBytes, ProviderLimits.RequestBytes);
        MaximumResponseBytes = Limit(maximumResponseBytes, ProviderLimits.ResponseBytes);
        MaximumContinuationBytes = Limit(maximumContinuationBytes, ProviderLimits.ContinuationBytes);
    }
    /// <summary>Maximum input entries.</summary>
    public int MaximumInputs { get; }
    /// <summary>Maximum host definitions.</summary>
    public int MaximumTools { get; }
    /// <summary>Maximum calls in each represented turn.</summary>
    public int MaximumToolCalls { get; }
    /// <summary>Maximum aggregate request payload bytes.</summary>
    public int MaximumRequestBytes { get; }
    /// <summary>Maximum aggregate response payload bytes.</summary>
    public int MaximumResponseBytes { get; }
    /// <summary>Maximum bytes per continuation.</summary>
    public int MaximumContinuationBytes { get; }
    private static int Limit(int value, int ceiling)
    { if (value < 1 || value > ceiling) throw new ArgumentOutOfRangeException(nameof(value)); return value; }
}

/// <summary>Control/data classification retained independently of wire roles.</summary>
public enum ProviderInputKind
{
    /// <summary>Instructions supplied only by the authorized host.</summary>
    HostInstruction,
    /// <summary>Untrusted input data.</summary>
    InputData,
    /// <summary>Previously accepted model data.</summary>
    ModelData,
    /// <summary>Guarded tool output, always data.</summary>
    ToolResultData,
}

/// <summary>Immutable classified input; factories classify content but do not authenticate the caller.</summary>
public sealed class ProviderInput
{
    private ProviderInput(ProviderInputKind kind, string? text = null, ProviderResponse? model = null, ToolResult? toolResult = null)
    { Kind = kind; Text = text; Model = model; ToolResult = toolResult; }
    /// <summary>Classifies bounded host-authorized instructions. Never use this for response/tool data.</summary>
    public static ProviderInput Instruction(string text) => new(ProviderInputKind.HostInstruction, ProviderBoundary.Text(text, ProviderLimits.TextBytes));
    /// <summary>Classifies bounded untrusted input.</summary>
    public static ProviderInput Data(string text) => new(ProviderInputKind.InputData, ProviderBoundary.Text(text, ProviderLimits.TextBytes));
    /// <summary>Admits strict UTF-8 data before decoding.</summary>
    public static ProviderInput DataFromUtf8(ReadOnlySpan<byte> bytes) => Data(ProviderBoundary.Decode(bytes));
    /// <summary>Classifies only guard-accepted model data; rejected candidate continuation is unusable.</summary>
    public static ProviderInput FromModel(ProviderResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        ProviderBoundary.Require(response.Accepted, ProviderError.InvalidResponse);
        return new(ProviderInputKind.ModelData, model: response);
    }
    /// <summary>Classifies an actual guarded tool result as data.</summary>
    public static ProviderInput FromTool(ToolResult result) => new(ProviderInputKind.ToolResultData,
        toolResult: result ?? throw new ArgumentNullException(nameof(result)));
    /// <summary>Gets immutable classification.</summary>
    public ProviderInputKind Kind { get; }
    /// <summary>Gets restricted instruction/input text.</summary>
    public string? Text { get; }
    /// <summary>Gets restricted previously accepted model data.</summary>
    public ProviderResponse? Model { get; }
    /// <summary>Gets restricted tool-result data.</summary>
    public ToolResult? ToolResult { get; }
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(ProviderInput);
    internal int ByteCount => ProviderBoundary.Bytes(Text) + (Model?.ByteCount ?? 0)
        + (ToolResult is null ? 0 : ProviderResponse.CallBytes(ToolResult.Call) + ProviderBoundary.Bytes(ToolResult.Json));
}

/// <summary>Host-built classified exchange, with completed tool rounds and exact latest-turn replay.</summary>
public sealed class ProviderRequest
{
    /// <summary>Copies bounded input/definitions and explicitly rejects conflicting association or incomplete rounds.</summary>
    public ProviderRequest(ProviderScope scope, ProviderAttempt attempt, IReadOnlyList<ProviderInput> inputs,
        IReadOnlyList<ToolDescriptor>? tools = null, ProviderContinuation? continuation = null,
        ProviderCapabilities requiredCapabilities = ProviderCapabilities.None, ProviderExchangeBounds? bounds = null)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Attempt = attempt ?? throw new ArgumentNullException(nameof(attempt));
        Bounds = bounds ?? new();
        ValidateCapabilities(requiredCapabilities);
        var entries = ProviderBoundary.Copy(inputs, Bounds.MaximumInputs);
        var definitions = ProviderBoundary.Copy(tools ?? [], Bounds.MaximumTools);
        ProviderBoundary.Require(definitions.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() == definitions.Length, ProviderError.InvalidAssociation);
        var total = 0;
        ProviderBoundary.Charge(ref total, scope.Bytes, Bounds.MaximumRequestBytes);
        foreach (var definition in definitions)
            ProviderBoundary.Charge(ref total, ProviderBoundary.Bytes(definition.Name) + ProviderBoundary.Bytes(definition.Description)
                + ProviderBoundary.Bytes(definition.CapabilityId) + ProviderBoundary.Bytes(definition.InputSchema.NormalizedJson)
                + ProviderBoundary.Bytes(definition.ResultSchema.NormalizedJson), Bounds.MaximumRequestBytes);
        var pending = new Dictionary<string, ToolCall>(StringComparer.Ordinal);
        var physical = new HashSet<Guid> { attempt.PhysicalAttemptId };
        var logical = new HashSet<Guid> { attempt.LogicalCallId };
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        ProviderResponse? latest = null;
        var dataStarted = false;
        foreach (var entry in entries)
        {
            ProviderBoundary.Charge(ref total, entry.ByteCount, Bounds.MaximumRequestBytes);
            if (entry.Kind == ProviderInputKind.HostInstruction)
                ProviderBoundary.Require(!dataStarted, ProviderError.InvalidInput);
            else dataStarted = true;
            if (entry.Kind is ProviderInputKind.InputData or ProviderInputKind.ModelData)
                ProviderBoundary.Require(pending.Count == 0, ProviderError.InvalidAssociation);
            if (entry.Model is { } model)
            {
                ProviderBoundary.Require(scope.Matches(model.Scope) && model.Attempt.ExecutionId == attempt.ExecutionId
                    && physical.Add(model.Attempt.PhysicalAttemptId) && logical.Add(model.Attempt.LogicalCallId), ProviderError.InvalidAssociation);
                ProviderBoundary.Require(model.Calls.Count <= Bounds.MaximumToolCalls, ProviderError.LimitExceeded);
                CheckContinuation(model.Continuation);
                foreach (var call in model.Calls)
                {
                    ProviderBoundary.Require(callIds.Add(call.CallId), ProviderError.InvalidAssociation);
                    pending.Add(call.CallId, call);
                }
                latest = model;
            }
            if (entry.ToolResult is { } result)
            {
                ProviderBoundary.Require(pending.TryGetValue(result.Call.CallId, out var expected) && expected.Matches(result.Call), ProviderError.InvalidAssociation);
                pending.Remove(result.Call.CallId);
            }
        }
        ProviderBoundary.Require(pending.Count == 0, ProviderError.InvalidAssociation);
        if (continuation is null) ProviderBoundary.Require(latest?.Continuation is null, ProviderError.ContinuationMismatch);
        else
        {
            CheckContinuation(continuation);
            ProviderBoundary.Require(latest?.Continuation is { } required && required.Matches(continuation), ProviderError.ContinuationMismatch);
            ProviderBoundary.Charge(ref total, continuation.ByteCount + continuation.Scope.Bytes, Bounds.MaximumRequestBytes);
        }
        Inputs = Array.AsReadOnly(entries); Tools = Array.AsReadOnly(definitions); Continuation = continuation;
        RequiredCapabilities = requiredCapabilities | (definitions.Length != 0 || callIds.Count != 0 ? ProviderCapabilities.ToolCalls : ProviderCapabilities.None)
            | (continuation is not null ? ProviderCapabilities.Continuation : ProviderCapabilities.None);
        HistoricalCallIds = callIds;
        PayloadByteCount = total;
    }
    /// <summary>Gets exact host provider/model association.</summary>
    public ProviderScope Scope { get; }
    /// <summary>Gets current physical attempt association.</summary>
    public ProviderAttempt Attempt { get; }
    /// <summary>Gets copied classified inputs; data cannot change definitions or host instruction positions.</summary>
    public IReadOnlyList<ProviderInput> Inputs { get; }
    /// <summary>Gets unique host-supplied descriptive definitions, never invocation authority.</summary>
    public IReadOnlyList<ToolDescriptor> Tools { get; }
    /// <summary>Gets restricted exact latest-turn replay.</summary>
    public ProviderContinuation? Continuation { get; }
    /// <summary>Gets explicit requirements including those implied by represented tools/replay.</summary>
    public ProviderCapabilities RequiredCapabilities { get; }
    /// <summary>Gets finite limits.</summary>
    public ProviderExchangeBounds Bounds { get; }
    /// <summary>Gets aggregate variable payload size; not a wire byte estimate.</summary>
    public int PayloadByteCount { get; }
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(ProviderRequest);
    internal IReadOnlySet<string> HistoricalCallIds { get; }
    internal static void ValidateCapabilities(ProviderCapabilities capabilities) => ProviderBoundary.Require(
        (capabilities & ~(ProviderCapabilities.ToolCalls | ProviderCapabilities.Continuation | ProviderCapabilities.UsageReporting)) == 0, ProviderError.UnsupportedCapability);
    private void CheckContinuation(ProviderContinuation? continuation)
    {
        if (continuation is null) return;
        ProviderBoundary.Require(continuation.ByteCount <= Bounds.MaximumContinuationBytes, ProviderError.LimitExceeded);
        ProviderBoundary.Require(Scope.Matches(continuation.Scope) && continuation.Origin.ExecutionId == Attempt.ExecutionId, ProviderError.ContinuationMismatch);
    }
}

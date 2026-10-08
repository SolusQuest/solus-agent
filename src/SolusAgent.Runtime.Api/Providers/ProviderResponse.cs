using SolusAgent.Tools.Api;

namespace SolusAgent.Runtime.Api.Providers;

/// <summary>Coherent successful-turn shapes, not provider wire finish reasons.</summary>
public enum ProviderFinish
{
    /// <summary>Nonempty model data with no calls.</summary>
    Final,
    /// <summary>A call batch with optional model data.</summary>
    ToolCalls,
}

/// <summary>Restricted model candidate; only guarded acceptance makes it eligible for history.</summary>
public sealed class ProviderResponse
{
    /// <summary>Creates a bounded coherent candidate; the guard validates request-relative semantics.</summary>
    public ProviderResponse(ProviderScope scope, ProviderAttempt attempt, ProviderFinish finish, string? text,
        IReadOnlyList<ToolCall> calls, ProviderContinuation? continuation = null)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Attempt = attempt ?? throw new ArgumentNullException(nameof(attempt));
        ProviderBoundary.Require(Enum.IsDefined(finish), ProviderError.InvalidResponse);
        if (text is not null) ProviderBoundary.Text(text, ProviderLimits.TextBytes);
        var copy = ProviderBoundary.Copy(calls, ProviderLimits.Tools);
        ProviderBoundary.Require(finish == ProviderFinish.Final ? text is { Length: > 0 } && copy.Length == 0 : copy.Length != 0, ProviderError.InvalidResponse);
        ProviderBoundary.Require(copy.Select(call => call.CallId).Distinct(StringComparer.Ordinal).Count() == copy.Length, ProviderError.InvalidAssociation);
        if (continuation is not null) ProviderBoundary.Require(scope.Matches(continuation.Scope) && attempt.Matches(continuation.Origin), ProviderError.ContinuationMismatch);
        Finish = finish; Text = text; Calls = Array.AsReadOnly(copy); Continuation = continuation;
        ProviderBoundary.Require(ByteCount <= ProviderLimits.ResponseBytes, ProviderError.LimitExceeded);
    }
    private ProviderResponse(ProviderResponse source)
    {
        Scope = source.Scope; Attempt = source.Attempt; Finish = source.Finish; Text = source.Text;
        Calls = source.Calls; Continuation = source.Continuation; Accepted = true;
    }
    /// <summary>Gets restricted provider/model association.</summary>
    public ProviderScope Scope { get; }
    /// <summary>Gets full attempt association.</summary>
    public ProviderAttempt Attempt { get; }
    /// <summary>Gets turn classification.</summary>
    public ProviderFinish Finish { get; }
    /// <summary>Gets model data, never trusted instructions.</summary>
    public string? Text { get; }
    /// <summary>Gets copied call requests without invocation authority.</summary>
    public IReadOnlyList<ToolCall> Calls { get; }
    /// <summary>Gets restricted continuation required when this accepted turn is continued.</summary>
    public ProviderContinuation? Continuation { get; }
    /// <summary>Gets whether guarded request-relative acceptance occurred.</summary>
    public bool Accepted { get; }
    /// <summary>Gets bounded variable payload bytes, including scope, calls and continuation; not a wire-size estimate.</summary>
    public int PayloadByteCount => ByteCount;
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(ProviderResponse);
    internal int ByteCount => Scope.Bytes + ProviderBoundary.Bytes(Text) + Calls.Sum(CallBytes)
        + (Continuation is null ? 0 : Continuation.ByteCount + Continuation.Scope.Bytes);
    internal static int CallBytes(ToolCall call) => ProviderBoundary.Bytes(call.CallId) + ProviderBoundary.Bytes(call.ToolName) + ProviderBoundary.Bytes(call.ArgumentsJson);
    internal ProviderResponse Accept() => new(this);
    /// <summary>Revalidates this response against the actual consumer request, including its bounds and continuation requirements.</summary>
    /// <remarks>Accepted alone does not prove validation against this request. This operation neither dispatches nor makes a candidate accepted.</remarks>
    public void ValidateFor(ProviderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ProviderBoundary.Require(Scope.Matches(request.Scope) && Attempt.Matches(request.Attempt), ProviderError.InvalidAssociation);
        ProviderBoundary.Require(Calls.Count <= request.Bounds.MaximumToolCalls && ByteCount <= request.Bounds.MaximumResponseBytes, ProviderError.LimitExceeded);
        if (Continuation is not null)
        {
            ProviderBoundary.Require(Continuation.ByteCount <= request.Bounds.MaximumContinuationBytes, ProviderError.LimitExceeded);
            ProviderBoundary.Require(request.RequiredCapabilities.HasFlag(ProviderCapabilities.Continuation), ProviderError.UnsupportedCapability);
        }
        foreach (var call in Calls)
        {
            var descriptor = request.Tools.SingleOrDefault(tool => tool.Name == call.ToolName);
            ProviderBoundary.Require(descriptor is not null && !request.HistoricalCallIds.Contains(call.CallId), ProviderError.InvalidAssociation);
            ProviderBoundary.Require(descriptor!.InputSchema.Validate(call.ArgumentsJson, descriptor.MaximumArgumentBytes) == ToolError.None, ProviderError.InvalidResponse);
        }
    }
}

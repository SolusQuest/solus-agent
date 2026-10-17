using System.Security.Cryptography;
using System.Text.Json;
using SolusAgent.Tools.Api;

namespace SolusAgent.Runtime.Api.Providers;

/// <summary>The guarded classification origin, never an effect-recovery guarantee.</summary>
public enum ToolCorrectionOrigin
{
    /// <summary>Complete structural admission followed by eligible schema rejection.</summary>
    SchemaArguments,
    /// <summary>Schema-valid guarded calls followed by explicit Host argument denial.</summary>
    HostArguments,
}

/// <summary>Sanitized historical member data; construction authenticates neither source nor invocation.</summary>
public sealed class ProviderCorrectionMember
{
    /// <summary>Copies bounded facts. Rejected calls must contain the selected marker at batch validation.</summary>
    public ProviderCorrectionMember(ToolCall call, int originalArgumentBytes, string errorKey, ToolError schemaError = ToolError.None)
    {
        Call = call ?? throw new ArgumentNullException(nameof(call));
        ProviderBoundary.Require(originalArgumentBytes is >= 0 and <= ToolLimits.MaximumPayloadBytes, ProviderError.LimitExceeded);
        ProviderBoundary.Require(Enum.IsDefined(schemaError), ProviderError.InvalidInput);
        OriginalArgumentBytes = originalArgumentBytes; ErrorKey = CorrectionBoundary.Identifier(errorKey); SchemaError = schemaError;
    }
    /// <summary>Gets only sanitized rejected arguments or unchanged schema-valid sibling arguments.</summary>
    public ToolCall Call { get; }
    /// <summary>Gets numeric source size, never a content-derived identity.</summary>
    public int OriginalArgumentBytes { get; }
    /// <summary>Gets the selected static declaration key, independent of its wire code.</summary>
    public string ErrorKey { get; }
    /// <summary>Gets the original closed schema classification, or None for Host denial/valid siblings.</summary>
    public ToolError SchemaError { get; }
    /// <summary>Gets whether this member was argument-rejected; neither state implies an invocation.</summary>
    public bool Rejected => ErrorKey != ToolArgumentCorrectionPolicy.BatchNotExecuted;
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(ProviderCorrectionMember);
}

/// <summary>A Host-selected denial used only with an already schema-valid accepted source batch.</summary>
public sealed class ToolArgumentDenial
{
    /// <summary>Selects one exact original call and previously configured fixed error.</summary>
    public ToolArgumentDenial(string callId, string errorKey)
    {
        ProviderBoundary.Text(callId, ToolLimits.CallIdBytes);
        ProviderBoundary.Require(callId.Length != 0 && !callId.Any(char.IsControl), ProviderError.InvalidAssociation);
        CallId = callId; ErrorKey = CorrectionBoundary.Identifier(errorKey);
        ProviderBoundary.Require(ErrorKey != ToolArgumentCorrectionPolicy.BatchNotExecuted, ProviderError.InvalidInput);
    }
    /// <summary>Gets the exact restricted original call identifier.</summary>
    public string CallId { get; }
    /// <summary>Gets only the installed fixed declaration key.</summary>
    public string ErrorKey { get; }
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(ToolArgumentDenial);
}

/// <summary>Guarded complete-batch zero-invocation data, with no raw rejected arguments or invocation results.</summary>
public sealed class ProviderToolCorrection
{
    internal ProviderToolCorrection(ToolArgumentCorrectionPolicy policy, ToolCorrectionOrigin origin,
        IReadOnlyList<ProviderCorrectionMember> members, int originalResponseBytes, string definitionFingerprint)
    {
        Policy = policy;
        ProviderBoundary.Require(Enum.IsDefined(origin), ProviderError.InvalidResponse);
        var copy = ProviderBoundary.Copy(members, ProviderLimits.Tools);
        ProviderBoundary.Require(copy.Length != 0 && copy.Any(m => m.Rejected)
            && copy.Select(m => m.Call.CallId).Distinct(StringComparer.Ordinal).Count() == copy.Length, ProviderError.InvalidAssociation);
        ProviderBoundary.Require(originalResponseBytes is >= 0 and <= ProviderLimits.MaximumResponseBytes, ProviderError.LimitExceeded);
        ProviderBoundary.Require(definitionFingerprint is { Length: 64 }
            && definitionFingerprint.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F'), ProviderError.InvalidAssociation);
        Origin = origin; Members = Array.AsReadOnly(copy); OriginalResponseBytes = originalResponseBytes;
        DefinitionFingerprint = definitionFingerprint;
        PayloadByteCount = ProviderBoundary.Bytes(definitionFingerprint) + copy.Sum(m => ProviderResponse.CallBytes(m.Call)
            + ProviderBoundary.Bytes(m.ErrorKey) + ProviderBoundary.Bytes(policy.GetError(m.ErrorKey).Code)
            + ProviderBoundary.Bytes(policy.GetError(m.ErrorKey).Json));
    }
    internal ToolArgumentCorrectionPolicy Policy { get; }
    /// <summary>Gets the guarded origin; no origin grants effects or replay.</summary>
    public ToolCorrectionOrigin Origin { get; }
    /// <summary>Gets original member order and sanitized associations.</summary>
    public IReadOnlyList<ProviderCorrectionMember> Members { get; }
    /// <summary>Gets original raw response size for revalidation against the actual consumer's limits.</summary>
    public int OriginalResponseBytes { get; }
    /// <summary>Gets only a static descriptor/profile fingerprint, never a hash of rejected content.</summary>
    public string DefinitionFingerprint { get; }
    /// <summary>Gets actual sanitized variable payload, including every fixed feedback value.</summary>
    public int PayloadByteCount { get; }
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(ProviderToolCorrection);

    internal void ValidateFor(ProviderResponse response, ProviderRequest request)
    {
        var policy = request.ToolArgumentCorrection ?? throw new ProviderContractException(ProviderError.UnsupportedCapability);
        ProviderBoundary.Require(response.Scope.Matches(request.Scope) && response.Attempt.Matches(request.Attempt), ProviderError.InvalidAssociation);
        ProviderBoundary.Require(Members.Count <= request.Bounds.MaximumToolCalls
            && OriginalResponseBytes <= request.Bounds.MaximumResponseBytes && response.PayloadByteCount <= request.Bounds.MaximumResponseBytes,
            ProviderError.LimitExceeded);
        ProviderBoundary.Require(policy.SemanticFingerprint == Policy.SemanticFingerprint
            && DefinitionFingerprint == Definitions(request), ProviderError.InvalidAssociation);
        response.ValidateContinuationFor(request);
        var originalBytes = response.Scope.Bytes + ProviderBoundary.Bytes(response.Text)
            + (response.Continuation is null ? 0 : response.Continuation.ByteCount + response.Continuation.Scope.Bytes);
        foreach (var member in Members)
        {
            var call = member.Call;
            var descriptor = request.Tools.SingleOrDefault(t => t.Name == call.ToolName);
            ProviderBoundary.Require(descriptor is not null && !request.HistoricalCallIds.Contains(call.CallId), ProviderError.InvalidAssociation);
            ProviderBoundary.Require(member.OriginalArgumentBytes <= descriptor!.MaximumArgumentBytes, ProviderError.LimitExceeded);
            originalBytes = checked(originalBytes + ProviderBoundary.Bytes(call.CallId) + ProviderBoundary.Bytes(call.ToolName) + member.OriginalArgumentBytes);
            ProviderBoundary.Require(policy.ContainsError(member.ErrorKey), ProviderError.InvalidResponse);
            if (!member.Rejected)
            {
                ProviderBoundary.Require(member.SchemaError == ToolError.None && member.OriginalArgumentBytes == ProviderBoundary.Bytes(call.ArgumentsJson)
                    && descriptor.InputSchema.Validate(call.ArgumentsJson, descriptor.MaximumArgumentBytes) == ToolError.None, ProviderError.InvalidResponse);
            }
            else
            {
                ProviderBoundary.Require(call.ArgumentsJson == policy.MarkerArgumentsJson, ProviderError.InvalidResponse);
                ProviderBoundary.Require(Origin == ToolCorrectionOrigin.SchemaArguments
                    ? member.ErrorKey == ToolArgumentCorrectionPolicy.ArgumentsInvalid && Eligible(member.SchemaError)
                    : member.SchemaError == ToolError.None, ProviderError.InvalidResponse);
            }
        }
        ProviderBoundary.Require(originalBytes == OriginalResponseBytes, ProviderError.InvalidResponse);
    }

    internal static bool Eligible(ToolError error) => error is ToolError.InvalidArguments or ToolError.InvalidJson;
    internal static string Definitions(ProviderRequest request) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        Policy = request.ToolArgumentCorrection?.SemanticFingerprint,
        Tools = request.Tools.Select(t => new { t.Name, t.Description, t.CapabilityId, t.Effect, t.MaximumArgumentBytes,
            t.MaximumResultBytes, Input = t.InputSchema.NormalizedJson, Result = t.ResultSchema.NormalizedJson }).ToArray(),
    })));
}

/// <summary>One fixed associated non-invocation feedback value; never a ToolResult or grounding observation.</summary>
public sealed class ProviderCorrectionFeedback
{
    internal ProviderCorrectionFeedback(ProviderResponse model, int ordinal)
    {
        ProviderBoundary.Require(model is { Accepted: true, Finish: ProviderFinish.ToolCorrection, Correction: not null }
            && ordinal >= 0 && ordinal < model.Correction.Members.Count, ProviderError.InvalidResponse);
        Scope = model.Scope; Attempt = model.Attempt; Ordinal = ordinal;
        var member = model.Correction!.Members[ordinal]; Call = member.Call; ErrorKey = member.ErrorKey;
        var declaration = model.Correction.Policy.GetError(ErrorKey);
        ErrorCode = declaration.Code; Json = declaration.Json;
    }
    /// <summary>Gets restricted original scope.</summary>
    public ProviderScope Scope { get; }
    /// <summary>Gets restricted original turn association.</summary>
    public ProviderAttempt Attempt { get; }
    /// <summary>Gets the zero-based original member ordinal.</summary>
    public int Ordinal { get; }
    /// <summary>Gets only sanitized call association.</summary>
    public ToolCall Call { get; }
    /// <summary>Gets the selected static declaration key.</summary>
    public string ErrorKey { get; }
    /// <summary>Gets the selected declaration's model-visible wire code.</summary>
    public string ErrorCode { get; }
    /// <summary>Gets fixed feedback data, never successful tool output.</summary>
    public string Json { get; }
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(ProviderCorrectionFeedback);
    internal int ByteCount => Scope.Bytes + ProviderResponse.CallBytes(Call) + ProviderBoundary.Bytes(ErrorKey)
        + ProviderBoundary.Bytes(ErrorCode) + ProviderBoundary.Bytes(Json);
}

public sealed partial class ProviderResponse
{
    private ProviderResponse(ProviderScope scope, ProviderAttempt attempt, string? text, ProviderContinuation? continuation,
        ProviderToolCorrection correction)
    {
        Scope = scope; Attempt = attempt;
        if (text is not null) ProviderBoundary.Text(text, ProviderLimits.MaximumTextBytes);
        ProviderBoundary.Require(continuation is null || scope.Matches(continuation.Scope) && attempt.Matches(continuation.Origin), ProviderError.ContinuationMismatch);
        Text = text; Continuation = continuation; Correction = correction; Finish = ProviderFinish.ToolCorrection; Calls = Array.AsReadOnly(Array.Empty<ToolCall>());
        ProviderBoundary.Require(ByteCount <= ProviderLimits.MaximumResponseBytes, ProviderError.LimitExceeded);
    }
    /// <summary>Gets guarded sanitized zero-invocation data only for ToolCorrection.</summary>
    public ProviderToolCorrection? Correction { get; }

    /// <summary>Builds fixed correction from a guard-accepted schema-valid source and explicit Host preflight denials.</summary>
    /// <remarks>Callers own effect-free current Host classification and capability checks. This factory validates association/policy but authenticates neither Host denial nor historical provenance and grants no invocation.</remarks>
    public static ProviderResponse WithholdToolArguments(ProviderRequest request, ProviderResponse source, IReadOnlyList<ToolArgumentDenial> denials)
    {
        ArgumentNullException.ThrowIfNull(source);
        ProviderBoundary.Require(source is { Accepted: true, Finish: ProviderFinish.ToolCalls }, ProviderError.InvalidResponse);
        source.ValidateFor(request);
        var policy = request.ToolArgumentCorrection ?? throw new ProviderContractException(ProviderError.UnsupportedCapability);
        var copy = ProviderBoundary.Copy(denials, source.Calls.Count);
        ProviderBoundary.Require(copy.Length != 0 && copy.Select(d => d.CallId).Distinct(StringComparer.Ordinal).Count() == copy.Length
            && copy.All(d => source.Calls.Any(c => c.CallId == d.CallId) && policy.ContainsError(d.ErrorKey)), ProviderError.InvalidAssociation);
        var byId = copy.ToDictionary(d => d.CallId, StringComparer.Ordinal);
        var members = source.Calls.Select(call => byId.TryGetValue(call.CallId, out var denial)
            ? new ProviderCorrectionMember(new(call.CallId, call.ToolName, policy.MarkerArgumentsJson), ProviderBoundary.Bytes(call.ArgumentsJson), denial.ErrorKey)
            : new(call, ProviderBoundary.Bytes(call.ArgumentsJson), ToolArgumentCorrectionPolicy.BatchNotExecuted)).ToArray();
        return Correct(request, source.Scope, source.Attempt, source.Text, source.Continuation, ToolCorrectionOrigin.HostArguments,
            members, source.PayloadByteCount, ProviderToolCorrection.Definitions(request));
    }

    /// <summary>Restores complete sanitized history only after original-request validation; caller must establish trusted provenance.</summary>
    /// <remarks>No rejected raw argument is required or accepted in rejected members. Returned accepted model data supplies no current invocation authority.</remarks>
    public static ProviderResponse RestoreToolCorrection(ProviderRequest originalRequest, string? text, ProviderContinuation? continuation,
        ToolCorrectionOrigin origin, IReadOnlyList<ProviderCorrectionMember> members, int originalResponseBytes, string definitionFingerprint) =>
        Correct(originalRequest, originalRequest.Scope, originalRequest.Attempt, text, continuation, origin, members, originalResponseBytes, definitionFingerprint);

    private static ProviderResponse Correct(ProviderRequest request, ProviderScope scope, ProviderAttempt attempt, string? text,
        ProviderContinuation? continuation, ToolCorrectionOrigin origin, IReadOnlyList<ProviderCorrectionMember> members,
        int originalResponseBytes, string definitionFingerprint)
    {
        var policy = request.ToolArgumentCorrection ?? throw new ProviderContractException(ProviderError.UnsupportedCapability);
        var response = new ProviderResponse(scope, attempt, text, continuation, new(policy, origin, members, originalResponseBytes, definitionFingerprint));
        response.ValidateFor(request);
        return response.Accept();
    }

    internal ProviderResponse AdmitFor(ProviderRequest request)
    {
        if (Finish == ProviderFinish.ToolCorrection && Correction?.Origin != ToolCorrectionOrigin.SchemaArguments)
            throw new ProviderContractException(ProviderError.InvalidResponse);
        if (request.ToolArgumentCorrection is not { } policy || Finish != ProviderFinish.ToolCalls)
        { ValidateFor(request); return Accept(); }
        // Complete structural admission precedes every schema classification; short markers never launder raw limits.
        ProviderBoundary.Require(Scope.Matches(request.Scope) && Attempt.Matches(request.Attempt), ProviderError.InvalidAssociation);
        if (Text is not null) ProviderBoundary.Text(Text, request.Bounds.MaximumTextBytes);
        ProviderBoundary.Require(Calls.Count <= request.Bounds.MaximumToolCalls && ByteCount <= request.Bounds.MaximumResponseBytes, ProviderError.LimitExceeded);
        ValidateContinuationFor(request);
        var descriptors = new ToolDescriptor[Calls.Count];
        for (var i = 0; i < Calls.Count; i++)
        {
            var call = Calls[i]; var descriptor = request.Tools.SingleOrDefault(t => t.Name == call.ToolName);
            ProviderBoundary.Require(descriptor is not null && !request.HistoricalCallIds.Contains(call.CallId), ProviderError.InvalidAssociation);
            ProviderBoundary.Require(ProviderBoundary.Bytes(call.ArgumentsJson) <= descriptor!.MaximumArgumentBytes, ProviderError.LimitExceeded);
            descriptors[i] = descriptor;
        }
        var errors = Calls.Select((call, i) => descriptors[i].InputSchema.Validate(call.ArgumentsJson, descriptors[i].MaximumArgumentBytes)).ToArray();
        foreach (var error in errors.Where(e => e != ToolError.None && !ProviderToolCorrection.Eligible(e)))
            throw new ProviderContractException(error == ToolError.LimitExceeded ? ProviderError.LimitExceeded
                : error == ToolError.InvalidEncoding ? ProviderError.InvalidEncoding : ProviderError.InvalidResponse);
        if (errors.All(e => e == ToolError.None)) { ValidateFor(request); return Accept(); }
        var members = Calls.Select((call, i) => errors[i] == ToolError.None
            ? new ProviderCorrectionMember(call, ProviderBoundary.Bytes(call.ArgumentsJson), ToolArgumentCorrectionPolicy.BatchNotExecuted)
            : new(new(call.CallId, call.ToolName, policy.MarkerArgumentsJson), ProviderBoundary.Bytes(call.ArgumentsJson),
                ToolArgumentCorrectionPolicy.ArgumentsInvalid, errors[i])).ToArray();
        return Correct(request, Scope, Attempt, Text, Continuation, ToolCorrectionOrigin.SchemaArguments, members, PayloadByteCount,
            ProviderToolCorrection.Definitions(request));
    }

    internal void ValidateContinuationFor(ProviderRequest request)
    {
        if (Continuation is not { } continuation) return;
        ProviderBoundary.Require(Scope.Matches(continuation.Scope) && Attempt.Matches(continuation.Origin), ProviderError.ContinuationMismatch);
        ProviderBoundary.Require(continuation.ByteCount <= request.Bounds.MaximumContinuationBytes, ProviderError.LimitExceeded);
        ProviderBoundary.Require(request.RequiredCapabilities.HasFlag(ProviderCapabilities.Continuation), ProviderError.UnsupportedCapability);
    }
}

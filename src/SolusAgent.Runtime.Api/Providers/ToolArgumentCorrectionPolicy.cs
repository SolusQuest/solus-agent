using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SolusAgent.Tools.Api;

namespace SolusAgent.Runtime.Api.Providers;

/// <summary>A fixed Host-authored error declaration. Per-call classification can select it, never add text.</summary>
public sealed class ToolArgumentError
{
    /// <summary>Creates a closed error envelope with bounded, ordered, static identifier attributes.</summary>
    public ToolArgumentError(string code, IReadOnlyList<KeyValuePair<string, string>>? attributes = null)
    {
        Code = CorrectionBoundary.Identifier(code);
        attributes ??= [];
        var count = attributes.Count;
        ProviderBoundary.Require(count is >= 0 and <= ToolLimits.Properties - 3, ProviderError.LimitExceeded);
        var copy = new KeyValuePair<string, string>[count];
        var names = new HashSet<string>(StringComparer.Ordinal) { "status", "code", "retryable" };
        for (var i = 0; i < count; i++)
        {
            var pair = attributes[i];
            var name = CorrectionBoundary.Identifier(pair.Key); var value = CorrectionBoundary.Identifier(pair.Value);
            ProviderBoundary.Require(names.Add(name), ProviderError.InvalidInput);
            copy[i] = new(name, value);
        }
        Attributes = Array.AsReadOnly(copy);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("status", "error"); writer.WriteString("code", Code);
            foreach (var pair in copy) writer.WriteString(pair.Key, pair.Value);
            writer.WriteBoolean("retryable", true); writer.WriteEndObject(); writer.Flush();
        }
        Json = Encoding.UTF8.GetString(stream.ToArray());
        ProviderBoundary.Text(Json, ToolLimits.PayloadBytes);
    }
    /// <summary>Gets the fixed restricted error identifier.</summary>
    public string Code { get; }
    /// <summary>Gets copied static attributes, never per-call rejected values.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Attributes { get; }
    /// <summary>Gets the exact bounded fixed feedback JSON.</summary>
    public string Json { get; }
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(ToolArgumentError);
}

/// <summary>Immutable Host opt-in and fixed feedback semantics; neither data nor saved state can install it.</summary>
public sealed class ToolArgumentCorrectionPolicy
{
    /// <summary>The fixed schema-argument error identifier.</summary>
    public const string ArgumentsInvalid = "arguments_invalid";
    /// <summary>The fixed otherwise-valid withheld-member identifier.</summary>
    public const string BatchNotExecuted = "batch_not_executed";
    private readonly IReadOnlyDictionary<string, ToolArgumentError> byCode;

    /// <summary>Creates a finite declaration; marker and error attributes are Host-selected static identifiers.</summary>
    public ToolArgumentCorrectionPolicy(string identity, IReadOnlyList<ToolArgumentError>? errors = null,
        string rejectedArgumentProperty = "_solus_rejected")
    {
        Identity = CorrectionBoundary.Identifier(identity);
        MarkerProperty = CorrectionBoundary.Identifier(rejectedArgumentProperty, allowLeadingUnderscore: true);
        var custom = ProviderBoundary.Copy(errors ?? [], ProviderLimits.Tools - 2);
        var values = new Dictionary<string, ToolArgumentError>(StringComparer.Ordinal)
        {
            [ArgumentsInvalid] = new(ArgumentsInvalid), [BatchNotExecuted] = new(BatchNotExecuted),
        };
        foreach (var error in custom) ProviderBoundary.Require(values.TryAdd(error.Code, error), ProviderError.InvalidInput);
        Errors = Array.AsReadOnly(values.Values.OrderBy(error => error.Code, StringComparer.Ordinal).ToArray());
        byCode = values;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        { writer.WriteStartObject(); writer.WriteBoolean(MarkerProperty, true); writer.WriteEndObject(); writer.Flush(); }
        MarkerArgumentsJson = Encoding.UTF8.GetString(stream.ToArray());
        var declaration = JsonSerializer.SerializeToUtf8Bytes(new { Identity, MarkerArgumentsJson, Errors = Errors.Select(e => e.Json).ToArray() });
        PayloadByteCount = declaration.Length;
        ProviderBoundary.Require(PayloadByteCount <= ProviderLimits.RequestBytes, ProviderError.LimitExceeded);
        SemanticFingerprint = Convert.ToHexString(SHA256.HashData(declaration));
    }
    /// <summary>Gets the stable restricted policy identity.</summary>
    public string Identity { get; }
    /// <summary>Gets the bounded static marker property.</summary>
    public string MarkerProperty { get; }
    /// <summary>Gets the fixed argument replacement, which is not an invocation argument.</summary>
    public string MarkerArgumentsJson { get; }
    /// <summary>Gets copied fixed definitions, including the two builtin errors.</summary>
    public IReadOnlyList<ToolArgumentError> Errors { get; }
    /// <summary>Gets a fingerprint of static declarations only; never rejected argument content.</summary>
    public string SemanticFingerprint { get; }
    /// <summary>Gets bounded declaration bytes charged as Host configuration material.</summary>
    public int PayloadByteCount { get; }
    /// <summary>Checks that a fixed feedback identifier belongs to this declaration.</summary>
    public bool ContainsError(string code) => code is not null && byCode.ContainsKey(code);
    /// <summary>Gets an installed fixed error without accepting per-call attributes or text.</summary>
    public ToolArgumentError GetError(string code) => code is not null && byCode.TryGetValue(code, out var error)
        ? error : throw new ProviderContractException(ProviderError.InvalidInput);
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(ToolArgumentCorrectionPolicy);
}

/// <summary>Closed effect-free Host argument classification, independent of invocation authority.</summary>
public enum ToolArgumentDispositionKind
{
    /// <summary>No Host argument denial; normal preparation and authority checks still apply.</summary>
    Allowed,
    /// <summary>An explicitly registered fixed argument error may withhold the complete batch.</summary>
    Correctable,
    /// <summary>A fixed fatal failure, never eligible for correction.</summary>
    Fatal,
}

/// <summary>A bounded Host classification; it cannot carry raw arguments, attributes or exceptions.</summary>
public sealed class ToolArgumentDisposition
{
    private ToolArgumentDisposition(ToolArgumentDispositionKind kind, string? code, ToolError error)
    { Kind = kind; ErrorCode = code; Error = error; }
    /// <summary>Creates an allowed classification without granting an invocation.</summary>
    public static ToolArgumentDisposition Allow() => new(ToolArgumentDispositionKind.Allowed, null, ToolError.None);
    /// <summary>Selects one previously installed fixed error; the registration and policy must independently admit it.</summary>
    public static ToolArgumentDisposition Correct(string code)
    {
        code = CorrectionBoundary.Identifier(code);
        ProviderBoundary.Require(code != ToolArgumentCorrectionPolicy.BatchNotExecuted, ProviderError.InvalidInput);
        return new(ToolArgumentDispositionKind.Correctable, code, ToolError.None);
    }
    /// <summary>Creates a fixed terminal classification; it grants no rollback or retry.</summary>
    public static ToolArgumentDisposition Fail(ToolError error)
    {
        if (!Enum.IsDefined(error) || error == ToolError.None) throw new ArgumentOutOfRangeException(nameof(error));
        return new(ToolArgumentDispositionKind.Fatal, null, error);
    }
    /// <summary>Gets the closed classification.</summary>
    public ToolArgumentDispositionKind Kind { get; }
    /// <summary>Gets only the selected installed identifier.</summary>
    public string? ErrorCode { get; }
    /// <summary>Gets the fixed fatal failure, or None.</summary>
    public ToolError Error { get; }
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(ToolArgumentDisposition);
}

/// <summary>Fresh live Host classifier and its static semantic declaration; never serialize this binding.</summary>
public sealed class ToolArgumentCorrectionBinding
{
    private readonly Func<ToolCall, IToolCapability, ToolArgumentDisposition> classify;
    /// <summary>Installs bounded effect-free classification. Do not retain calls, inspect effects, invoke I/O or return input-derived errors.</summary>
    public ToolArgumentCorrectionBinding(string identity, IReadOnlyList<string> allowedErrors,
        Func<ToolCall, IToolCapability, ToolArgumentDisposition> classify)
    {
        Identity = CorrectionBoundary.Identifier(identity);
        var copy = ProviderBoundary.Copy(allowedErrors, ProviderLimits.Tools);
        foreach (var code in copy)
        {
            CorrectionBoundary.Identifier(code);
            ProviderBoundary.Require(code != ToolArgumentCorrectionPolicy.BatchNotExecuted, ProviderError.InvalidInput);
        }
        ProviderBoundary.Require(copy.Distinct(StringComparer.Ordinal).Count() == copy.Length, ProviderError.InvalidInput);
        AllowedErrors = Array.AsReadOnly(copy);
        this.classify = classify ?? throw new ArgumentNullException(nameof(classify));
    }
    /// <summary>Gets the stable restricted Host classifier identity.</summary>
    public string Identity { get; }
    /// <summary>Gets the copied static identifier allowlist; delegate identity is not a saved compatibility claim.</summary>
    public IReadOnlyList<string> AllowedErrors { get; }
    /// <summary>Classifies transient schema-valid arguments without preparation or effects; unknown codes and faults are fatal.</summary>
    public ToolArgumentDisposition Classify(ToolCall call, IToolCapability capability)
    {
        try
        {
            var value = classify(call, capability);
            if (value is null || value.Kind == ToolArgumentDispositionKind.Correctable
                && !AllowedErrors.Contains(value.ErrorCode!, StringComparer.Ordinal)) return ToolArgumentDisposition.Fail(ToolError.DomainRejected);
            return value;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        { return ToolArgumentDisposition.Fail(ToolError.DomainRejected); }
    }
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(ToolArgumentCorrectionBinding);
}

internal static class CorrectionBoundary
{
    internal static string Identifier(string value, bool allowLeadingUnderscore = false)
    {
        ProviderBoundary.Text(value, ToolLimits.IdentifierCharacters);
        ProviderBoundary.Require(value.Length != 0 && (value[0] is >= 'a' and <= 'z' || allowLeadingUnderscore && value[0] == '_')
            && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'), ProviderError.InvalidInput);
        return value;
    }
}

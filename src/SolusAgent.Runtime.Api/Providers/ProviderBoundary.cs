using System.Text;

namespace SolusAgent.Runtime.Api.Providers;

/// <summary>Conservative exchange defaults and finite construction ceilings, not wire-format guarantees.</summary>
public static class ProviderLimits
{
    /// <summary>Maximum exact provider/model label bytes.</summary>
    public const int ScopeBytes = 128;
    /// <summary>Default bytes in one text value admitted by an exchange.</summary>
    public const int TextBytes = 16384;
    /// <summary>Default opaque replay bytes and independent provider-owned saved-state ceiling.</summary>
    public const int ContinuationBytes = 8192;
    /// <summary>Default classified inputs.</summary>
    public const int Inputs = 32;
    /// <summary>Maximum definitions or calls per turn.</summary>
    public const int Tools = 16;
    /// <summary>Default retained request bytes.</summary>
    public const int RequestBytes = 131072;
    /// <summary>Default retained response bytes.</summary>
    public const int ResponseBytes = 65536;
    /// <summary>Finite text construction ceiling; actual exchanges enforce MaximumTextBytes.</summary>
    public const int MaximumTextBytes = 32 * 1024 * 1024;
    /// <summary>Maximum Host-selectable aggregate logical request bytes.</summary>
    public const int MaximumRequestBytes = 128 * 1024 * 1024;
    /// <summary>Maximum constructed and Host-admitted aggregate logical response bytes.</summary>
    public const int MaximumResponseBytes = 8 * 1024 * 1024;
    /// <summary>Maximum constructed opaque replay bytes; provider-owned saved state remains independently bounded.</summary>
    public const int MaximumContinuationBytes = 2 * 1024 * 1024;
    /// <summary>Maximum Host-selectable classified input inventory.</summary>
    public const int MaximumInputs = 2048;
    /// <summary>Maximum exact accepted origins retained for historical admission.</summary>
    public const int MaximumHistoryOrigins = 128;
}

/// <summary>Closed errors; none grants remote rollback or retry permission.</summary>
public enum ProviderError
{
    /// <summary>No error.</summary>
    None,
    /// <summary>Invalid classified input or metadata.</summary>
    InvalidInput,
    /// <summary>Conflicting exchange or tool association.</summary>
    InvalidAssociation,
    /// <summary>Unsupported required semantics.</summary>
    UnsupportedCapability,
    /// <summary>Malformed text encoding.</summary>
    InvalidEncoding,
    /// <summary>A finite byte/count bound was exceeded.</summary>
    LimitExceeded,
    /// <summary>The candidate response was not accepted.</summary>
    InvalidResponse,
    /// <summary>Missing, stale, orphan or incompatible replay.</summary>
    ContinuationMismatch,
    /// <summary>Caller cancellation before acceptance.</summary>
    Cancelled,
    /// <summary>Content-free provider failure.</summary>
    ProviderFailed,
    /// <summary>Observation already closed.</summary>
    ObservationClosed,
    /// <summary>Contradictory or overwritten observation.</summary>
    ObservationConflict,
}

/// <summary>Optional closed origin of a finite rejection; Unknown never asserts an unobserved bound.</summary>
public enum ProviderLimitKind
{
    /// <summary>Closed Unknown bound origin.</summary>
    Unknown = 0,
    /// <summary>Closed ResponseDeclaredBodyBytes bound origin.</summary>
    ResponseDeclaredBodyBytes,
    /// <summary>Closed ResponseStreamBodyBytes bound origin.</summary>
    ResponseStreamBodyBytes,
    /// <summary>Closed ResponseToolCallCount bound origin.</summary>
    ResponseToolCallCount,
    /// <summary>Closed ResponseTextBytes bound origin.</summary>
    ResponseTextBytes,
    /// <summary>Closed ResponseIdentifierBytes bound origin.</summary>
    ResponseIdentifierBytes,
    /// <summary>Closed ToolNameBytes bound origin.</summary>
    ToolNameBytes,
    /// <summary>Closed ToolArgumentBytes bound origin.</summary>
    ToolArgumentBytes,
    /// <summary>Closed ReasoningFrameBytes bound origin.</summary>
    ReasoningFrameBytes,
    /// <summary>Closed ConstructionToolCallCount bound origin.</summary>
    ConstructionToolCallCount,
    /// <summary>Closed ConstructionResponseBytes bound origin.</summary>
    ConstructionResponseBytes,
    /// <summary>Closed RequestToolCallCount bound origin.</summary>
    RequestToolCallCount,
    /// <summary>Closed RequestResponseBytes bound origin.</summary>
    RequestResponseBytes,
    /// <summary>Closed ResponseReplayFrameBytes bound origin.</summary>
    ResponseReplayFrameBytes,
}

/// <summary>Fixed rejection without the rejected value.</summary>
public sealed class ProviderContractException : ArgumentException
{
    /// <summary>Creates a content-free rejection.</summary>
    public ProviderContractException(ProviderError error) : this(error, ProviderLimitKind.Unknown) { }
    /// <summary>Creates a classified limit rejection without a rejected value or changed error category.</summary>
    public ProviderContractException(ProviderError error, ProviderLimitKind limitKind) : base("The provider boundary rejected input.")
    {
        if (!Enum.IsDefined(error) || error == ProviderError.None) throw new ArgumentOutOfRangeException(nameof(error));
        if (!Enum.IsDefined(limitKind) || (limitKind != ProviderLimitKind.Unknown && error != ProviderError.LimitExceeded))
            throw new ArgumentOutOfRangeException(nameof(limitKind));
        Error = error; LimitKind = limitKind;
    }
    /// <summary>Gets the closed classification.</summary>
    public ProviderError Error { get; }
    /// <summary>Gets an authored closed limit origin, or Unknown when it was not captured.</summary>
    public ProviderLimitKind LimitKind { get; }
}

internal static class ProviderBoundary
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static void Require(bool valid, ProviderError error)
    { if (!valid) throw new ProviderContractException(error); }
    internal static void Require(bool valid, ProviderError error, ProviderLimitKind limitKind)
    { if (!valid) throw new ProviderContractException(error, limitKind); }
    internal static string Text(string value, int limit) => Text(value, limit, ProviderLimitKind.Unknown);
    internal static string Text(string value, int limit, ProviderLimitKind limitKind)
    {
        ArgumentNullException.ThrowIfNull(value);
        Require(value.Length <= limit, ProviderError.LimitExceeded, limitKind);
        try { Require(StrictUtf8.GetByteCount(value) <= limit, ProviderError.LimitExceeded, limitKind); }
        catch (EncoderFallbackException) { throw new ProviderContractException(ProviderError.InvalidEncoding); }
        return value;
    }
    internal static string Decode(ReadOnlySpan<byte> bytes)
    {
        Require(bytes.Length <= ProviderLimits.MaximumTextBytes, ProviderError.LimitExceeded);
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new ProviderContractException(ProviderError.InvalidEncoding); }
    }
    internal static int Bytes(string? text) => text is null ? 0 : StrictUtf8.GetByteCount(text);
    internal static void Charge(ref int total, int bytes, int limit)
    {
        try { total = checked(total + bytes); }
        catch (OverflowException) { throw new ProviderContractException(ProviderError.LimitExceeded); }
        Require(total <= limit, ProviderError.LimitExceeded);
    }
    internal static T[] Copy<T>(IReadOnlyList<T> values, int limit) where T : class => Copy(values, limit, ProviderLimitKind.Unknown);
    internal static T[] Copy<T>(IReadOnlyList<T> values, int limit, ProviderLimitKind limitKind) where T : class
    {
        ArgumentNullException.ThrowIfNull(values);
        var count = values.Count;
        Require(count >= 0, ProviderError.InvalidInput);
        Require(count <= limit, ProviderError.LimitExceeded, limitKind);
        var copy = new T[count];
        for (var i = 0; i < copy.Length; i++) copy[i] = values[i] ?? throw new ProviderContractException(ProviderError.InvalidInput);
        return copy;
    }
}

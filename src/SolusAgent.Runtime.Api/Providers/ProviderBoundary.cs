using System.Text;

namespace SolusAgent.Runtime.Api.Providers;

/// <summary>Finite ceilings for this in-memory draft, not wire-format guarantees.</summary>
public static class ProviderLimits
{
    /// <summary>Maximum exact provider/model label bytes.</summary>
    public const int ScopeBytes = 128;
    /// <summary>Maximum bytes in one text value.</summary>
    public const int TextBytes = 16384;
    /// <summary>Maximum opaque replay bytes.</summary>
    public const int ContinuationBytes = 8192;
    /// <summary>Maximum classified inputs.</summary>
    public const int Inputs = 32;
    /// <summary>Maximum definitions or calls per turn.</summary>
    public const int Tools = 16;
    /// <summary>Maximum retained request bytes.</summary>
    public const int RequestBytes = 131072;
    /// <summary>Maximum retained response bytes.</summary>
    public const int ResponseBytes = 65536;
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

/// <summary>Fixed rejection without the rejected value.</summary>
public sealed class ProviderContractException : ArgumentException
{
    /// <summary>Creates a content-free rejection.</summary>
    public ProviderContractException(ProviderError error) : base("The provider boundary rejected input.")
    {
        if (!Enum.IsDefined(error) || error == ProviderError.None) throw new ArgumentOutOfRangeException(nameof(error));
        Error = error;
    }
    /// <summary>Gets the closed classification.</summary>
    public ProviderError Error { get; }
}

internal static class ProviderBoundary
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static void Require(bool valid, ProviderError error)
    { if (!valid) throw new ProviderContractException(error); }
    internal static string Text(string value, int limit)
    {
        ArgumentNullException.ThrowIfNull(value);
        Require(value.Length <= limit, ProviderError.LimitExceeded);
        try { Require(StrictUtf8.GetByteCount(value) <= limit, ProviderError.LimitExceeded); }
        catch (EncoderFallbackException) { throw new ProviderContractException(ProviderError.InvalidEncoding); }
        return value;
    }
    internal static string Decode(ReadOnlySpan<byte> bytes)
    {
        Require(bytes.Length <= ProviderLimits.TextBytes, ProviderError.LimitExceeded);
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
    internal static T[] Copy<T>(IReadOnlyList<T> values, int limit) where T : class
    {
        ArgumentNullException.ThrowIfNull(values);
        var count = values.Count;
        Require(count >= 0, ProviderError.InvalidInput);
        Require(count <= limit, ProviderError.LimitExceeded);
        var copy = new T[count];
        for (var i = 0; i < copy.Length; i++) copy[i] = values[i] ?? throw new ProviderContractException(ProviderError.InvalidInput);
        return copy;
    }
}

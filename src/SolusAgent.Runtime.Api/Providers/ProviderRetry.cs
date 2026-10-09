namespace SolusAgent.Runtime.Api.Providers;

/// <summary>Positive failure classifications; absence of metadata means unclassified or nonretryable.</summary>
public enum ProviderRetryKind
{
    /// <summary>A positively classified temporary provider or transport failure.</summary>
    Transient,
    /// <summary>A positively classified provider throttling failure.</summary>
    Throttled,
}

/// <summary>Safe advisory failure metadata, never permission to dispatch, replay effects or extend deadlines.</summary>
public sealed class ProviderRetry
{
    /// <summary>Maximum admitted advisory delay in this draft.</summary>
    public static TimeSpan MaximumDelay { get; } = TimeSpan.FromMinutes(10);
    /// <summary>Creates a positive classification with an optional finite, nonnegative advisory delay.</summary>
    public ProviderRetry(ProviderRetryKind kind, TimeSpan? retryAfter = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (retryAfter is { } delay && (delay < TimeSpan.Zero || delay > MaximumDelay))
            throw new ArgumentOutOfRangeException(nameof(retryAfter));
        Kind = kind; RetryAfter = retryAfter;
    }
    /// <summary>Gets the closed positive classification.</summary>
    public ProviderRetryKind Kind { get; }
    /// <summary>Gets a bounded advisory delay; caller and whole-run controls remain authoritative.</summary>
    public TimeSpan? RetryAfter { get; }
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(ProviderRetry);
}

/// <summary>Content-free producer failure carrier; the guard retains only its safe retry metadata.</summary>
public sealed class ProviderFailureException : Exception
{
    /// <summary>Creates a fixed failure without provider text, an inner exception or retry-by-default behavior.</summary>
    public ProviderFailureException(ProviderRetry? retry = null) : base("The provider attempt failed.") => Retry = retry;
    /// <summary>Gets optional positive evidence; null never grants retry eligibility.</summary>
    public ProviderRetry? Retry { get; }
}

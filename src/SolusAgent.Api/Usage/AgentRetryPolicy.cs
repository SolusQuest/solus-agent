namespace SolusAgent.Api.Usage;

/// <summary>Explicit finite provider retry permission, honored by supporting implementations; never permission to replay tools or Host effects.</summary>
public sealed class AgentRetryPolicy
{
    /// <summary>Maximum backoff admitted by this draft.</summary>
    public static TimeSpan DelayCeiling { get; } = TimeSpan.FromMinutes(10);

    /// <summary>Creates a bounded policy including the first attempt. Both positive transient and throttled classifications are eligible.</summary>
    /// <remarks>Absent backoff means zero; absent maximumDelay means ten minutes. An admitted hint above the Host maximum declines retry.</remarks>
    public AgentRetryPolicy(int maximumAttemptsPerLogicalCall, TimeSpan? backoff = null, TimeSpan? maximumDelay = null,
        bool honorRetryAfter = false)
    {
        if (maximumAttemptsPerLogicalCall is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maximumAttemptsPerLogicalCall));
        var delay = backoff ?? TimeSpan.Zero;
        var maximum = maximumDelay ?? DelayCeiling;
        if (maximum < TimeSpan.Zero || maximum > DelayCeiling) throw new ArgumentOutOfRangeException(nameof(maximumDelay));
        if (delay < TimeSpan.Zero || delay > maximum) throw new ArgumentOutOfRangeException(nameof(backoff));
        MaximumAttemptsPerLogicalCall = maximumAttemptsPerLogicalCall;
        Backoff = delay; MaximumDelay = maximum; HonorRetryAfter = honorRetryAfter;
    }
    /// <summary>Gets the maximum physical attempts per logical call, including the first; other run limits can stop earlier.</summary>
    public int MaximumAttemptsPerLogicalCall { get; }
    /// <summary>Gets the fixed monotonic delay between a closed failed attempt and its retry.</summary>
    public TimeSpan Backoff { get; }
    /// <summary>Gets the Host ceiling for any admitted wait.</summary>
    public TimeSpan MaximumDelay { get; }
    /// <summary>Gets whether to honor a provider hint: wait at least the hint, or decline retry if it exceeds the ceiling.</summary>
    public bool HonorRetryAfter { get; }
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(AgentRetryPolicy);
}

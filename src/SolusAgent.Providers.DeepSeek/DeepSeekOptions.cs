namespace SolusAgent.Providers.DeepSeek;

/// <summary>Host-owned configuration for the single supported non-streaming thinking/high profile.</summary>
public sealed class DeepSeekOptions
{
    /// <summary>The only supported credential destination.</summary>
    public const string OfficialEndpoint = "https://api.deepseek.com/chat/completions";
    /// <summary>The canonical request model and separately admitted response identity.</summary>
    public const string Model = "deepseek-flash";
    /// <summary>Creates finite configuration without reading environment variables or exposing the credential.</summary>
    public DeepSeekOptions(string credential, int maximumTokens, Uri? endpoint = null,
        int maximumRequestBodyBytes = 1048576, int maximumResponseBodyBytes = 524288, TimeSpan? timeout = null)
    {
        if (credential is null || credential.Length is < 1 or > 4096
            || credential.Any(c => c is < '!' or > '~')) throw new ArgumentException("Invalid provider credential.", nameof(credential));
        if (maximumTokens is < 1 or > 393216) throw new ArgumentOutOfRangeException(nameof(maximumTokens));
        endpoint ??= new(OfficialEndpoint);
        if (endpoint.OriginalString != OfficialEndpoint) throw new ArgumentException("Unsupported provider endpoint.", nameof(endpoint));
        if (maximumRequestBodyBytes is < 1 or > 1048576) throw new ArgumentOutOfRangeException(nameof(maximumRequestBodyBytes));
        if (maximumResponseBodyBytes is < 1 or > 1048576) throw new ArgumentOutOfRangeException(nameof(maximumResponseBodyBytes));
        var duration = timeout ?? TimeSpan.FromSeconds(120);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(timeout));
        Credential = credential; MaximumTokens = maximumTokens; Endpoint = endpoint;
        MaximumRequestBodyBytes = maximumRequestBodyBytes; MaximumResponseBodyBytes = maximumResponseBodyBytes; Timeout = duration;
    }
    internal string Credential { get; }
    /// <summary>Gets the fixed HTTPS endpoint, without credentials or query data.</summary>
    public Uri Endpoint { get; }
    /// <summary>Gets the positive Host-selected output allowance.</summary>
    public int MaximumTokens { get; }
    /// <summary>Gets the cap enforced while writing escaped request bytes.</summary>
    public int MaximumRequestBodyBytes { get; }
    /// <summary>Gets the cap enforced while reading unvalidated response bytes.</summary>
    public int MaximumResponseBodyBytes { get; }
    /// <summary>Gets the finite whole-attempt timeout, independent of caller cancellation.</summary>
    public TimeSpan Timeout { get; }
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(DeepSeekOptions);
}

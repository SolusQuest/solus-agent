using System.Text;

namespace SolusAgent.ConsumerProbes.ScribeHost;

/// <summary>Shared synthetic text admission for member tokens and fact text.</summary>
internal static class ScribeText
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Admits one bounded exact member token without normalization.</summary>
    internal static string Member(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is 0 or > 32 || value.Any(char.IsControl) || value != value.Trim())
        {
            throw new ArgumentException("A member must be a bounded exact token.", nameof(value));
        }

        return value;
    }

    /// <summary>Admits nonempty bounded fact text without control characters.</summary>
    internal static string Fact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            throw new ArgumentException("Fact text must be nonempty text without control characters.", nameof(value));
        }

        try
        {
            if (StrictUtf8.GetByteCount(value) > ScribeFact.MaximumTextBytes)
            {
                throw new ArgumentException("Fact text exceeds the synthetic bound.", nameof(value));
            }
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Fact text must be valid UTF-8 data.", nameof(value));
        }

        return value;
    }
}

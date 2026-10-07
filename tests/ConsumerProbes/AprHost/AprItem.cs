using System.Globalization;

namespace AprHost;

/// <summary>
/// Minimized synthetic business item for the APR-shaped Host, containing only a synthetic
/// identifier and value. It carries no pull-request identity, review finding, evidence schema,
/// platform service or credential, and its text form is untrusted candidate data.
/// </summary>
public sealed class AprItem
{
    /// <summary>Gets the fixed prefix of the synthetic item payload grammar.</summary>
    public const string PayloadPrefix = "apr-item:";

    private const int MaximumItemIdCharacters = 64;
    private const int MaximumPayloadCharacters = 192;

    /// <summary>Creates a minimized synthetic item with a bounded identifier and nonnegative value.</summary>
    /// <exception cref="ArgumentNullException">The identifier is null.</exception>
    /// <exception cref="ArgumentException">The identifier is empty, too long or contains unsupported characters.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public AprItem(string itemId, long value)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        if (itemId.Length is 0 or > MaximumItemIdCharacters
            || itemId[0] is < 'A' or > 'z' || (itemId[0] is > 'Z' and < 'a')
            || itemId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("An item identifier must use bounded letters, digits or hyphens.", nameof(itemId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ItemId = itemId;
        Value = value;
    }

    /// <summary>Gets the synthetic item identifier; it is not a repository or platform identity.</summary>
    public string ItemId { get; }

    /// <summary>Gets the synthetic measured value.</summary>
    public long Value { get; }

    /// <summary>Returns the synthetic payload text for untrusted candidate delivery.</summary>
    public string ToPayload() => string.Create(CultureInfo.InvariantCulture, $"{PayloadPrefix}{ItemId}:{Value}");

    /// <summary>
    /// Strictly parses the synthetic payload grammar. Malformed, duplicated, oversized or
    /// contradictory text yields false without echoing the supplied bytes.
    /// </summary>
    /// <param name="payload">Untrusted candidate payload text.</param>
    /// <param name="item">The parsed synthetic item, when the grammar admitted it.</param>
    public static bool TryParse(string? payload, out AprItem? item)
    {
        item = null;
        if (payload is null || payload.Length > MaximumPayloadCharacters || !payload.StartsWith(PayloadPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var body = payload[PayloadPrefix.Length..];
        var separator = body.IndexOf(':');
        if (separator <= 0 || separator == body.Length - 1 || body.IndexOf(':', separator + 1) >= 0)
        {
            return false;
        }

        var valueText = body[(separator + 1)..];
        if (valueText.Length > 1 && valueText[0] == '0')
        {
            return false;
        }

        if (!valueText.All(char.IsAsciiDigit) || !long.TryParse(valueText, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        try
        {
            item = new AprItem(body[..separator], value);
            return true;
        }
        catch (ArgumentException)
        {
            item = null;
            return false;
        }
    }

    /// <summary>Returns the type name without synthetic data.</summary>
    public override string ToString() => nameof(AprItem);
}

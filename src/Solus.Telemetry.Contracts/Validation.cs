using System.Text;

namespace Solus.Telemetry.Contracts;

internal static class Validation
{
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Text(string? value, int maximum)
    {
        if (value is null || string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new ArgumentException("A bounded nonblank label is required.");
        Unicode(value);
        return value;
    }

    internal static void Unicode(string value)
    {
        try { _ = StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException) { throw new ArgumentException("Invalid Unicode is not admitted."); }
    }

    internal static T Defined<T>(T value) where T : struct, Enum =>
        Enum.IsDefined(value) ? value : throw new ArgumentException("An undefined classification is not admitted.");

    internal static Guid Identity(Guid value) => value != Guid.Empty ? value : throw new ArgumentException("A nonempty identity is required.");
    internal static long? Count(long? value) => value is < 0 ? throw new ArgumentException("Counts cannot be negative.") : value;

    internal static IReadOnlyList<T> Copy<T>(IReadOnlyList<T>? values, int maximum)
    {
        if (values is null) return Array.AsReadOnly(Array.Empty<T>());
        if (values.Count > maximum) throw new ArgumentException("The collection exceeds its bound.");
        var copy = values.Take(maximum + 1).ToArray();
        if (copy.Length > maximum || copy.Any(value => value is null)) throw new ArgumentException("The collection is invalid.");
        return Array.AsReadOnly(copy);
    }

    internal static string Product(string value)
    {
        Text(value, 32);
        if (value[0] is < 'a' or > 'z' || value.Any(c => c is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_' and not '-')
            || value is "caller" or "eventId" or "invocationId" or "occurredAt" or "product" or "productVersion" or "schemaVersion" or "execution" or "outcome" or "usage" or "extensions")
            throw new ArgumentException("A nonreserved product namespace is required.");
        return value;
    }

    internal static void Knowledge<T>(T? value, TelemetryAccountingKnowledge knowledge) where T : struct
    {
        Defined(knowledge);
        if ((knowledge is TelemetryAccountingKnowledge.Known or TelemetryAccountingKnowledge.Provisional) && value is null
            || (knowledge is TelemetryAccountingKnowledge.Unavailable or TelemetryAccountingKnowledge.Overflow) && value is not null)
            throw new ArgumentException("Accounting value and knowledge disagree.");
    }
}

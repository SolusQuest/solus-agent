using System.Text.Json;

namespace Solus.Telemetry.Contracts;

internal static class ExtensionValidation
{
    internal static JsonElement Snapshot(JsonElement value, string product)
    {
        try
        {
            if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Extensions must be a JSON object.");
            // Bound output before recursive admission and clone only the compact admitted bytes.
            using var stream = new LimitedMemoryStream(TelemetryLimits.MaximumExtensionBytes, TelemetryDiagnostic.ExtensionsTooLarge);
            JsonSerializer.Serialize(stream, value);
            using var document = JsonDocument.Parse(stream.ToArray());
            var compact = document.RootElement;
            Validate(compact, 1);
            foreach (var item in compact.EnumerateObject())
                if ((item.Name != product && item.Name != "caller") || item.Value.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("Extensions require owned object namespaces.");
            return compact.Clone();
        }
        catch (Exception error) when (error is InvalidOperationException or JsonException)
        { throw new ArgumentException("Invalid extension JSON is not admitted."); }
    }

    internal static void Validate(JsonElement value, int depth)
    {
        try { ValidateCore(value, depth); }
        catch (Exception error) when (error is InvalidOperationException or JsonException)
        { throw new ArgumentException("Invalid extension JSON is not admitted."); }
    }

    private static void ValidateCore(JsonElement value, int depth)
    {
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array && depth > TelemetryLimits.MaximumExtensionDepth)
            throw new ArgumentException("Extension container depth exceeds its bound.");
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in value.EnumerateObject())
                {
                    Validation.Unicode(item.Name);
                    if (!names.Add(item.Name) || names.Count > TelemetryLimits.MaximumObjectProperties)
                        throw new ArgumentException("Extension member names must be unique and bounded.");
                    Validate(item.Value, depth + 1);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) Validate(item, depth + 1);
                break;
            case JsonValueKind.String:
                Validation.Unicode(value.GetString()!);
                break;
            case JsonValueKind.Number:
                if (!value.TryGetDouble(out var number) || !double.IsFinite(number)
                    || Math.Truncate(number) == number && Math.Abs(number) > 9007199254740991d)
                    throw new ArgumentException("Extension number is not interoperable.");
                // Inspection never replaces the admitted lexeme. Reject underflow of a nonzero significand.
                var significand = value.GetRawText().Split('e', 'E')[0];
                if (number == 0 && significand.Any(c => c is >= '1' and <= '9'))
                    throw new ArgumentException("Extension number underflows binary64.");
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                break;
            default:
                throw new ArgumentException("Only JSON extension values are admitted.");
        }
    }

    internal static JsonElement AddCaller(JsonElement existing, JsonElement attributes, string product)
    {
        if (attributes.ValueKind != JsonValueKind.Object) throw new ArgumentException("Caller attributes must be a plain JSON object.");
        using var stream = new LimitedMemoryStream(TelemetryLimits.MaximumExtensionBytes, TelemetryDiagnostic.ExtensionsTooLarge);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var item in existing.EnumerateObject())
                if (item.Name != "caller") item.WriteTo(writer);
            writer.WritePropertyName("caller"); writer.WriteStartObject();
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (existing.TryGetProperty("caller", out var caller))
                foreach (var item in caller.EnumerateObject()) { names.Add(item.Name); item.WriteTo(writer); }
            foreach (var item in attributes.EnumerateObject())
            {
                if (!names.Add(item.Name)) throw new ArgumentException("Caller attributes cannot overwrite an admitted member.");
                item.WriteTo(writer);
            }
            writer.WriteEndObject(); writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return Snapshot(document.RootElement, product);
    }
}

internal sealed class AdmissionException(TelemetryDiagnostic diagnostic) : ArgumentException("Telemetry exceeds its admitted bound.")
{
    internal TelemetryDiagnostic Diagnostic { get; } = diagnostic;
}

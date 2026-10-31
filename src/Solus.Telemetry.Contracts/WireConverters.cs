using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Solus.Telemetry.Contracts;

internal sealed class Int64StringConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("An Int64 decimal string is required.");
        var text = reader.GetString();
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            || text != value.ToString(CultureInfo.InvariantCulture)) throw new JsonException("Noncanonical Int64.");
        return value;
    }
    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}

internal sealed class DecimalStringConverter : JsonConverter<decimal>
{
    internal static string Format(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("An exact decimal string is required.");
        var text = reader.GetString();
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            || text != Format(value)) throw new JsonException("Noncanonical decimal.");
        return value;
    }
    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) => writer.WriteStringValue(Format(value));
}

internal sealed class IdentityConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Guid.TryParseExact(reader.GetString(), "D", out var id) && id != Guid.Empty
            ? id : throw new JsonException("A nonempty UUID is required.");
    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString("D"));
}

internal sealed class UtcConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("An explicit UTC timestamp is required.");
        var text = reader.GetString()!;
        // DateTimeOffset otherwise gives offset-free input the host's local timezone.
        var utcSuffixLength = text.EndsWith("Z", StringComparison.Ordinal) ? 1 : text.EndsWith("+00:00", StringComparison.Ordinal) ? 6 : 0;
        var fraction = text.IndexOf('.');
        // DateTimeOffset has seven fractional digits; do not silently truncate a more precise instant.
        if (utcSuffixLength == 0 || fraction >= 0 && text.Length - utcSuffixLength - fraction - 1 > 7
            || !reader.TryGetDateTimeOffset(out var value) || value.Offset != TimeSpan.Zero)
            throw new JsonException("An explicit UTC timestamp is required.");
        return value;
    }
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString("O", CultureInfo.InvariantCulture));
}

internal sealed class ClosedEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly IReadOnlyDictionary<string, T> Values = Enum.GetValues<T>().ToDictionary(
        value => JsonNamingPolicy.CamelCase.ConvertName(value.ToString()), StringComparer.Ordinal);
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Values.TryGetValue(reader.GetString()!, out var value)
            ? value : throw new JsonException("An exact classification name is required.");
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        Validation.Defined(value);
        writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
    }
}

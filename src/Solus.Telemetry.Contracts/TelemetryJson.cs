using System.Text.Json;
using System.Text.Json.Serialization;

namespace Solus.Telemetry.Contracts;

/// <summary>Strict bounded v1 JSON admission; failures return a fixed diagnostic and no partial record.</summary>
public static class TelemetryJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>Serializes the complete immutable record, returning no JSON on any admitted validation failure.</summary>
    public static bool TrySerialize(TelemetryRecord? record, out string? json, out TelemetryDiagnostic diagnostic)
    {
        json = null; diagnostic = TelemetryDiagnostic.InvalidInput;
        if (record is null) return false;
        try
        {
            using var stream = new LimitedMemoryStream(TelemetryLimits.MaximumRecordBytes, TelemetryDiagnostic.RecordTooLarge);
            JsonSerializer.Serialize(stream, record, Options);
            json = Validation.StrictUtf8.GetString(stream.ToArray());
            diagnostic = TelemetryDiagnostic.None; return true;
        }
        catch (AdmissionException error) { diagnostic = error.Diagnostic; }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException) { }
        return false;
    }

    /// <summary>Reads exactly one bounded v1 record, rejecting duplicates, unknown fixed fields and unsupported versions.</summary>
    public static bool TryDeserialize(string? json, out TelemetryRecord? record, out TelemetryDiagnostic diagnostic)
    {
        record = null; diagnostic = TelemetryDiagnostic.InvalidInput;
        if (json is null) return false;
        try
        {
            if (json.Length > TelemetryLimits.MaximumRecordBytes || Validation.StrictUtf8.GetByteCount(json) > TelemetryLimits.MaximumRecordBytes)
                throw new AdmissionException(TelemetryDiagnostic.RecordTooLarge);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            CheckNames(root);
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number)) return false;
            if (number != 1) { diagnostic = TelemetryDiagnostic.UnsupportedSchema; return false; }
            var candidate = root.Deserialize<TelemetryRecord>(Options);
            if (candidate is null || !TrySerialize(candidate, out _, out diagnostic)) return false;
            record = candidate; diagnostic = TelemetryDiagnostic.None; return true;
        }
        catch (AdmissionException error) { diagnostic = error.Diagnostic; }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException) { }
        return false;
    }

    private static void CheckNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in element.EnumerateObject())
            {
                Validation.Unicode(item.Name);
                if (!names.Add(item.Name)) throw new JsonException("Duplicate member name.");
                CheckNames(item.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckNames(item);
        else if (element.ValueKind == JsonValueKind.String) Validation.Unicode(element.GetString()!);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            MaxDepth = 64,
        };
        options.Converters.Add(new Int64StringConverter());
        options.Converters.Add(new DecimalStringConverter());
        options.Converters.Add(new IdentityConverter());
        options.Converters.Add(new UtcConverter());
        options.Converters.Add(new ClosedEnumConverter<TelemetryInventoryCoverage>());
        options.Converters.Add(new ClosedEnumConverter<TelemetryMeasurementCoverage>());
        options.Converters.Add(new ClosedEnumConverter<TelemetryOutcomeKind>());
        options.Converters.Add(new ClosedEnumConverter<TelemetryCounterKind>());
        options.Converters.Add(new ClosedEnumConverter<TelemetryCounterRelationship>());
        options.Converters.Add(new ClosedEnumConverter<TelemetryAccountingSource>());
        options.Converters.Add(new ClosedEnumConverter<TelemetryAccountingKind>());
        options.Converters.Add(new ClosedEnumConverter<TelemetryAccountingKnowledge>());
        options.Converters.Add(new ClosedEnumConverter<TelemetrySettlement>());
        return options;
    }

}

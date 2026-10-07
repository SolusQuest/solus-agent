using System.Text;
using System.Text.Json;

namespace SolusAgent.Tools.Api;

/// <summary>
/// A bounded closed flat object profile with string, boolean and lexical Int64 properties.
/// Unsupported keywords, duplicates, references, nesting, arrays and unions reject explicitly.
/// This profile is intentionally smaller than general JSON Schema.
/// </summary>
public sealed class ToolSchema
{
    private readonly SortedDictionary<string, string> properties;
    private readonly HashSet<string> required;

    private ToolSchema(SortedDictionary<string, string> properties, HashSet<string> required)
    {
        this.properties = properties;
        this.required = required;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");
            foreach (var property in properties)
            {
                writer.WriteStartObject(property.Key);
                writer.WriteString("type", property.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteStartArray("required");
            foreach (var name in required.Order(StringComparer.Ordinal)) { writer.WriteStringValue(name); }
            writer.WriteEndArray();
            writer.WriteBoolean("additionalProperties", false);
            writer.WriteEndObject();
        }
        NormalizedJson = ToolBoundary.Text(Encoding.UTF8.GetString(stream.ToArray()), ToolLimits.SchemaBytes);
    }

    /// <summary>The complete, deterministic schema representation; no supported keyword is discarded.</summary>
    public string NormalizedJson { get; }
    /// <summary>The supported profile identifier, which adapters must explicitly support.</summary>
    public string Profile => "closed_scalar_object";

    /// <summary>Admits source bytes before strict decoding and parsing. Throws a classified rejection.</summary>
    public static ToolSchema FromUtf8(ReadOnlySpan<byte> utf8Json) => Parse(ToolBoundary.Decode(utf8Json, ToolLimits.SchemaBytes));

    /// <summary>Parses only the documented supported semantics; unknown semantics are rejected.</summary>
    public static ToolSchema Parse(string json)
    {
        using var document = ToolBoundary.Json(json, ToolLimits.SchemaBytes);
        var root = document.RootElement;
        Require(root.ValueKind == JsonValueKind.Object);
        var keys = root.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Require(keys.SetEquals(["type", "properties", "required", "additionalProperties"]));
        Require(root.GetProperty("type").ValueKind == JsonValueKind.String && root.GetProperty("type").GetString() == "object");
        Require(root.GetProperty("additionalProperties").ValueKind == JsonValueKind.False);
        var definitions = root.GetProperty("properties");
        Require(definitions.ValueKind == JsonValueKind.Object);
        var properties = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in definitions.EnumerateObject())
        {
            Require(properties.Count < ToolLimits.Properties);
            ToolBoundary.Identifier(property.Name);
            Require(property.Value.ValueKind == JsonValueKind.Object);
            var members = property.Value.EnumerateObject().ToArray();
            Require(members.Length == 1 && members[0].Name == "type" && members[0].Value.ValueKind == JsonValueKind.String);
            var type = members[0].Value.GetString()!;
            Require(type is "string" or "boolean" or "integer");
            properties.Add(property.Name, type);
        }
        var requiredElement = root.GetProperty("required");
        Require(requiredElement.ValueKind == JsonValueKind.Array);
        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in requiredElement.EnumerateArray())
        {
            Require(member.ValueKind == JsonValueKind.String);
            var name = member.GetString()!;
            Require(required.Count < ToolLimits.Properties && properties.ContainsKey(name) && required.Add(name));
        }
        return new(properties, required);
    }

    /// <summary>Validates actual JSON against every supported constraint; returns a fixed failure or None.</summary>
    public ToolError Validate(string json, int maximumBytes = ToolLimits.PayloadBytes)
    {
        if (maximumBytes is < 1 or > ToolLimits.PayloadBytes) { throw new ArgumentOutOfRangeException(nameof(maximumBytes)); }
        try
        {
            using var document = ToolBoundary.Json(json, maximumBytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { return ToolError.InvalidArguments; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in root.EnumerateObject())
            {
                if (!properties.TryGetValue(member.Name, out var type) || !seen.Add(member.Name)) { return ToolError.InvalidArguments; }
                var value = member.Value;
                var valid = type switch
                {
                    "string" => value.ValueKind == JsonValueKind.String,
                    "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "integer" => value.ValueKind == JsonValueKind.Number
                        && !value.GetRawText().Any(c => c is '.' or 'e' or 'E') && value.TryGetInt64(out _),
                    _ => false,
                };
                if (!valid) { return ToolError.InvalidArguments; }
            }
            return required.IsSubsetOf(seen) ? ToolError.None : ToolError.InvalidArguments;
        }
        catch (ToolContractException exception) { return exception.Error; }
    }

    /// <summary>Returns only the type name, rather than schema contents.</summary>
    public override string ToString() => nameof(ToolSchema);

    private static void Require(bool condition)
    {
        if (!condition) { throw new ToolContractException(ToolError.UnsupportedSchema); }
    }
}

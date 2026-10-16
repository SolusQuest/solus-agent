using System.Text.Json;

namespace SolusAgent.Tools.Api;

public sealed partial class ToolSchema
{
    private sealed record Node(string Type, bool Nullable, SortedDictionary<string, Node>? Properties = null,
        string[]? Required = null, Node? Items = null, long? Minimum = null, long? Maximum = null,
        int MinItems = 0, int MaxItems = ToolLimits.ArrayElements, string[]? Values = null, bool EnumNull = false)
    {
        internal static Node Read(JsonElement source, int depth, ref int nodes, ref int declarations)
        {
            Require(source.ValueKind == JsonValueKind.Object && ++nodes <= ToolLimits.SchemaNodes);
            Require(source.TryGetProperty("type", out var typeElement));
            var nullable = false;
            string? type = null;
            if (typeElement.ValueKind == JsonValueKind.String) type = typeElement.GetString();
            else if (typeElement.ValueKind == JsonValueKind.Array)
            {
                var types = typeElement.EnumerateArray().ToArray();
                Require(types.Length == 2 && types.All(t => t.ValueKind == JsonValueKind.String));
                var names = types.Select(t => t.GetString()!).ToArray();
                Require(names.Distinct(StringComparer.Ordinal).Count() == 2 && names.Contains("null", StringComparer.Ordinal));
                type = names.Single(t => t != "null");
                nullable = true;
            }
            Require(type is "object" or "array" or "string" or "integer" or "boolean");
            var keys = source.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            string[] allowed = type switch
            {
                "object" => ["type", "properties", "required", "additionalProperties"],
                "array" => ["type", "items", "minItems", "maxItems"],
                "integer" => ["type", "minimum", "maximum"],
                "string" => ["type", "enum"],
                _ => ["type"],
            };
            Require(keys.IsSubsetOf(allowed));
            if (type is "object" or "array") Require(++depth <= ToolLimits.SchemaDepth);
            if (type == "object")
            {
                Require(source.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False
                    && source.TryGetProperty("properties", out var definitions) && definitions.ValueKind == JsonValueKind.Object);
                var properties = new SortedDictionary<string, Node>(StringComparer.Ordinal);
                foreach (var property in source.GetProperty("properties").EnumerateObject())
                {
                    Require(properties.Count < ToolLimits.Properties && ++declarations <= ToolLimits.SchemaProperties);
                    ToolBoundary.Identifier(property.Name);
                    properties.Add(property.Name, Read(property.Value, depth, ref nodes, ref declarations));
                }
                var required = new HashSet<string>(StringComparer.Ordinal);
                if (source.TryGetProperty("required", out var requiredElement))
                {
                    Require(requiredElement.ValueKind == JsonValueKind.Array);
                    foreach (var entry in requiredElement.EnumerateArray())
                    {
                        Require(entry.ValueKind == JsonValueKind.String && properties.ContainsKey(entry.GetString()!) && required.Add(entry.GetString()!));
                    }
                }
                return new(type, nullable, properties, required.Order(StringComparer.Ordinal).ToArray());
            }
            if (type == "array")
            {
                Require(source.TryGetProperty("items", out var items));
                var minimum = Count(source, "minItems", 0);
                var maximum = Count(source, "maxItems", ToolLimits.ArrayElements);
                Require(minimum <= maximum);
                return new(type, nullable, Items: Read(items, depth, ref nodes, ref declarations), MinItems: minimum, MaxItems: maximum);
            }
            if (type == "integer")
            {
                var minimum = Integer(source, "minimum");
                var maximum = Integer(source, "maximum");
                Require(minimum is null || maximum is null || minimum <= maximum);
                return new(type, nullable, Minimum: minimum, Maximum: maximum);
            }
            if (source.TryGetProperty("enum", out var values))
            {
                Require(values.ValueKind == JsonValueKind.Array && values.GetArrayLength() is > 0 and <= ToolLimits.EnumValues);
                var strings = new SortedSet<string>(StringComparer.Ordinal);
                var enumNull = false;
                foreach (var value in values.EnumerateArray())
                {
                    if (value.ValueKind == JsonValueKind.Null)
                    {
                        Require(nullable && !enumNull);
                        enumNull = true;
                    }
                    else Require(value.ValueKind == JsonValueKind.String && strings.Add(value.GetString()!));
                }
                return new(type!, nullable, Values: strings.ToArray(), EnumNull: enumNull);
            }
            return new(type!, nullable);
        }

        private static long? Integer(JsonElement source, string name)
        {
            if (!source.TryGetProperty(name, out var element)) return null;
            Require(element.ValueKind == JsonValueKind.Number && ToolInteger.TryParse(element.GetRawText(), out _));
            ToolInteger.TryParse(element.GetRawText(), out var value);
            return value;
        }

        private static int Count(JsonElement source, string name, int fallback)
        {
            var value = Integer(source, name) ?? fallback;
            Require(value is >= 0 and <= ToolLimits.ArrayElements);
            return (int)value;
        }

        internal bool Accepts(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Null) return Nullable && (Values is null || EnumNull);
            switch (Type)
            {
                case "object":
                    if (value.ValueKind != JsonValueKind.Object) return false;
                    foreach (var property in value.EnumerateObject())
                        if (!Properties!.TryGetValue(property.Name, out var schema) || !schema.Accepts(property.Value)) return false;
                    return Required!.All(name => value.TryGetProperty(name, out _));
                case "array":
                    if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < MinItems || value.GetArrayLength() > MaxItems) return false;
                    return value.EnumerateArray().All(Items!.Accepts);
                case "string":
                    return value.ValueKind == JsonValueKind.String
                        && (Values is null || Array.BinarySearch(Values, value.GetString(), StringComparer.Ordinal) >= 0);
                case "boolean":
                    return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
                case "integer":
                    return value.ValueKind == JsonValueKind.Number && ToolInteger.TryParse(value.GetRawText(), out var number)
                        && (Minimum is null || number >= Minimum) && (Maximum is null || number <= Maximum);
                default:
                    return false;
            }
        }

        internal void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            if (Nullable)
            {
                writer.WriteStartArray("type"); writer.WriteStringValue(Type); writer.WriteStringValue("null"); writer.WriteEndArray();
            }
            else writer.WriteString("type", Type);
            if (Type == "object")
            {
                writer.WriteStartObject("properties");
                foreach (var property in Properties!) { writer.WritePropertyName(property.Key); property.Value.Write(writer); }
                writer.WriteEndObject();
                writer.WriteStartArray("required");
                foreach (var name in Required!) writer.WriteStringValue(name);
                writer.WriteEndArray();
                writer.WriteBoolean("additionalProperties", false);
            }
            if (Type == "array")
            {
                writer.WritePropertyName("items"); Items!.Write(writer);
                writer.WriteNumber("minItems", MinItems); writer.WriteNumber("maxItems", MaxItems);
            }
            if (Minimum is { } minimum) writer.WriteNumber("minimum", minimum);
            if (Maximum is { } maximum) writer.WriteNumber("maximum", maximum);
            if (Values is not null)
            {
                writer.WriteStartArray("enum");
                foreach (var value in Values) writer.WriteStringValue(value);
                if (EnumNull) writer.WriteNullValue();
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
    }
}

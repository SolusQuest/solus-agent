using System.Text;
using System.Text.Json;

namespace SolusAgent.Tools.Api;

/// <summary>Finite limits for the current function-tool draft.</summary>
public static class ToolLimits
{
    /// <summary>Maximum canonical tool, capability, or property identifier characters.</summary>
    public const int IdentifierCharacters = 64;
    /// <summary>Maximum opaque call identifier UTF-8 bytes.</summary>
    public const int CallIdBytes = 1024;
    /// <summary>Independent opaque recovery target snapshot ceiling.</summary>
    public const int RecoveryTargetBytes = 128;
    /// <summary>Maximum normalized description UTF-8 bytes.</summary>
    public const int DescriptionBytes = 1024;
    /// <summary>Maximum source schema UTF-8 bytes.</summary>
    public const int SchemaBytes = 8192;
    /// <summary>Default descriptor and schema-validation argument/result UTF-8 bytes.</summary>
    public const int PayloadBytes = 16384;
    /// <summary>Finite construction ceiling; actual preparation and results obey the Host descriptor's smaller limits.</summary>
    public const int MaximumPayloadBytes = 8 * 1024 * 1024;
    /// <summary>Maximum declared properties in each closed object.</summary>
    public const int Properties = 32;
    /// <summary>Maximum aggregate property declarations in a schema.</summary>
    public const int SchemaProperties = 128;
    /// <summary>Maximum semantic schema nodes, including the root and array item schemas.</summary>
    public const int SchemaNodes = 256;
    /// <summary>Maximum semantic object/array container nesting in a schema.</summary>
    public const int SchemaDepth = 6;
    /// <summary>Maximum JSON container depth for schema syntax.</summary>
    public const int SchemaJsonDepth = 18;
    /// <summary>Maximum decoded member occurrences in schema syntax.</summary>
    public const int SchemaJsonProperties = 1024;
    /// <summary>Maximum aggregate array elements in schema syntax.</summary>
    public const int SchemaJsonElements = 256;
    /// <summary>Maximum distinct values in a string enum.</summary>
    public const int EnumValues = 32;
    /// <summary>Maximum payload JSON container depth before semantic validation.</summary>
    public const int JsonDepth = 6;
    /// <summary>Maximum aggregate decoded member occurrences in a payload.</summary>
    public const int JsonProperties = 2048;
    /// <summary>Maximum elements in each payload array.</summary>
    public const int ArrayElements = 1000;
    /// <summary>Maximum aggregate payload array elements, including container and null elements.</summary>
    public const int TotalArrayElements = 1024;
}

/// <summary>Fixed rejection or failure classifications; these never carry exception text.</summary>
public enum ToolError
{
    /// <summary>No failure.</summary>
    None,
    /// <summary>An identity or normalized metadata value is invalid.</summary>
    InvalidMetadata,
    /// <summary>A byte or count limit was exceeded.</summary>
    LimitExceeded,
    /// <summary>Invalid UTF-8, UTF-16, or escaped Unicode.</summary>
    InvalidEncoding,
    /// <summary>Malformed, duplicate-member, or over-depth JSON.</summary>
    InvalidJson,
    /// <summary>The schema contains unsupported or invalid semantics.</summary>
    UnsupportedSchema,
    /// <summary>Actual arguments violate the selected schema.</summary>
    InvalidArguments,
    /// <summary>The tool's effect or supplied capability semantics are unsupported.</summary>
    UnsupportedCapability,
    /// <summary>The tool or expected original call does not match.</summary>
    CallMismatch,
    /// <summary>The preparation belongs to a different tool instance.</summary>
    PreparedMismatch,
    /// <summary>This preparation has already been dispatched.</summary>
    AlreadyInvoked,
    /// <summary>Output violates the selected result boundary.</summary>
    InvalidResult,
    /// <summary>Output belongs to a different call.</summary>
    ResultMismatch,
    /// <summary>Invocation observed matching-token cancellation.</summary>
    Cancelled,
    /// <summary>The implementation failed.</summary>
    InvocationFailed,
    /// <summary>Effect-free implementation-local validation rejected the arguments.</summary>
    DomainRejected,
}

/// <summary>An explicit constructor/factory rejection, including its fixed classification.</summary>
public sealed class ToolContractException : ArgumentException
{
    /// <summary>Creates a rejection without copying the rejected value into the message.</summary>
    public ToolContractException(ToolError error) : base("The function-tool boundary rejected input.") => Error = error;
    /// <summary>The rejected boundary.</summary>
    public ToolError Error { get; }
}

internal static class ToolBoundary
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Identifier(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is < 1 or > ToolLimits.IdentifierCharacters
            || value[0] is < 'a' or > 'z'
            || value.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
        {
            throw new ToolContractException(ToolError.InvalidMetadata);
        }
        return value;
    }

    internal static string CallId(string value, int maximumBytes = ToolLimits.CallIdBytes)
    {
        Text(value, maximumBytes);
        if (value.Length == 0 || value.Any(char.IsControl))
        {
            throw new ToolContractException(ToolError.InvalidMetadata);
        }
        return value;
    }

    internal static string Text(string value, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            // Count before allocating encoded bytes or parsing JSON.
            if (StrictUtf8.GetByteCount(value) > maximumBytes)
            {
                throw new ToolContractException(ToolError.LimitExceeded);
            }
        }
        catch (EncoderFallbackException)
        {
            throw new ToolContractException(ToolError.InvalidEncoding);
        }
        return value;
    }

    internal static string Decode(ReadOnlySpan<byte> bytes, int maximumBytes)
    {
        if (bytes.Length > maximumBytes)
        {
            throw new ToolContractException(ToolError.LimitExceeded);
        }
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new ToolContractException(ToolError.InvalidEncoding); }
    }

    internal static JsonDocument Json(string value, int maximumBytes, bool schema = false)
    {
        Text(value, maximumBytes);
        try
        {
            var bytes = StrictUtf8.GetBytes(value);
            var depth = schema ? ToolLimits.SchemaJsonDepth : ToolLimits.JsonDepth;
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = depth });
            var objects = new Stack<HashSet<string>>();
            var containers = new Stack<(bool Array, int Count)>();
            var totalProperties = 0;
            var totalElements = 0;
            while (reader.Read())
            {
                if (reader.TokenType is not (JsonTokenType.PropertyName or JsonTokenType.EndObject or JsonTokenType.EndArray)
                    && containers.TryPeek(out var parent) && parent.Array)
                {
                    containers.Pop();
                    containers.Push((true, parent.Count + 1));
                    if (++totalElements > (schema ? ToolLimits.SchemaJsonElements : ToolLimits.TotalArrayElements)
                        || parent.Count + 1 > (schema ? ToolLimits.SchemaJsonElements : ToolLimits.ArrayElements))
                        throw new ToolContractException(ToolError.LimitExceeded);
                }
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    containers.Push((reader.TokenType == JsonTokenType.StartArray, 0));
                if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) containers.Pop();
                if (reader.TokenType == JsonTokenType.StartObject) { objects.Push(new(StringComparer.Ordinal)); }
                if (reader.TokenType == JsonTokenType.EndObject) { objects.Pop(); }
                if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName)
                {
                    CheckEscapedUnicode(reader.ValueSpan);
                    Text(reader.GetString()!, maximumBytes);
                }
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (!objects.Peek().Add(reader.GetString()!)) throw new ToolContractException(ToolError.InvalidJson);
                    if (++totalProperties > (schema ? ToolLimits.SchemaJsonProperties : ToolLimits.JsonProperties))
                        throw new ToolContractException(ToolError.LimitExceeded);
                }
            }
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = depth });
        }
        catch (JsonException) { throw new ToolContractException(ToolError.InvalidJson); }
        catch (InvalidOperationException) { throw new ToolContractException(ToolError.InvalidJson); }
    }

    private static void CheckEscapedUnicode(ReadOnlySpan<byte> token)
    {
        for (var i = 0; i < token.Length; i++)
        {
            if (token[i] != (byte)'\\') { continue; }
            i++;
            if (token[i] != (byte)'u') { continue; }
            var code = Hex(token.Slice(i + 1, 4));
            i += 4;
            if (code is >= 0xDC00 and <= 0xDFFF) { throw new ToolContractException(ToolError.InvalidEncoding); }
            if (code is >= 0xD800 and <= 0xDBFF)
            {
                if (i + 6 >= token.Length || token[i + 1] != (byte)'\\' || token[i + 2] != (byte)'u'
                    || Hex(token.Slice(i + 3, 4)) is not (>= 0xDC00 and <= 0xDFFF))
                {
                    throw new ToolContractException(ToolError.InvalidEncoding);
                }
                i += 6;
            }
        }
    }

    private static int Hex(ReadOnlySpan<byte> digits)
    {
        var result = 0;
        foreach (var digit in digits)
        {
            result = (result << 4) + (digit switch
            {
                >= (byte)'0' and <= (byte)'9' => digit - '0',
                >= (byte)'a' and <= (byte)'f' => digit - 'a' + 10,
                _ => digit - 'A' + 10,
            });
        }
        return result;
    }
}

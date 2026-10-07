using System.Text;
using System.Text.Json;

namespace SolusAgent.Tools.Api;

/// <summary>Finite limits for the current function-tool draft.</summary>
public static class ToolLimits
{
    /// <summary>Maximum canonical tool, capability, or property identifier characters.</summary>
    public const int IdentifierCharacters = 64;
    /// <summary>Maximum opaque call identifier UTF-8 bytes.</summary>
    public const int CallIdBytes = 128;
    /// <summary>Maximum normalized description UTF-8 bytes.</summary>
    public const int DescriptionBytes = 1024;
    /// <summary>Maximum source schema UTF-8 bytes.</summary>
    public const int SchemaBytes = 8192;
    /// <summary>Maximum argument or result UTF-8 bytes.</summary>
    public const int PayloadBytes = 16384;
    /// <summary>Maximum supported schema property count.</summary>
    public const int Properties = 32;
    /// <summary>Maximum JSON container depth before semantic validation.</summary>
    public const int JsonDepth = 4;
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

    internal static string CallId(string value)
    {
        Text(value, ToolLimits.CallIdBytes);
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

    internal static JsonDocument Json(string value, int maximumBytes)
    {
        Text(value, maximumBytes);
        try
        {
            var bytes = StrictUtf8.GetBytes(value);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = ToolLimits.JsonDepth });
            var objects = new Stack<HashSet<string>>();
            var totalProperties = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) { objects.Push(new(StringComparer.Ordinal)); }
                if (reader.TokenType == JsonTokenType.EndObject) { objects.Pop(); }
                if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName)
                {
                    CheckEscapedUnicode(reader.ValueSpan);
                    Text(reader.GetString()!, maximumBytes);
                }
                if (reader.TokenType == JsonTokenType.PropertyName
                    && (!objects.Peek().Add(reader.GetString()!) || ++totalProperties > 128))
                {
                    throw new ToolContractException(ToolError.InvalidJson);
                }
            }
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = ToolLimits.JsonDepth });
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

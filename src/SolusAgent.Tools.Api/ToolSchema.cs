using System.Text;
using System.Text.Json;

namespace SolusAgent.Tools.Api;

/// <summary>A bounded closed object profile with nested objects/arrays, nullable values and exact scalar constraints.</summary>
/// <remarks>Unsupported keywords, duplicate members, references and arbitrary unions reject. This is not general JSON Schema.</remarks>
public sealed partial class ToolSchema
{
    private readonly Node root;

    private ToolSchema(Node root)
    {
        this.root = root;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) root.Write(writer);
        NormalizedJson = ToolBoundary.Text(Encoding.UTF8.GetString(stream.ToArray()), ToolLimits.SchemaBytes);
    }

    /// <summary>The deterministic complete schema, including intrinsic array bounds; no supported constraint is discarded.</summary>
    public string NormalizedJson { get; }
    /// <summary>The supported profile identifier, which adapters must explicitly support.</summary>
    public string Profile => "bounded_closed_object";

    /// <summary>Admits source bytes before strict decoding and parsing. Throws a classified rejection.</summary>
    public static ToolSchema FromUtf8(ReadOnlySpan<byte> utf8Json) => Parse(ToolBoundary.Decode(utf8Json, ToolLimits.SchemaBytes));

    /// <summary>Parses the supported bounded semantics with a nonnullable closed object root.</summary>
    public static ToolSchema Parse(string json)
    {
        using var document = ToolBoundary.Json(json, ToolLimits.SchemaBytes, schema: true);
        var nodes = 0;
        var properties = 0;
        var root = Node.Read(document.RootElement, 0, ref nodes, ref properties);
        Require(root.Type == "object" && !root.Nullable);
        return new(root);
    }

    /// <summary>Validates actual JSON against every supported constraint and the independent payload budgets.</summary>
    public ToolError Validate(string json, int maximumBytes = ToolLimits.PayloadBytes)
    {
        if (maximumBytes is < 1 or > ToolLimits.MaximumPayloadBytes) { throw new ArgumentOutOfRangeException(nameof(maximumBytes)); }
        try
        {
            using var document = ToolBoundary.Json(json, maximumBytes);
            return root.Accepts(document.RootElement) ? ToolError.None : ToolError.InvalidArguments;
        }
        catch (ToolContractException exception) { return exception.Error; }
    }

    /// <summary>Returns only the type name, rather than schema contents.</summary>
    public override string ToString() => nameof(ToolSchema);

    private static void Require(bool condition)
    {
        if (!condition) throw new ToolContractException(ToolError.UnsupportedSchema);
    }
}

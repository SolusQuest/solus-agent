using System.Text.Json;
using SolusAgent.Tools.Api;

namespace CustomTools;

/// <summary>Exercises a real domain hook at the admitted six-container payload boundary, without product policy or I/O.</summary>
public sealed class NestedInputTool : FunctionTool<StructuredReadCapability>
{
    /// <summary>Creates a closed nested input producer using the same narrow counting capability.</summary>
    public NestedInputTool() : base(new("nested_input", "Synthetic nested input",
        ToolSchema.Parse(InputSchema), ToolSchema.Parse("""{"type":"object","properties":{},"required":[],"additionalProperties":false}"""),
        "synthetic_read", ToolEffect.ReadOnly, 2048, 4096)) { }

    /// <summary>A bounded object/array input whose tags array is the sixth container.</summary>
    public const string InputSchema = """
        {"type":"object","properties":{"rows":{"type":"array","minItems":1,"maxItems":2,"items":{"type":"object","properties":{"values":{"type":"array","minItems":1,"maxItems":2,"items":{"type":"object","properties":{"tags":{"type":"array","minItems":1,"maxItems":2,"items":{"type":"string"}}},"required":["tags"],"additionalProperties":false}}},"required":["values"],"additionalProperties":false}}},"required":["rows"],"additionalProperties":false}
        """;
    /// <summary>The original nested JSON used through preparation and invocation.</summary>
    public const string Arguments = """{"rows":[{"values":[{"tags":["allowed"]}]}]}""";
    /// <inheritdoc />
    protected override bool ValidateArguments(JsonElement arguments) =>
        arguments.GetProperty("rows")[0].GetProperty("values")[0].GetProperty("tags")[0].GetString() == "allowed";
    /// <inheritdoc />
    protected override ValueTask<ToolOutput> InvokeCoreAsync(ToolCall call, StructuredReadCapability capability, CancellationToken cancellationToken)
    {
        capability.Enter(1, cancellationToken);
        return ValueTask.FromResult(ToolOutput.Success(call, "{}"));
    }
}

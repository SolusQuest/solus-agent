using SolusAgent.Tools.Api;
using System.Text;
using System.Text.Json;

namespace PackageConsumers.Foundation.CustomTool;

public sealed class Counter : IToolCapability
{
    public string CapabilityId => "foundation_increment";
    public int Effects { get; private set; }
    public void Increment(CancellationToken token) { token.ThrowIfCancellationRequested(); Effects++; }
}

public sealed class IncrementTool : FunctionTool<Counter>
{
    public IncrementTool() : base(new("increment", "Increment a synthetic counter once.",
        ToolSchema.Parse("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"),
        ToolSchema.Parse("{\"type\":\"object\",\"properties\":{\"done\":{\"type\":\"boolean\"}},\"required\":[\"done\"],\"additionalProperties\":false}"),
        "foundation_increment", ToolEffect.Mutating)) { }

    protected override ValueTask<ToolOutput> InvokeCoreAsync(ToolCall call, Counter capability, CancellationToken cancellationToken)
    {
        capability.Increment(cancellationToken);
        return ValueTask.FromResult(ToolOutput.Success(call, "{\"done\":true}"));
    }
}

public sealed class ArtifactTool : FunctionTool<Counter>
{
    private readonly string json;
    public ArtifactTool(string artifact) : base(new("artifact", "Return a complete synthetic artifact once.",
        ToolSchema.Parse("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"),
        ToolSchema.Parse("{\"type\":\"object\",\"properties\":{\"artifact\":{\"type\":\"string\"}},\"required\":[\"artifact\"],\"additionalProperties\":false}"),
        "foundation_increment", ToolEffect.Mutating, maximumResultBytes: ToolLimits.MaximumPayloadBytes))
    {
        if (Encoding.UTF8.GetByteCount(artifact) > 1048576) throw new ArgumentException("Invalid synthetic artifact.");
        using var validated = JsonDocument.Parse(artifact);
        json = JsonSerializer.Serialize(new { artifact });
    }
    protected override ValueTask<ToolOutput> InvokeCoreAsync(ToolCall call, Counter capability, CancellationToken token)
    { capability.Increment(token); return ValueTask.FromResult(ToolOutput.Success(call, json)); }
}

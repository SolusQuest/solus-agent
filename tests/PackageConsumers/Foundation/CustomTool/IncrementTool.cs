using SolusAgent.Tools.Api;

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

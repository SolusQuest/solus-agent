using System.Text.Json;
using SolusAgent.Tools.Api;

namespace PackageConsumers.Apr.CustomTool;

public sealed record TargetState(Guid Id, int Effects);

public sealed class Counter(string path, Guid id) : IToolCapability
{
    public string CapabilityId => "apr_increment";
    public Guid TargetId => id;
    public int Entries { get; private set; }
    public int Effects => JsonSerializer.Deserialize<TargetState>(File.ReadAllBytes(path))!.Effects;
    public void Increment(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Entries++;
        var state = JsonSerializer.Deserialize<TargetState>(File.ReadAllBytes(path))!;
        if (state.Id != id) throw new InvalidOperationException("Wrong effect target.");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(state with { Effects = checked(state.Effects + 1) }));
    }
}

public sealed class IncrementTool : FunctionTool<Counter>
{
    public IncrementTool() : base(new("increment", "Increment a synthetic counter once.",
        ToolSchema.Parse("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"),
        ToolSchema.Parse("{\"type\":\"object\",\"properties\":{\"done\":{\"type\":\"boolean\"}},\"required\":[\"done\"],\"additionalProperties\":false}"),
        "apr_increment", ToolEffect.Mutating)) { }
    protected override ValueTask<ToolOutput> InvokeCoreAsync(ToolCall call, Counter capability, CancellationToken cancellationToken)
    {
        capability.Increment(cancellationToken);
        return ValueTask.FromResult(ToolOutput.Success(call, "{\"done\":true}"));
    }
}

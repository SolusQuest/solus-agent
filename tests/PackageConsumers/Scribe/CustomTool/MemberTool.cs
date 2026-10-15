using System.Text.Json;
using SolusAgent.Tools.Api;
namespace PackageConsumers.Scribe.CustomTool;
public sealed class MemberCapability(string directory, string[] allowed) : IToolCapability
{
    public string CapabilityId => "scribe_members";
    public List<string> Entries { get; } = [];
    public string Apply(string member, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (!allowed.Contains(member)) throw new InvalidOperationException("Unselected tool target.");
        Entries.Add(member);
        using var output = new FileStream(Path.Combine(directory, member + ".effect"), FileMode.CreateNew, FileAccess.Write);
        output.WriteByte(1);return "tool-" + member;
    }
}
public sealed class MemberTool : FunctionTool<MemberCapability>
{
    public MemberTool() : base(new("member", "Inspect one synthetic selected member.",
        ToolSchema.Parse("{\"type\":\"object\",\"properties\":{\"member\":{\"type\":\"string\"}},\"required\":[\"member\"],\"additionalProperties\":false}"),
        ToolSchema.Parse("{\"type\":\"object\",\"properties\":{\"fact\":{\"type\":\"string\"}},\"required\":[\"fact\"],\"additionalProperties\":false}"), "scribe_members", ToolEffect.Mutating)) { }
    protected override ValueTask<ToolOutput> InvokeCoreAsync(ToolCall call, MemberCapability capability, CancellationToken cancellationToken)
    {
        using var json = JsonDocument.Parse(call.ArgumentsJson);var member = json.RootElement.GetProperty("member").GetString()!;
        return ValueTask.FromResult(ToolOutput.Success(call, JsonSerializer.Serialize(new { fact = capability.Apply(member, cancellationToken) })));
    }
}

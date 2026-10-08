using CustomTools;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Tools;

public sealed class ToolAdmissionTests
{
    [Theory]
    [InlineData("missing")] [InlineData("wrong_type")] [InlineData("wrong_id")] [InlineData("throwing_id")]
    public async Task EffectFreeConcreteAdmissionRejectsWithoutClaimAndValidNeighborInvokes(string mode)
    {
        IFunctionTool tool = new CounterTool(); var call = new ToolCall("id", "counter", "{\"amount\":1}");
        var handle = tool.Prepare(call).Prepared!; var valid = new CounterCapability();
        IToolCapability? supplied = mode switch
        { "missing" => null, "wrong_type" => new OtherCapability(), "wrong_id" => new CounterCapability("other"), _ => new ThrowingCapability() };
        Assert.Equal(ToolError.UnsupportedCapability, tool.ValidateInvocation(handle, call, supplied));
        Assert.Equal(ToolError.None, tool.ValidateInvocation(handle, call, valid));
        Assert.Equal(ToolError.None, tool.ValidateInvocation(handle, call, valid)); Assert.Equal(0, valid.Effects);
        Assert.Equal(ToolOutcome.Succeeded, (await tool.InvokeAsync(handle, call, valid)).Outcome);
        Assert.Equal(ToolError.AlreadyInvoked, tool.ValidateInvocation(handle, call, valid)); Assert.Equal(1, valid.Effects);
        Assert.Equal(ToolError.AlreadyInvoked, (await tool.InvokeAsync(handle, call, valid)).Error); Assert.Equal(1, valid.Effects);
    }
    [Theory]
    [InlineData("owner", ToolError.PreparedMismatch)] [InlineData("id", ToolError.CallMismatch)]
    [InlineData("name", ToolError.CallMismatch)] [InlineData("arguments", ToolError.CallMismatch)]
    public void OwnerAndEveryOriginalCallFieldAreVerifiedBeforeAnyEffect(string field, ToolError expected)
    {
        var tool = new CounterTool(); var original = new ToolCall("id", "counter", "{\"amount\":1}");
        var handle = tool.Prepare(original).Prepared!; var capability = new CounterCapability();
        var call = field switch
        { "id" => new ToolCall("other", original.ToolName, original.ArgumentsJson), "name" => new ToolCall(original.CallId, "other", original.ArgumentsJson),
            "arguments" => new ToolCall(original.CallId, original.ToolName, "{\"amount\":2}"), _ => original };
        IFunctionTool receiver = field == "owner" ? new CounterTool() : tool;
        Assert.Equal(expected, receiver.ValidateInvocation(handle, call, capability)); Assert.Equal(0, capability.Effects);
        Assert.Equal(ToolError.None, tool.ValidateInvocation(handle, original, capability));
    }
    [Fact]
    public async Task InterfaceOnlyProducerUsesSameAdmissionWithoutBaseClassCast()
    {
        IFunctionTool tool = new TransformTool(); var capability = new TransformCapability();
        var call = new ToolCall("id", "transform", "{\"text\":\"data\"}"); var handle = tool.Prepare(call).Prepared!;
        Assert.False(tool is FunctionTool<TransformCapability>); Assert.Equal(ToolError.None, tool.ValidateInvocation(handle, call, capability));
        Assert.Equal(0, capability.Effects); var result = await tool.InvokeAsync(handle, call, capability);
        Assert.Equal(ToolOutcome.Succeeded, result.Outcome); Assert.Equal("{\"text\":\"DATA\"}", result.Json); Assert.Equal(1, capability.Effects);
    }
    private sealed class OtherCapability : IToolCapability { public string CapabilityId => "counter_increment"; }
    private sealed class ThrowingCapability : IToolCapability { public string CapabilityId => throw new InvalidOperationException("CAPABILITY_CANARY"); }
}

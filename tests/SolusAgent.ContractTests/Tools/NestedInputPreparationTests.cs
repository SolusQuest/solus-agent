using CustomTools;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Tools;

public sealed class NestedInputPreparationTests
{
    [Theory]
    [InlineData("{\"rows\":[]}")]
    [InlineData("{\"rows\":[{\"values\":[]}]}")]
    [InlineData("{\"rows\":[{\"values\":[{\"tags\":[]}]}]}")]
    [InlineData("{\"rows\":[{\"values\":[{\"tags\":[null]}]}]}")]
    public void ActualNestedItemMinimumAndNonnullableTypesRejectBeforeTheDomainHook(string json)
    {
        Assert.Equal(ToolError.InvalidArguments, new NestedInputTool().Prepare(new("invalid", "nested_input", json)).Error);
    }

    [Fact]
    public async Task RealDomainHookReparsesTheAdmittedSixContainerInputAndRetainsOriginalCall()
    {
        var tool = new NestedInputTool(); var capability = new StructuredReadCapability();
        var call = new ToolCall("nested", "nested_input", NestedInputTool.Arguments);
        var prepared = tool.Prepare(call); Assert.True(prepared.Accepted); Assert.Equal(0, capability.Entries);
        var result = await tool.InvokeAsync(prepared.Prepared!, call, capability);
        Assert.Equal(ToolOutcome.Succeeded, result.Outcome); Assert.Equal(1, capability.Entries);
        Assert.Equal(NestedInputTool.Arguments, result.Call.ArgumentsJson);
        var refused = new ToolCall("denied", "nested_input", NestedInputTool.Arguments.Replace("allowed", "denied", StringComparison.Ordinal));
        Assert.Equal(ToolError.None, tool.Descriptor.InputSchema.Validate(refused.ArgumentsJson));
        Assert.Equal(ToolError.DomainRejected, tool.Prepare(refused).Error); Assert.Equal(1, capability.Entries);
    }
}

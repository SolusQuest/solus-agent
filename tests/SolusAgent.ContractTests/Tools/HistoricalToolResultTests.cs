using System.Text;
using CustomTools;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Tools;

public sealed class HistoricalToolResultTests
{
    [Theory]
    [InlineData(CounterMode.Normal)]
    [InlineData(CounterMode.ThrowAfterEffect)]
    [InlineData(CounterMode.FailAfterEffect)]
    [InlineData(CounterMode.WrongAssociation)]
    [InlineData(CounterMode.OversizedResult)]
    [InlineData(CounterMode.InvalidResult)]
    [InlineData(CounterMode.UnrelatedCancellation)]
    [InlineData(CounterMode.InvalidResultEncoding)]
    [InlineData(CounterMode.DuplicateResult)]
    [InlineData(CounterMode.MalformedResult)]
    [InlineData(CounterMode.WrongArguments)]
    public async Task IndependentConsumerRestoresActualGuardedFactsWithoutAnotherEffect(CounterMode mode)
    {
        var tool = new CounterTool(mode); var capability = new CounterCapability();
        var call = new ToolCall("Case-Sensitive", "counter", "{ \"amount\":3 }");
        var handle = tool.Prepare(call).Prepared!;
        var actual = await tool.InvokeAsync(handle, call, capability);
        Assert.False(actual.IsHistorical); Assert.Equal(1, capability.Effects);
        var restored = HistoricalToolResults.Restore(tool.Descriptor, call, actual);
        Equal(actual, restored); Assert.Equal(1, capability.Effects);
        Assert.Equal(ToolError.AlreadyInvoked, (await tool.InvokeAsync(handle, call, capability)).Error);
        Assert.Equal(1, capability.Effects);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("call")]
    [InlineData("capability")]
    [InlineData("used")]
    [InlineData("cancel-before")]
    [InlineData("cancel-after")]
    public async Task ActualAdmissionAndCancellationFactsPreserveStartedMeaning(string mode)
    {
        var tool = new CounterTool(mode == "cancel-after" ? CounterMode.WaitAfterEffect : CounterMode.Normal);
        var capability = new CounterCapability(); var call = new ToolCall("id", "counter", "{\"amount\":2}");
        var handle = (mode == "owner" ? new CounterTool() : tool).Prepare(call).Prepared!;
        if (mode == "used") await tool.InvokeAsync(handle, call, capability);
        if (mode == "call") call = new(call.CallId, call.ToolName, "{\"amount\":3}");
        using var cancellation = new CancellationTokenSource();
        if (mode == "cancel-before") cancellation.Cancel();
        var operation = tool.InvokeAsync(handle, call, mode == "capability" ? null : capability, cancellation.Token).AsTask();
        if (mode == "cancel-after") { await tool.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel(); }
        var actual = await operation;
        var effects = capability.Effects;
        Equal(actual, HistoricalToolResults.Restore(tool.Descriptor, call, actual));
        Assert.Equal(effects, capability.Effects);
        Assert.Equal(mode == "cancel-after", actual.InvocationStarted);
    }

    [Theory]
    [InlineData(ToolOutcome.Succeeded, ToolError.None, false, "{\"total\":1}")]
    [InlineData(ToolOutcome.Succeeded, ToolError.InvocationFailed, true, "{\"total\":1}")]
    [InlineData(ToolOutcome.Succeeded, ToolError.None, true, null)]
    [InlineData(ToolOutcome.Failed, ToolError.None, true, null)]
    [InlineData(ToolOutcome.Failed, ToolError.Cancelled, true, null)]
    [InlineData(ToolOutcome.Failed, ToolError.DomainRejected, false, null)]
    [InlineData(ToolOutcome.Cancelled, ToolError.InvocationFailed, true, null)]
    [InlineData(ToolOutcome.Cancelled, ToolError.Cancelled, false, "{}")]
    [InlineData(ToolOutcome.Rejected, ToolError.InvalidResult, false, null)]
    [InlineData(ToolOutcome.Rejected, ToolError.AlreadyInvoked, true, null)]
    [InlineData((ToolOutcome)99, ToolError.None, true, null)]
    [InlineData(ToolOutcome.Failed, (ToolError)99, true, null)]
    public void IncoherentHistoricalFactsReject(ToolOutcome outcome, ToolError error, bool started, string? json)
    {
        var descriptor = new CounterTool().Descriptor; var call = new ToolCall("id", "counter", "{\"amount\":1}");
        Assert.Throws<ToolContractException>(() => ToolResult.RestoreHistorical(descriptor, call, call, outcome, error, started, json));
    }

    [Fact]
    public async Task EveryActualFixedImplementationFailureRemainsRepresentable()
    {
        var call = new ToolCall("id", "counter", "{\"amount\":1}");
        foreach (var error in Enum.GetValues<ToolError>().Where(e => e is not (ToolError.None or ToolError.Cancelled)))
        {
            var tool = new FailureTool(error); var capability = new CounterCapability();
            var actual = await tool.InvokeAsync(tool.Prepare(call).Prepared!, call, capability);
            Equal(actual, HistoricalToolResults.Restore(tool.Descriptor, call, actual));
            Assert.Equal(error, actual.Error); Assert.Equal(1, capability.Effects);
        }
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("arguments")]
    [InlineData("argument-schema")]
    [InlineData("result-schema")]
    [InlineData("argument-limit")]
    [InlineData("result-limit")]
    [InlineData("unicode")]
    public void AssociationAndDescriptorBoundariesAreValidatedWithoutAnInvocation(string mutation)
    {
        var descriptor = new CounterTool().Descriptor;
        var original = new ToolCall("id", "counter", "{\"amount\":1}"); var recorded = original;
        var json = "{\"total\":1}";
        switch (mutation)
        {
            case "id": recorded = new("Id", "counter", original.ArgumentsJson); break;
            case "name": recorded = new("id", "other", original.ArgumentsJson); break;
            case "arguments": recorded = new("id", "counter", "{ \"amount\":1}"); break;
            case "argument-schema": original = recorded = new("id", "counter", "{\"amount\":\"x\"}"); break;
            case "result-schema": json = "{\"total\":\"x\"}"; break;
            case "argument-limit": descriptor = new CounterTool(maximumArgumentBytes: Encoding.UTF8.GetByteCount(original.ArgumentsJson) - 1).Descriptor; break;
            case "result-limit": descriptor = new CounterTool(maximumResultBytes: Encoding.UTF8.GetByteCount(json) - 1).Descriptor; break;
            case "unicode": json = "{\"total\":\"\ud800\"}"; break;
        }
        Assert.Throws<ToolContractException>(() => ToolResult.RestoreHistorical(descriptor, original, recorded, ToolOutcome.Succeeded, ToolError.None, true, json));
    }

    [Fact]
    public void NullsAndExactInclusiveByteBoundariesAreExplicit()
    {
        var call = new ToolCall("id", "counter", "{\"amount\":1}"); var json = "{\"total\":1}";
        var descriptor = new CounterTool(maximumArgumentBytes: Encoding.UTF8.GetByteCount(call.ArgumentsJson), maximumResultBytes: Encoding.UTF8.GetByteCount(json)).Descriptor;
        Assert.True(ToolResult.RestoreHistorical(descriptor, call, call, ToolOutcome.Succeeded, ToolError.None, true, json).IsHistorical);
        Assert.Throws<ArgumentNullException>(() => ToolResult.RestoreHistorical(null!, call, call, ToolOutcome.Succeeded, ToolError.None, true, json));
        Assert.Throws<ArgumentNullException>(() => ToolResult.RestoreHistorical(descriptor, null!, call, ToolOutcome.Succeeded, ToolError.None, true, json));
        Assert.Throws<ArgumentNullException>(() => ToolResult.RestoreHistorical(descriptor, call, null!, ToolOutcome.Succeeded, ToolError.None, true, json));
    }

    private static void Equal(ToolResult actual, ToolResult restored)
    {
        Assert.True(restored.IsHistorical); Assert.True(actual.Call.Matches(restored.Call));
        Assert.Equal(actual.Outcome, restored.Outcome); Assert.Equal(actual.Error, restored.Error);
        Assert.Equal(actual.InvocationStarted, restored.InvocationStarted); Assert.Equal(actual.Json, restored.Json);
        Assert.Equal(nameof(ToolResult), restored.ToString());
    }
    private sealed class FailureTool(ToolError error) : FunctionTool<CounterCapability>(new CounterTool().Descriptor)
    {
        protected override ValueTask<ToolOutput> InvokeCoreAsync(ToolCall call, CounterCapability capability, CancellationToken cancellationToken)
        { capability.Add(1, cancellationToken); return ValueTask.FromResult(ToolOutput.Failure(call, error)); }
    }
}

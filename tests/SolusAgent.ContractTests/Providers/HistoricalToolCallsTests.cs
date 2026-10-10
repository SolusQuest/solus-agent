using CustomTools;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Providers;

public sealed class HistoricalToolCallsTests
{
    [Fact]
    public void PublicRestorationAcceptsOnlyAfterExactRequestValidationWithoutToolExecution()
    {
        var tool = new CounterTool(); var capability = new CounterCapability();
        var request = Request(tool.Descriptor); var call = new ToolCall("Original-ID", "counter", "{ \"amount\":7 }");
        var candidate = new ProviderResponse(request.Scope, request.Attempt, ProviderFinish.ToolCalls, "model", [call], new(request.Scope, request.Attempt, [1, 2]));
        var accepted = ProviderResponse.RestoreToolCalls(request, candidate);
        Assert.False(candidate.Accepted); Assert.True(accepted.Accepted); Assert.Same(call, Assert.Single(accepted.Calls));
        Assert.True(accepted.Attempt.Matches(request.Attempt)); Assert.True(accepted.Continuation!.Matches(candidate.Continuation!));
        Assert.Equal(0, capability.Effects); Assert.False(tool.Started.Task.IsCompleted);
        Assert.Throws<ProviderContractException>(() => ProviderResponse.RestoreFinal(request, candidate));
    }
    [Theory]
    [InlineData("scope")]
    [InlineData("attempt")]
    [InlineData("name")]
    [InlineData("arguments")]
    [InlineData("response-bound")]
    [InlineData("continuation-bound")]
    [InlineData("continuation-support")]
    [InlineData("final")]
    public void PublicRestorationCannotBypassOriginalRequestContracts(string mode)
    {
        var descriptor = new CounterTool().Descriptor;
        var request = Request(descriptor, mode == "response-bound" ? new(maximumResponseBytes: 1)
            : mode == "continuation-bound" ? new(maximumContinuationBytes: 1) : null,
            mode != "continuation-support");
        var scope = mode == "scope" ? new ProviderScope("other", "model") : request.Scope;
        var attempt = mode == "attempt" ? new ProviderAttempt(request.Attempt.ExecutionId, Guid.NewGuid(), Guid.NewGuid(), 1) : request.Attempt;
        var call = new ToolCall("id", mode == "name" ? "other" : "counter", mode == "arguments" ? "{\"amount\":\"invalid\"}" : "{\"amount\":7}");
        var candidate = new ProviderResponse(scope, attempt, mode == "final" ? ProviderFinish.Final : ProviderFinish.ToolCalls,
            "text", mode == "final" ? [] : [call], new(scope, attempt, [1, 2]));
        Assert.Throws<ProviderContractException>(() => ProviderResponse.RestoreToolCalls(request, candidate));
    }
    [Fact]
    public async Task RecycledCallIdentityCannotBeAdmittedIntoAcceptedHistory()
    {
        var tool = new CounterTool(); var first = Request(tool.Descriptor); var call = new ToolCall("id", "counter", "{\"amount\":1}");
        var model = ProviderResponse.RestoreToolCalls(first, new(first.Scope, first.Attempt, ProviderFinish.ToolCalls, null, [call]));
        var result = await tool.InvokeAsync(tool.Prepare(call).Prepared!, call, new CounterCapability());
        var next = new ProviderRequest(first.Scope, new(first.Attempt.ExecutionId, Guid.NewGuid(), Guid.NewGuid(), 1),
            [ProviderInput.FromModel(model), ProviderInput.FromTool(result)], [tool.Descriptor]);
        Assert.Throws<ProviderContractException>(() => ProviderResponse.RestoreToolCalls(next,
            new(next.Scope, next.Attempt, ProviderFinish.ToolCalls, null, [call])));
    }
    private static ProviderRequest Request(ToolDescriptor descriptor, ProviderExchangeBounds? bounds = null, bool continuation = true) =>
        new(new("historical", "model"), new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1), [ProviderInput.Instruction("host")],
            [descriptor], requiredCapabilities: continuation ? ProviderCapabilities.Continuation : ProviderCapabilities.None, bounds: bounds);
}

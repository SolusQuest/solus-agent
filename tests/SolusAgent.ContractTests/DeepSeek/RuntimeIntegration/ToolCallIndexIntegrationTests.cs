using System.Text.Json.Nodes;
using CustomTools;
using SolusAgent.Api.Execution;
using SolusAgent.ContractTests.DeepSeek.Adapter;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.RuntimeIntegration;

public sealed class ToolCallIndexIntegrationTests
{
    [Fact]
    public async Task IndexedBatchExecutesOrdinaryToolsOnceBeforeFinal()
    {
        var counter = new CounterCapability(); var transform = new TransformCapability();
        var observedCounter = new ObservedFunctionTool(new CounterTool(maximumResultBytes: 64));
        var observedTransform = new ObservedFunctionTool(new TransformTool(maximumResultBytes: 64));
        var responses = new Queue<string>([Body(), AdapterFixture.Response("final", AdapterFixture.Replay)]);
        using var handler = new FakeHandler((_, _) => Task.FromResult(AdapterFixture.Http(responses.Dequeue())));
        using var provider = AdapterFixture.Provider(handler);
        var agent = RuntimeAgentFactory.Create(new(provider,
            [new RuntimeToolRegistration(observedCounter, counter), new RuntimeToolRegistration(observedTransform, transform)], new RuntimeHooks()),
            IntegrationFixture.Options());
        var result = await agent.ExecuteAsync(IntegrationFixture.Execution());
        Assert.Equal(AgentTerminationReason.Completed, result.Reason); Assert.Equal(2, handler.Sends);
        Assert.Equal(1, observedCounter.Invocations); Assert.Equal(1, observedTransform.Invocations);
        Assert.Equal(1, counter.Effects); Assert.Equal(2, counter.Total);
        Assert.NotNull(result.Usage); Assert.Equal(2, result.Usage!.ToolInvocations!.Invoked);
    }

    [Theory]
    [InlineData("null")] [InlineData("0")] [InlineData("2")] [InlineData("\"1\"")] [InlineData("true")]
    public async Task InvalidLaterIndexRejectsBeforeAnyToolPreparationOrEffect(string literal)
    {
        var counter = new CounterCapability(); var transform = new TransformCapability();
        var observedCounter = new ObservedFunctionTool(new CounterTool(maximumResultBytes: 64));
        var observedTransform = new ObservedFunctionTool(new TransformTool(maximumResultBytes: 64));
        var root = JsonNode.Parse(Body())!;
        root["choices"]![0]!["message"]!["tool_calls"]![1]!["index"] = JsonNode.Parse(literal);
        using var handler = FakeHandler.Reply(root.ToJsonString()); using var provider = AdapterFixture.Provider(handler);
        var agent = RuntimeAgentFactory.Create(new(provider,
            [new RuntimeToolRegistration(observedCounter, counter), new RuntimeToolRegistration(observedTransform, transform)], new RuntimeHooks()),
            IntegrationFixture.Options());
        var result = await agent.ExecuteAsync(IntegrationFixture.Execution());
        Assert.Equal(AgentTerminationReason.Failed, result.Reason); Assert.Equal(1, handler.Sends);
        Assert.Equal(0, observedCounter.Preparations); Assert.Equal(0, observedTransform.Preparations);
        Assert.Equal(0, observedCounter.Invocations); Assert.Equal(0, observedTransform.Invocations);
        Assert.Equal(0, counter.Effects); Assert.Equal(0, counter.Total); Assert.Equal(0, result.Usage!.ToolInvocations!.Invoked);
        Assert.Equal(10, result.Usage!.InputTokens.ObservedTokens); Assert.Equal(5, result.Usage!.OutputTokens.ObservedTokens);
    }

    private static string Body() => AdapterFixture.WithCallIndices(AdapterFixture.Response(null, AdapterFixture.Replay, "tool_calls",
        calls: [AdapterFixture.Call("counter-call", "counter", IntegrationFixture.CounterArguments),
            AdapterFixture.Call("transform-call", "transform", IntegrationFixture.TransformArguments)]));
}

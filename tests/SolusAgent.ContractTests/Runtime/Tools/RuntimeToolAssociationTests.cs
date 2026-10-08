using CustomTools;
using SolusAgent.Api.Execution;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Tools;

public sealed class RuntimeToolAssociationTests
{
    [Theory]
    [InlineData("missing")] [InlineData("duplicate")] [InlineData("foreign")]
    [InlineData("arguments")] [InlineData("name")] [InlineData("before_model")] [InlineData("continuation")]
    public async Task ActualNextProviderRequestRejectsAlteredRoundAssociations(string mode)
    {
        var counter = new CounterTool(maximumResultBytes: 64); var cap = new CounterCapability();
        var transform = new TransformTool(); var other = new TransformCapability(); ProviderRequest? next = null;
        var provider = new ScriptedProvider([
            (r, o, _) => ToolFixture.Calls(r, o, [ToolFixture.Counter("c"), ToolFixture.Transform("t")], new(r.Scope, r.Attempt, [1, 2])),
            async (r, o, token) =>
            {
                next = r; var inputs = r.Inputs.ToList(); var first = inputs[2].ToolResult!; var continuation = r.Continuation;
                switch (mode)
                {
                    case "missing": inputs.RemoveAt(3); break;
                    case "duplicate": inputs.Add(inputs[2]); break;
                    case "before_model": (inputs[1], inputs[2]) = (inputs[2], inputs[1]); break;
                    case "continuation": continuation = null; break;
                    default:
                        IFunctionTool tool = mode == "name" ? transform : counter;
                        var call = mode == "name" ? ToolFixture.Transform("c") : new ToolCall(mode == "foreign" ? "foreign" : first.Call.CallId, first.Call.ToolName, mode == "arguments" ? "{\"amount\":2}" : first.Call.ArgumentsJson);
                        var handle = tool.Prepare(call).Prepared!;
                        var result = await tool.InvokeAsync(handle, call, mode == "name" ? other : new CounterCapability(), token);
                        inputs[2] = ProviderInput.FromTool(result); break;
                }
                var failure = Assert.Throws<ProviderContractException>(() => new ProviderRequest(r.Scope, r.Attempt, inputs, r.Tools, continuation, r.RequiredCapabilities, r.Bounds));
                Assert.Equal(mode == "continuation" ? ProviderError.ContinuationMismatch : ProviderError.InvalidAssociation, failure.Error);
                return await ScriptedProvider.Final(r, o, token);
            }]);
        var outcome = await ToolFixture.Agent(provider, ToolFixture.Bindings(counter, cap, transform, other), new(requireContinuation: true)).ExecuteAsync(RuntimeFixture.Request(2));
        Assert.NotNull(next); Assert.Equal(AgentTerminationReason.Completed, outcome.Reason); Assert.Equal(1, cap.Effects);
        Assert.Equal(mode == "name" ? 2 : 1, other.Effects); Assert.Equal(2, provider.Effects);
    }

    [Fact]
    public async Task CompleteAssociatedResultsMayBeReorderedWithoutChangingMeaning()
    {
        var counter = new CounterTool(maximumResultBytes: 64); var cap = new CounterCapability(); var transform = new TransformTool(); var other = new TransformCapability();
        var provider = new ScriptedProvider([
            (r, o, _) => ToolFixture.Calls(r, o, [ToolFixture.Counter("c"), ToolFixture.Transform("t")]),
            (r, o, token) =>
            {
                var inputs = r.Inputs.ToArray(); (inputs[2], inputs[3]) = (inputs[3], inputs[2]);
                var reordered = new ProviderRequest(r.Scope, r.Attempt, inputs, r.Tools);
                Assert.Equal(new[] { "t", "c" }, reordered.Inputs.Skip(2).Select(i => i.ToolResult!.Call.CallId));
                return ScriptedProvider.Final(r, o, token);
            }]);
        var outcome = await ToolFixture.Agent(provider, ToolFixture.Bindings(counter, cap, transform, other)).ExecuteAsync(RuntimeFixture.Request(2));
        Assert.Equal(AgentTerminationReason.Completed, outcome.Reason); Assert.Equal(1, cap.Effects); Assert.Equal(1, other.Effects);
    }

    [Fact]
    public async Task RecycledCallIdOnSecondActualResponseNeverRepeatsToolEffects()
    {
        var cap = new CounterCapability(); var tool = new CounterTool(maximumResultBytes: 64);
        var provider = new ScriptedProvider([(r, o, _) => ToolFixture.Calls(r, o, [ToolFixture.Counter("same")]), (r, o, _) => ToolFixture.Calls(r, o, [ToolFixture.Counter("same")])]);
        var outcome = await ToolFixture.Agent(provider, [new(tool, cap)]).ExecuteAsync(RuntimeFixture.Request(3));
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(1, cap.Effects); Assert.Equal(2, provider.Effects);
        Assert.Equal(1, outcome.CompletedWorkUnits); Assert.Equal(2, outcome.Usage!.Attempts.Count);
        Assert.All(outcome.Usage.Attempts, attempt => Assert.Equal(3, attempt.Usage.InputTokens));
    }
}

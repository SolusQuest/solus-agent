using System.Text.Json;
using CustomTools;
using SolusAgent.Api.Execution;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Tools;

public sealed class RuntimeToolTurnTests
{
    [Fact]
    public async Task PublicFactoryRunsUnrelatedBaseAndInterfaceToolsAcrossThreeActualProviderTurns()
    {
        var counter = new CounterTool(maximumResultBytes: 64); var cap = new CounterCapability(); var transformCap = new TransformCapability();
        var order = new List<string>();
        IFunctionTool transform = new TransformTool(effect: (call, capability, token) =>
        { order.Add(call.CallId); Assert.Equal(order.Count == 1 ? 2 : 5, cap.Total); using var data = JsonDocument.Parse(call.ArgumentsJson);
            return ValueTask.FromResult(ToolOutput.Success(call, JsonSerializer.Serialize(new { text = capability.Upper(data.RootElement.GetProperty("text").GetString()!, token) }))); });
        var firstReplay = new byte[] { 1, 2, 3 }; var lastReplay = new byte[] { 4, 5, 6 }; ProviderRequest? second = null, third = null;
        var provider = new ScriptedProvider([
            (request, observation, _) => ToolFixture.Calls(request, observation, [ToolFixture.Counter("c1", 2), ToolFixture.Transform("t1", "alpha")], new(request.Scope, request.Attempt, firstReplay)),
            (request, observation, _) => { second = request; return ToolFixture.Calls(request, observation,
                [ToolFixture.Counter("c2", 3), ToolFixture.Transform("t2", "beta")], new(request.Scope, request.Attempt, lastReplay)); },
            (request, observation, token) => { third = request; return ScriptedProvider.Final(request, observation, token); }]);
        var progress = new List<AgentProgress>(); var outcome = await ToolFixture.Agent(provider,
            ToolFixture.Bindings(counter, cap, transform, transformCap), new(requireContinuation: true)).ExecuteAsync(RuntimeFixture.Request(3), new InlineProgress(progress.Add));
        Assert.Equal(AgentTerminationReason.Completed, outcome.Reason); Assert.Equal(3, outcome.CompletedWorkUnits);
        Assert.Equal(3, provider.Effects); Assert.Equal(2, cap.Effects); Assert.Equal(2, transformCap.Effects); Assert.Equal(5, cap.Total);
        Assert.Equal(new[] { "t1", "t2" }, order); Assert.Equal(new[] { 1, 2, 3 }, progress.Select(p => p.CompletedWorkUnits));
        Assert.Equal(firstReplay, second!.Continuation!.CopyReplayBytes()); Assert.Equal(lastReplay, third!.Continuation!.CopyReplayBytes());
        Assert.Equal(new[] { ProviderInputKind.HostInstruction, ProviderInputKind.ModelData, ProviderInputKind.ToolResultData, ProviderInputKind.ToolResultData }, second.Inputs.Select(i => i.Kind));
        Assert.Equal(new[] { "c1", "t1", "c2", "t2" }, third.Inputs.Where(i => i.ToolResult is not null).Select(i => i.ToolResult!.Call.CallId));
        Assert.All(third.Inputs.Where(i => i.ToolResult is not null), i => Assert.Equal(ToolOutcome.Succeeded, i.ToolResult!.Outcome));
        Assert.Equal(3, outcome.Usage!.Attempts.Count); Assert.All(outcome.Usage.Attempts, attempt => Assert.Equal(3, attempt.Usage.InputTokens));
        Assert.False(transform is FunctionTool<TransformCapability>);
    }

    [Theory]
    [InlineData("domain")] [InlineData("concrete")]
    public async Task LastMemberDomainOrConcreteCapabilityFailureHasZeroToolEffectsAndRetainsAcceptedModel(string mode)
    {
        var counter = new CounterTool(maximumResultBytes: 64); var cap = new CounterCapability();
        var transform = new TransformTool(rejectDomain: mode == "domain"); var transformCap = new TransformCapability();
        IToolCapability supplied = mode == "concrete" ? new WrongTransformCapability() : transformCap;
        var provider = new ScriptedProvider([(request, observation, _) => ToolFixture.Calls(request, observation, [ToolFixture.Counter(), ToolFixture.Transform()])]);
        var outcome = await ToolFixture.Agent(provider, ToolFixture.Bindings(counter, cap, transform, supplied)).ExecuteAsync(RuntimeFixture.Request(2));
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(AgentFailureCode.ExecutionFailed, outcome.FailureCode);
        Assert.Equal(1, outcome.CompletedWorkUnits); Assert.Equal(0, cap.Effects); Assert.Equal(0, transformCap.Effects); Assert.Equal(1, provider.Effects);
        Assert.Equal(3, outcome.Usage!.Attempts.Single().Usage.InputTokens);
    }

    [Theory]
    [InlineData("arguments")] [InlineData("unknown")] [InlineData("duplicate")]
    public async Task ActualProviderResponseGuardRejectsBeforeToolsAndRetainsUsage(string mode)
    {
        var counter = new CounterTool(maximumResultBytes: 64); var cap = new CounterCapability(); var transform = new TransformTool(); var otherCap = new TransformCapability();
        var provider = new ScriptedProvider([(request, observation, _) => ToolFixture.Calls(request, observation,
            [ToolFixture.Counter(), mode switch { "arguments" => new("bad", "transform", "{}"), "unknown" => new("bad", "unknown", "{}"), _ => new("counter_call", "transform", "{\"text\":\"data\"}") }])]);
        var outcome = await ToolFixture.Agent(provider, ToolFixture.Bindings(counter, cap, transform, otherCap)).ExecuteAsync(RuntimeFixture.Request(2));
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(0, outcome.CompletedWorkUnits);
        Assert.Equal(0, cap.Effects); Assert.Equal(0, otherCap.Effects); Assert.Equal(3, outcome.Usage!.Attempts.Single().Usage.InputTokens);
    }

    [Theory]
    [InlineData("stop", AgentTerminationReason.Partial)] [InlineData("unknown", AgentTerminationReason.Failed)]
    [InlineData("missing", AgentTerminationReason.Failed)] [InlineData("failed", AgentTerminationReason.Failed)]
    public async Task ProviderSettlementMustPermitFirstToolEffect(string mode, AgentTerminationReason reason)
    {
        var counter = new CounterTool(maximumResultBytes: 64); var cap = new CounterCapability(); var transform = new TransformTool(); var otherCap = new TransformCapability();
        var hooks = new RuntimeHooks { After = (settlement, _) => ValueTask.FromResult<SettlementAcknowledgement?>(mode switch
        { "stop" => new(settlement.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Stop), "unknown" => new(settlement.Exposure, RuntimeHookStatus.Unknown),
            "failed" => new(settlement.Exposure, RuntimeHookStatus.Failed), _ => null }) };
        var provider = new ScriptedProvider([(request, observation, _) => ToolFixture.Calls(request, observation, [ToolFixture.Counter(), ToolFixture.Transform()])]);
        var outcome = await ToolFixture.Agent(provider, ToolFixture.Bindings(counter, cap, transform, otherCap), hooks: hooks).ExecuteAsync(RuntimeFixture.Request(2));
        Assert.Equal(reason, outcome.Reason); Assert.Equal(1, outcome.CompletedWorkUnits); Assert.Equal(0, cap.Effects); Assert.Equal(0, otherCap.Effects);
        Assert.Single(hooks.Settlements); Assert.Equal(1, provider.Effects);
    }

    [Fact]
    public async Task WorkLimitOneRetainsAdmittedToolEffectsAndStopsBeforeSecondModel()
    {
        var counter = new CounterTool(maximumResultBytes: 64); var cap = new CounterCapability(); var transform = new TransformTool(); var otherCap = new TransformCapability();
        var provider = new ScriptedProvider([(request, observation, _) => ToolFixture.Calls(request, observation, [ToolFixture.Counter(), ToolFixture.Transform()]), ScriptedProvider.Final]);
        var outcome = await ToolFixture.Agent(provider, ToolFixture.Bindings(counter, cap, transform, otherCap)).ExecuteAsync(RuntimeFixture.Request(1));
        Assert.Equal(AgentTerminationReason.ResourceLimit, outcome.Reason); Assert.Equal(1, outcome.CompletedWorkUnits);
        Assert.Equal(1, cap.Effects); Assert.Equal(1, otherCap.Effects); Assert.Equal(1, provider.Effects); Assert.Single(outcome.Usage!.Attempts);
    }
    private sealed class WrongTransformCapability : IToolCapability { public string CapabilityId => "text_transform"; }
}

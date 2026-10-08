using System.Text.Json;
using CustomTools;
using SolusAgent.Api.Execution;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Tools;

public sealed class RuntimeToolIsolationTests
{
    [Theory]
    [InlineData(9, true)] [InlineData(10, false)] [InlineData(11, false)]
    public async Task DeadlineNeighborsAtToolCompletionPreserveEarlierResults(int seconds, bool valid)
    {
        var clock = new ControlledTimeProvider(); var counter = new CounterTool(maximumResultBytes: 64); var cap = new CounterCapability(); var other = new TransformCapability();
        var transform = new TransformTool(effect: (call, capability, token) =>
        { capability.Upper("effect", token); clock.Advance(TimeSpan.FromSeconds(seconds)); return ValueTask.FromResult(ToolOutput.Success(call, "{\"text\":\"result\"}")); });
        var provider = new ScriptedProvider([(r, o, _) => ToolFixture.Calls(r, o, [ToolFixture.Counter("c"), ToolFixture.Transform("t")]), ScriptedProvider.Final]);
        var outcome = await ToolFixture.Agent(provider, ToolFixture.Bindings(counter, cap, transform, other), new(clock)).ExecuteAsync(RuntimeFixture.Request(2));
        Assert.Equal(valid ? AgentTerminationReason.Completed : AgentTerminationReason.ResourceLimit, outcome.Reason);
        Assert.Equal(valid ? 2 : 1, outcome.CompletedWorkUnits); Assert.Equal(1, cap.Effects); Assert.Equal(1, other.Effects); Assert.Equal(valid ? 2 : 1, provider.Effects);
    }

    [Fact]
    public async Task EqualExecutionIdsHaveIndependentPreparedHandlesHistoryAndCutsWithIntentionallySharedHostCapability()
    {
        var entered1 = RuntimeFixture.Barrier<ToolCall>(); var entered2 = RuntimeFixture.Barrier<ToolCall>();
        var output1 = RuntimeFixture.Barrier<ToolOutput>(); var output2 = RuntimeFixture.Barrier<ToolOutput>(); var invocations = 0;
        var capability = new TransformCapability();
        var tool = new TransformTool(effect: (call, cap, token) =>
        {
            cap.Upper("effect", token);
            if (Interlocked.Increment(ref invocations) == 1) { entered1.SetResult(call); return new(output1.Task); }
            entered2.SetResult(call); return new(output2.Task);
        });
        ProviderRequest? finalRequest = null;
        var provider = new ScriptedProvider([(r, o, _) => ToolFixture.Calls(r, o, [ToolFixture.Transform("same")]),
            (r, o, _) => ToolFixture.Calls(r, o, [ToolFixture.Transform("same")]),
            (r, o, token) => { finalRequest = r; return ScriptedProvider.Final(r, o, token); }]);
        var agent = ToolFixture.Agent(provider, [new(tool, capability)]); var id = Guid.NewGuid(); using var cancellation = new CancellationTokenSource();
        var first = agent.ExecuteAsync(RuntimeFixture.Request(2, executionId: id), cancellationToken: cancellation.Token).AsTask();
        await RuntimeFixture.Await(entered1.Task);
        var second = agent.ExecuteAsync(RuntimeFixture.Request(2, executionId: id)).AsTask(); var call = await RuntimeFixture.Await(entered2.Task);
        cancellation.Cancel(); var stopped = await RuntimeFixture.Await(first); Assert.False(second.IsCompleted); Assert.False(output1.Task.IsCompleted);
        output2.SetResult(ToolOutput.Success(call, "{\"text\":\"SECOND_RESULT\"}")); var completed = await RuntimeFixture.Await(second);
        Assert.Equal(AgentTerminationReason.Cancelled, stopped.Reason); Assert.Equal(AgentTerminationReason.Completed, completed.Reason);
        Assert.Equal(1, stopped.CompletedWorkUnits); Assert.Equal(2, completed.CompletedWorkUnits); Assert.Equal(2, capability.Effects);
        Assert.NotEqual(stopped.Usage!.Attempts[0].LogicalCallId, completed.Usage!.Attempts[0].LogicalCallId);
        Assert.NotEqual(stopped.Usage.Attempts[0].PhysicalAttemptId, completed.Usage.Attempts[0].PhysicalAttemptId);
        Assert.Equal(completed.Usage.Attempts[0].LogicalCallId, finalRequest!.Inputs[1].Model!.Attempt.LogicalCallId);
        Assert.Single(finalRequest.Inputs, input => input.ToolResult is not null);
        output1.SetException(new InvalidOperationException("LATE_FAULT_CANARY"));
        Assert.Equal(AgentTerminationReason.Cancelled, stopped.Reason); Assert.Equal(2, capability.Effects); Assert.Equal(3, provider.Effects);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OrdinaryOutcomeProgressAndStartupMetadataOmitRestrictedToolData(bool observerFailure)
    {
        var progress = new List<AgentProgress>(); var cap = new TransformCapability();
        var tool = new TransformTool(effect: (call, capability, token) =>
        { capability.Upper("effect", token); return ValueTask.FromResult(ToolOutput.Success(call, "{\"text\":\"RESULT_CANARY\"}")); });
        var provider = new ScriptedProvider([(r, o, _) => ToolFixture.Calls(r, o, [ToolFixture.Transform("call", "ARGUMENT_CANARY")], new(r.Scope, r.Attempt, System.Text.Encoding.UTF8.GetBytes("REPLAY_CANARY"))), ScriptedProvider.Final]);
        var configuration = new SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration(provider, [new(tool, cap)], new RuntimeHooks());
        var agent = RuntimeAgentFactory.Create(configuration, new(requireContinuation: true));
        var outcome = await agent.ExecuteAsync(RuntimeFixture.Request(2), new InlineProgress(value =>
        { progress.Add(value); if (observerFailure) throw new InvalidOperationException("OBSERVER_CANARY"); }));
        Assert.Equal(observerFailure ? AgentTerminationReason.Failed : AgentTerminationReason.Completed, outcome.Reason);
        Assert.Equal(observerFailure ? AgentFailureCode.ProgressObserverFailed : AgentFailureCode.None, outcome.FailureCode);
        Assert.Equal(1, cap.Effects); Assert.Equal(observerFailure ? 1 : 2, outcome.CompletedWorkUnits);
        var ordinary = JsonSerializer.Serialize(outcome) + JsonSerializer.Serialize(progress) + JsonSerializer.Serialize(configuration.Describe()) + JsonSerializer.Serialize(RuntimeAgentFactory.Describe(configuration));
        foreach (var canary in new[] { "ARGUMENT_CANARY", "RESULT_CANARY", "REPLAY_CANARY", "OBSERVER_CANARY", "SCRIPTED_PRIVATE_CREDENTIAL_CANARY" })
            Assert.DoesNotContain(canary, ordinary);
        Assert.Equal(observerFailure ? 1 : 2, provider.Effects);
    }
}

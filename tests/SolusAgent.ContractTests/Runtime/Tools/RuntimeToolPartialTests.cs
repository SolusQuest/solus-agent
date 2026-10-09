using CustomTools;
using SolusAgent.Api.Execution;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Execution;
using SolusAgent.Runtime.Tools;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Tools;

public sealed class RuntimeToolPartialTests
{
    [Theory]
    [InlineData("throw")] [InlineData("failure")] [InlineData("schema")] [InlineData("association")]
    public async Task PublicFailureAfterFirstSuccessStopsWithoutRetryOrLaterEffects(string mode)
    {
        var counter = new CounterTool(maximumResultBytes: 64); var cap = new CounterCapability(); var transformCap = new TransformCapability();
        var transform = FailingTransform(mode);
        var provider = Script();
        var outcome = await ToolFixture.Agent(provider, ToolFixture.Bindings(counter, cap, transform, transformCap)).ExecuteAsync(RuntimeFixture.Request(3));
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(AgentFailureCode.ExecutionFailed, outcome.FailureCode);
        Assert.Equal(1, outcome.CompletedWorkUnits); Assert.Equal(1, cap.Effects); Assert.Equal(1, transformCap.Effects); Assert.Equal(1, provider.Effects);
        Assert.Equal(3, outcome.Usage!.Attempts.Single().Usage.InputTokens);
        ToolAllowanceTests.Counts(outcome.Usage, 2, 0, 1);
    }
    [Theory]
    [InlineData("throw", (int)ToolMemberState.Failed)] [InlineData("failure", (int)ToolMemberState.Failed)]
    [InlineData("schema", (int)ToolMemberState.Rejected)] [InlineData("association", (int)ToolMemberState.Rejected)]
    public async Task ActualOperationRetainsFirstGuardedResultFailedCurrentMemberAndUnstartedRemainder(string mode, int expected)
    {
        var counter = new CounterTool(maximumResultBytes: 64); var cap = new CounterCapability(); var transformCap = new TransformCapability();
        var options = new RuntimeOptions(); using var cut = new RunCut(options.TimeProvider, TimeSpan.FromSeconds(10), CancellationToken.None);
        var provider = Script(); var accepted = await ToolFixture.Accept(provider, ToolFixture.Bindings(counter, cap, FailingTransform(mode), transformCap), options, cut);
        var result = await ToolBatchOperation.ExecuteAsync(accepted.State, accepted.Request, accepted.Response);
        Assert.NotEqual(ToolError.None, result.Error); Assert.False(accepted.State.CanContinue);
        var records = accepted.State.ToolRecords;
        Assert.Equal(new[] { ToolMemberState.Succeeded, (ToolMemberState)expected, ToolMemberState.Unstarted }, records.Select(r => r.State));
        Assert.Equal("{\"total\":1}", records[0].Result!.Json); Assert.True(records[0].InvocationStarted);
        Assert.True(records[1].InvocationStarted); Assert.Null(records[1].Result!.Json); Assert.False(records[2].InvocationStarted); Assert.Null(records[2].Result);
        Assert.Equal(2, accepted.State.Records.Count(r => r.Kind == ProviderInputKind.ToolResultData));
        Assert.Equal(1, cap.Effects); Assert.Equal(1, transformCap.Effects); Assert.Equal(1, accepted.State.Completed);
        await ToolBatchOperation.ExecuteAsync(accepted.State, accepted.Request, accepted.Response);
        Assert.Equal(1, cap.Effects); Assert.Equal(1, transformCap.Effects); Assert.Equal(records, accepted.State.ToolRecords);
        ToolAllowanceTests.Counts(accepted.State.Usage(), 2, 0, 1);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PublicHeldToolReturnsBeforeReleaseAndNeverStartsRemainingMember(bool cancel)
    {
        var clock = new ControlledTimeProvider(); using var cancellation = new CancellationTokenSource();
        var entered = RuntimeFixture.Barrier<ToolCall>(); var output = RuntimeFixture.Barrier<ToolOutput>();
        var counter = new CounterTool(maximumResultBytes: 64); var cap = new CounterCapability(); var transformCap = new TransformCapability();
        var transform = new TransformTool(effect: (call, capability, token) =>
        { capability.Upper("effect", token); entered.SetResult(call); return new(output.Task); });
        var provider = Script(); var run = ToolFixture.Agent(provider, ToolFixture.Bindings(counter, cap, transform, transformCap), new(clock))
            .ExecuteAsync(RuntimeFixture.Request(3), cancellationToken: cancellation.Token).AsTask();
        var call = await RuntimeFixture.Await(entered.Task); if (cancel) cancellation.Cancel(); else clock.Advance(TimeSpan.FromSeconds(10));
        var outcome = await RuntimeFixture.Await(run); Assert.False(output.Task.IsCompleted);
        Assert.Equal(cancel ? AgentTerminationReason.Cancelled : AgentTerminationReason.ResourceLimit, outcome.Reason);
        Assert.Equal(1, outcome.CompletedWorkUnits); Assert.Equal(1, cap.Effects); Assert.Equal(1, transformCap.Effects); Assert.Equal(1, provider.Effects);
        Assert.Equal(3, outcome.Usage!.Attempts.Single().Usage.InputTokens);
        output.SetResult(ToolOutput.Success(call, "{\"text\":\"LATE_RESULT_CANARY\"}"));
        Assert.Equal(1, cap.Effects); Assert.Equal(1, outcome.CompletedWorkUnits); Assert.Equal(1, provider.Effects);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualCutSnapshotsKeepSucceededUncertainAndUnstartedWithoutLateRewrite(bool cancel)
    {
        var clock = new ControlledTimeProvider(); using var cancellation = new CancellationTokenSource(); var options = new RuntimeOptions(clock);
        using var cut = new RunCut(clock, TimeSpan.FromSeconds(10), cancellation.Token);
        var entered = RuntimeFixture.Barrier<ToolCall>(); var output = RuntimeFixture.Barrier<ToolOutput>();
        var counter = new CounterTool(maximumResultBytes: 64); var cap = new CounterCapability(); var transformCap = new TransformCapability();
        var transform = new TransformTool(effect: (call, capability, token) =>
        { capability.Upper("effect", token); entered.SetResult(call); return new(output.Task); });
        var accepted = await ToolFixture.Accept(Script(), ToolFixture.Bindings(counter, cap, transform, transformCap), options, cut);
        var run = ToolBatchOperation.ExecuteAsync(accepted.State, accepted.Request, accepted.Response).AsTask();
        var call = await RuntimeFixture.Await(entered.Task); if (cancel) cancellation.Cancel(); else clock.Advance(TimeSpan.FromSeconds(10));
        await RuntimeFixture.Await(run); Assert.False(output.Task.IsCompleted);
        var snapshot = accepted.State.ToolRecords;
        Assert.Equal(new[] { ToolMemberState.Succeeded, ToolMemberState.InvokedUnknown, ToolMemberState.Unstarted }, snapshot.Select(r => r.State));
        Assert.Equal("{\"total\":1}", snapshot[0].Result!.Json); Assert.True(snapshot[0].InvocationStarted);
        Assert.Null(snapshot[1].Result); Assert.Null(snapshot[1].InvocationStarted); Assert.False(snapshot[2].InvocationStarted);
        Assert.Single(accepted.State.Records, r => r.ToolResult is not null); Assert.False(accepted.State.CanContinue);
        output.SetResult(ToolOutput.Success(call, "{\"text\":\"LATE_CANARY\"}")); await output.Task;
        Assert.Equal(snapshot, accepted.State.ToolRecords); Assert.Equal(1, cap.Effects);
    }
    [Theory]
    [InlineData("pre")] [InlineData("settlement")] [InlineData("progress")]
    public async Task CallerCutsAtRealIntegrationBoundaries(string phase)
    {
        using var cancellation = new CancellationTokenSource(); var cap = new CounterCapability(); var transformCap = new TransformCapability();
        var counter = new CounterTool(maximumResultBytes: 64); var transform = new TransformTool(); var hooks = new RuntimeHooks();
        if (phase == "pre") cancellation.Cancel();
        if (phase == "settlement") hooks.After = (settlement, _) => { cancellation.Cancel(); return ValueTask.FromResult<SolusAgent.Runtime.Api.Exposure.SettlementAcknowledgement?>(RuntimeHooks.Continue(settlement)); };
        var provider = Script(); var outcome = await ToolFixture.Agent(provider, ToolFixture.Bindings(counter, cap, transform, transformCap), hooks: hooks)
            .ExecuteAsync(RuntimeFixture.Request(3), new InlineProgress(_ => { if (phase == "progress") cancellation.Cancel(); }), cancellation.Token);
        Assert.Equal(AgentTerminationReason.Cancelled, outcome.Reason); Assert.Equal(phase == "pre" ? 0 : 1, outcome.CompletedWorkUnits);
        Assert.Equal(phase == "progress" ? 2 : 0, cap.Effects); Assert.Equal(phase == "progress" ? 1 : 0, transformCap.Effects);
        Assert.Equal(phase == "pre" ? 0 : 1, provider.Effects);
    }
    private static ScriptedProvider Script() => new([(request, observation, _) => ToolFixture.Calls(request, observation,
        [ToolFixture.Counter("first"), ToolFixture.Transform("second"), ToolFixture.Counter("unstarted")]), ScriptedProvider.Final]);
    private static TransformTool FailingTransform(string mode) => new(effect: (call, capability, token) =>
    {
        capability.Upper("effect", token);
        return mode switch
        {
            "throw" => throw new InvalidOperationException("PRIVATE_TOOL_EXCEPTION_CANARY"),
            "failure" => ValueTask.FromResult(ToolOutput.Failure(call)),
            "schema" => ValueTask.FromResult(ToolOutput.Success(call, "{\"text\":1}")),
            _ => ValueTask.FromResult(ToolOutput.Success(new("foreign", call.ToolName, call.ArgumentsJson), "{\"text\":\"data\"}")),
        };
    });
}

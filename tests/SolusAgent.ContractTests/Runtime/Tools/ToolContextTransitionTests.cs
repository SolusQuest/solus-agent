using System.Text.Json;
using System.Text.Json.Nodes;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Tools;

public sealed class ToolContextTransitionTests
{
    [Fact]
    public async Task MultipleBatchesAcrossRoundsThenClosedFinalAndNewRunPreserveHistoricalEffects()
    {
        var source = await ToolContextFixture.Capture(); var sink = new RestrictedContextHost();
        var secondProvider = new PersistentProvider(tools: true) { Respond = r => ToolContextFixture.Calls(r, "second") };
        var request = ToolContextFixture.Request();
        var second = await ToolContextFixture.Agent(secondProvider, source.Tools, new SelectedAuthority(source.Envelope, source.Result.Checkpoint!))
            .ExecuteWithContextAsync(new(request, ContextExecutionIntent.ContinueRun, source.Envelope, OrdinaryFixture.Grant(source.Result, request.ExecutionId)), sink);
        Assert.Equal(ContextAdmission.Supplied, second.Admission); Assert.Equal(2, second.Outcome!.Usage!.ToolInvocations!.Invoked);
        Assert.Equal(2, source.Counter.Effects); Assert.Equal(2, source.Transform.Effects);
        var bytes = sink.CopyRestrictedContext(); var finalProvider = new PersistentProvider(tools: true) { Respond = ToolContextFixture.Final };
        var finalRequest = ToolContextFixture.Request(); var finalSink = new RestrictedContextHost();
        var final = await ToolContextFixture.Agent(finalProvider, source.Tools, new SelectedAuthority(bytes, second.Checkpoint!))
            .ExecuteWithContextAsync(new(finalRequest, ContextExecutionIntent.ContinueRun, bytes, OrdinaryFixture.Grant(second, finalRequest.ExecutionId)), finalSink);
        Assert.Equal(ContextAdmission.Supplied, final.Admission); Assert.Equal(AgentTerminationReason.Completed, final.Outcome!.Reason);
        Assert.Equal(2, final.History.Count); Assert.Equal(4, final.History.Sum(r => r.Usage.ToolInvocations!.Invoked));
        Assert.Equal(0, final.Outcome.Usage!.ToolInvocations!.Invoked); Assert.Equal(2, source.Counter.Effects);
        Assert.Equal(new[] { "Counter", "Transform", "Countersecond", "Transformsecond" }, finalProvider.LastRequest!.Inputs
            .Where(i => i.ToolResult is not null).Select(i => i.ToolResult!.Call.CallId));
        var closed = finalSink.CopyRestrictedContext(); var nextProvider = new PersistentProvider(tools: true) { Respond = ToolContextFixture.Final };
        var newTask = ToolContextFixture.Request(instructions: "NEW_CURRENT_INSTRUCTIONS");
        var nextRequest = new AgentRequest(newTask.ExecutionId, newTask.Instructions, [new(AgentInputSource.Repository, "NEW_DATA_CANARY")],
            newTask.Bounds, newTask.RequiredCapabilities, newTask.UsageLimits);
        var next = await ToolContextFixture.Agent(nextProvider, source.Tools, new SelectedAuthority(closed, final.Checkpoint!))
            .ExecuteWithContextAsync(new(nextRequest, ContextExecutionIntent.NewRunFromContext, closed, OrdinaryFixture.Grant(final, nextRequest.ExecutionId)));
        Assert.Equal(ContextAdmission.Supplied, next.Admission); Assert.Equal(AgentTerminationReason.Completed, next.Outcome!.Reason);
        Assert.Equal("NEW_CURRENT_INSTRUCTIONS", nextProvider.LastRequest!.Inputs[0].Text);
        Assert.Equal("NEW_DATA_CANARY", nextProvider.LastRequest.Inputs[^1].Text);
        Assert.Equal(ProviderInputKind.InputData, nextProvider.LastRequest.Inputs[^1].Kind);
        Assert.Equal(3, next.History.Count); Assert.Equal(2, source.Counter.Effects); Assert.Equal(2, source.Transform.Effects);
        Assert.Null(nextProvider.LastRequest.History!.OperationOrigin);
    }

    [Fact]
    public async Task MultipleBatchesInsideOneRoundValidateActualMixedModelGraph()
    {
        var source = await ToolContextFixture.Capture(); var provider = new PersistentProvider(tools: true);
        provider.Respond = r => provider.Effects < 3 ? ToolContextFixture.Calls(r, provider.Effects.ToString()) : ToolContextFixture.Final(r);
        var request = ToolContextFixture.Request(3, new(maximumPhysicalDispatches: 3, maximumToolInvocations: 4)); var sink = new RestrictedContextHost();
        var result = await ToolContextFixture.Agent(provider, source.Tools, new SelectedAuthority(source.Envelope, source.Result.Checkpoint!))
            .ExecuteWithContextAsync(new(request, ContextExecutionIntent.ContinueRun, source.Envelope, OrdinaryFixture.Grant(source.Result, request.ExecutionId)), sink);
        Assert.Equal(AgentTerminationReason.Completed, result.Outcome!.Reason); Assert.Equal(3, result.Outcome.CompletedWorkUnits);
        Assert.Equal(4, result.Outcome.Usage!.ToolInvocations!.Invoked); Assert.Equal(3, source.Counter.Effects);
        var bytes = sink.CopyRestrictedContext(); var next = ToolContextFixture.Request(); var lastProvider = new PersistentProvider(tools: true);
        var restored = await ToolContextFixture.Agent(lastProvider, source.Tools, new SelectedAuthority(bytes, result.Checkpoint!))
            .ExecuteWithContextAsync(new(next, ContextExecutionIntent.NewRunFromContext, bytes, OrdinaryFixture.Grant(result, next.ExecutionId)));
        Assert.Equal(ContextAdmission.Supplied, restored.Admission); Assert.Equal(AgentTerminationReason.Completed, restored.Outcome!.Reason);
        Assert.Equal(3, source.Counter.Effects); Assert.Equal(3, source.Transform.Effects);
    }

    [Fact]
    public async Task ZeroAttemptMiddleRoundRetainsToolCursorAndProviderState()
    {
        var source = await ToolContextFixture.Capture(); var clock = new ControlledTimeProvider();
        clock.TimerCreated = _ => { clock.TimerCreated = null; clock.Advance(TimeSpan.FromSeconds(30)); };
        var provider = new PersistentProvider(tools: true); var request = ToolContextFixture.Request(); var sink = new RestrictedContextHost();
        var middle = await ToolContextFixture.Agent(provider, source.Tools, new SelectedAuthority(source.Envelope, source.Result.Checkpoint!),
            new(timeProvider: clock, requireContinuation: true)).ExecuteWithContextAsync(
            new(request, ContextExecutionIntent.ContinueRun, source.Envelope, OrdinaryFixture.Grant(source.Result, request.ExecutionId)), sink);
        Assert.Equal(ContextCaptureStatus.Delivered, middle.CaptureStatus); Assert.Equal(AgentTerminationReason.ResourceLimit, middle.Outcome!.Reason);
        Assert.Empty(middle.Outcome.Usage!.Attempts); Assert.Equal(0, provider.Effects);
        var bytes = sink.CopyRestrictedContext(); var lastProvider = new PersistentProvider(tools: true); var lastRequest = ToolContextFixture.Request();
        var last = await ToolContextFixture.Agent(lastProvider, source.Tools, new SelectedAuthority(bytes, middle.Checkpoint!)).ExecuteWithContextAsync(
            new(lastRequest, ContextExecutionIntent.ContinueRun, bytes, OrdinaryFixture.Grant(middle, lastRequest.ExecutionId)));
        Assert.Equal(ContextAdmission.Supplied, last.Admission); Assert.Equal(AgentTerminationReason.Completed, last.Outcome!.Reason);
        Assert.Equal(2, last.History.Count); Assert.Empty(last.History[1].Usage.Attempts);
        Assert.Equal(1, source.Counter.Effects); Assert.Equal(1, source.Transform.Effects); Assert.Equal(0, last.Outcome.Usage!.ToolInvocations!.Invoked);
    }

    [Theory]
    [InlineData("observer")]
    [InlineData("cancel")]
    [InlineData("duration")]
    public async Task CutsAfterAllResultCommitsPreserveSafeBoundary(string mode)
    {
        using var cancellation = new CancellationTokenSource(); var clock = new ControlledTimeProvider();
        var source = await ToolContextFixture.Capture(options: new(timeProvider: clock, requireContinuation: true), afterBatch: () =>
        {
            if (mode == "observer") throw new InvalidOperationException("PRIVATE_OBSERVER_CANARY");
            if (mode == "cancel") cancellation.Cancel();
            if (mode == "duration") clock.Advance(TimeSpan.FromSeconds(30));
        }, cancellationToken: cancellation.Token);
        var request = ToolContextFixture.Request(); var provider = new PersistentProvider(tools: true);
        var result = await ToolContextFixture.Agent(provider, source.Tools, new SelectedAuthority(source.Envelope, source.Result.Checkpoint!))
            .ExecuteWithContextAsync(new(request, ContextExecutionIntent.ContinueRun, source.Envelope, OrdinaryFixture.Grant(source.Result, request.ExecutionId)));
        Assert.Equal(ContextAdmission.Supplied, result.Admission); Assert.Equal(AgentTerminationReason.Completed, result.Outcome!.Reason);
        Assert.Equal(1, source.Counter.Effects); Assert.Equal(1, source.Transform.Effects);
    }

    [Theory]
    [InlineData(4, 64, false)]
    [InlineData(5, 64, true)]
    [InlineData(64, 1, false)]
    [InlineData(64, 2, true)]
    public async Task RetainedRecordAndAttemptLimitsRemainCumulative(int records, int attempts, bool admitted)
    {
        var source = await ToolContextFixture.Capture(); var request = ToolContextFixture.Request();
        var provider = new PersistentProvider(tools: true); var authority = new SelectedAuthority(source.Envelope, source.Result.Checkpoint!);
        var result = await ToolContextFixture.Agent(provider, source.Tools, authority, new(maximumAttempts: attempts, maximumRecords: records, requireContinuation: true))
            .ExecuteWithContextAsync(new(request, ContextExecutionIntent.ContinueRun, source.Envelope, OrdinaryFixture.Grant(source.Result, request.ExecutionId)));
        Assert.Equal(admitted ? ContextAdmission.Supplied : ContextAdmission.Rejected, result.Admission);
        Assert.Equal(admitted ? 1 : 0, authority.Claims); Assert.Equal(admitted ? 1 : 0, provider.Effects);
        Assert.Equal(1, source.Counter.Effects);
    }

    [Fact]
    public async Task CompleteProviderResultCollectionStillAllowsExactIdentityReordering()
    {
        var source = await ToolContextFixture.Capture(); var request = ToolContextFixture.Request(); var provider = new PersistentProvider(tools: true);
        var result = await ToolContextFixture.Agent(provider, source.Tools, new SelectedAuthority(source.Envelope, source.Result.Checkpoint!))
            .ExecuteWithContextAsync(new(request, ContextExecutionIntent.ContinueRun, source.Envelope, OrdinaryFixture.Grant(source.Result, request.ExecutionId)));
        Assert.Equal(ContextAdmission.Supplied, result.Admission); var actual = provider.LastRequest!;
        var reordered = actual.Inputs.Take(2).Concat(actual.Inputs.Skip(2).Reverse()).ToArray();
        var accepted = new ProviderRequest(actual.Scope, new(actual.Attempt.ExecutionId, Guid.NewGuid(), Guid.NewGuid(), 1), reordered,
            actual.Tools, actual.Continuation, actual.RequiredCapabilities, actual.Bounds, actual.History);
        Assert.Equal("Transform", accepted.Inputs[2].ToolResult!.Call.CallId); Assert.Equal("Counter", accepted.Inputs[3].ToolResult!.Call.CallId);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("instructions")]
    [InlineData("quota")]
    [InlineData("permission")]
    public async Task CurrentHostControlsRemainAuthoritative(string control)
    {
        var source = await ToolContextFixture.Capture(); var provider = new PersistentProvider(tools: true) { Respond = r => ToolContextFixture.Calls(r, "new") };
        var current = ToolContextFixture.Request(2, new(maximumPhysicalDispatches: 2, maximumToolInvocations: 1));
        if (control == "instructions") current = new(current.ExecutionId, "DIFFERENT", [], current.Bounds, current.RequiredCapabilities, current.UsageLimits);
        if (control == "input") current = new(current.ExecutionId, current.Instructions, [new(AgentInputSource.Repository, "new")], current.Bounds, current.RequiredCapabilities, current.UsageLimits);
        var hooks = new RuntimeHooks(); if (control == "permission") hooks.Before = (exposure, _) => ValueTask.FromResult<SolusAgent.Runtime.Api.Exposure.ExposureAcknowledgement?>(null);
        var result = await ToolContextFixture.Agent(provider, source.Tools, new SelectedAuthority(source.Envelope, source.Result.Checkpoint!), hooks: hooks)
            .ExecuteWithContextAsync(new(current, ContextExecutionIntent.ContinueRun, source.Envelope, OrdinaryFixture.Grant(source.Result, current.ExecutionId)));
        Assert.Equal(control is "input" or "instructions" ? ContextAdmission.Rejected : ContextAdmission.Supplied, result.Admission);
        Assert.Equal(control == "quota" ? 1 : 0, provider.Effects); Assert.Equal(1, source.Counter.Effects); Assert.Equal(1, source.Transform.Effects);
        if (control == "quota") Assert.Equal(0, result.Outcome!.Usage!.ToolInvocations!.Invoked);
    }
}

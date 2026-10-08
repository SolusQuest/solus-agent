using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Execution;

public sealed class CutAndLateWorkTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HeldExposureReturnsBeforeReleaseAndLatePermissionNeverDispatches(bool cancel)
    {
        var clock = new ControlledTimeProvider(); using var cancellation = new CancellationTokenSource();
        var entered = RuntimeFixture.Barrier<RuntimeExposure>(); var permission = RuntimeFixture.Barrier<ExposureAcknowledgement?>();
        var hooks = new RuntimeHooks { Before = (exposure, _) => { entered.SetResult(exposure); return new(permission.Task); } };
        var provider = new ScriptedProvider([ScriptedProvider.Final]);
        var run = RuntimeFixture.Agent(provider, hooks, new(clock)).ExecuteAsync(RuntimeFixture.Request(), cancellationToken: cancellation.Token).AsTask();
        var exposure = await RuntimeFixture.Await(entered.Task); Assert.Equal(0, provider.Effects);
        if (cancel) cancellation.Cancel(); else clock.Advance(TimeSpan.FromSeconds(10));
        var outcome = await RuntimeFixture.Await(run);
        Assert.False(permission.Task.IsCompleted); Assert.Equal(cancel ? AgentTerminationReason.Cancelled : AgentTerminationReason.ResourceLimit, outcome.Reason);
        Assert.Equal(0, outcome.CompletedWorkUnits); Assert.Equal(0, provider.Effects);
        var settlement = hooks.Settlements.Single(); Assert.False(settlement.ProviderInvoked); Assert.Null(settlement.ProviderOutcome);
        Assert.Equal(cancel ? RuntimeStop.Cancelled : RuntimeStop.DurationLimit, settlement.Stop);
        Assert.Equal(DispatchExposure.NotDispatched, settlement.Observation.Exposure);
        permission.SetResult(RuntimeHooks.Permit(exposure)); await permission.Task;
        Assert.Equal(0, provider.Effects); Assert.Single(hooks.Settlements);
    }

    [Theory]
    [InlineData(false, "known")] [InlineData(true, "known")]
    [InlineData(false, "partial")] [InlineData(true, "partial")]
    [InlineData(false, "zero")] [InlineData(true, "zero")]
    [InlineData(false, "missing")] [InlineData(true, "missing")]
    public async Task NonCooperativeProviderPreservesCutSnapshotBeforeRelease(bool cancel, string usage)
    {
        var clock = new ControlledTimeProvider(); using var cancellation = new CancellationTokenSource();
        var entered = RuntimeFixture.Barrier<(ProviderRequest Request, ProviderObservation Observation)>();
        var response = RuntimeFixture.Barrier<ProviderResponse>();
        var provider = new ScriptedProvider([(request, observation, _) =>
        {
            if (usage != "missing") observation.CaptureUsage(usage == "zero" ? new(0, 0) : usage == "partial" ? new(7, null) : new(7, 4));
            entered.SetResult((request, observation)); return new(response.Task);
        }]);
        var hooks = new RuntimeHooks();
        var run = RuntimeFixture.Agent(provider, hooks, new(clock)).ExecuteAsync(RuntimeFixture.Request(), cancellationToken: cancellation.Token).AsTask();
        var active = await RuntimeFixture.Await(entered.Task);
        if (cancel) cancellation.Cancel(); else clock.Advance(TimeSpan.FromSeconds(10));
        // The cutoff itself seals capture, rather than waiting for the runtime continuation or provider return.
        Assert.Equal(ProviderError.ObservationClosed, Assert.Throws<ProviderContractException>(() => active.Observation.CaptureUsage(new())).Error);
        var outcome = await RuntimeFixture.Await(run);
        Assert.False(response.Task.IsCompleted); Assert.Equal(cancel ? AgentTerminationReason.Cancelled : AgentTerminationReason.ResourceLimit, outcome.Reason);
        var fact = outcome.Usage!.Attempts.Single(); Assert.Equal(DispatchExposure.Dispatched, fact.Exposure);
        Assert.Equal(usage == "missing" ? null : usage == "zero" ? 0L : 7L, fact.Usage.InputTokens);
        Assert.Equal(usage is "missing" or "partial" ? null : usage == "zero" ? 0L : 4L, fact.Usage.OutputTokens);
        var settlement = hooks.Settlements.Single(); Assert.True(settlement.ProviderInvoked); Assert.Null(settlement.ProviderOutcome);
        Assert.Same(fact, settlement.Observation); Assert.Equal(0, outcome.CompletedWorkUnits);
        response.SetResult(RuntimeFixture.Final(active.Request, "LATE_MODEL_CANARY")); await response.Task;
        Assert.Equal(0, outcome.CompletedWorkUnits); Assert.Equal(1, provider.Effects); Assert.Single(hooks.Settlements);
        Assert.Same(fact, outcome.Usage.Attempts[0]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task UnknownInterfaceDispatchAndLateFaultStayUnknown(bool cancel)
    {
        var clock = new ControlledTimeProvider(); using var cancellation = new CancellationTokenSource();
        var entered = RuntimeFixture.Barrier(); var pending = RuntimeFixture.Barrier<ProviderExchangeResult>();
        var provider = new InterfaceScriptedProvider(new("p", "m"), (_, _) => { entered.SetResult(); return new(pending.Task); });
        var hooks = new RuntimeHooks(); var run = RuntimeFixture.Agent(provider, hooks, new(clock)).ExecuteAsync(RuntimeFixture.Request(), cancellationToken: cancellation.Token).AsTask();
        await RuntimeFixture.Await(entered.Task);
        if (cancel) cancellation.Cancel(); else clock.Advance(TimeSpan.FromSeconds(10));
        var outcome = await RuntimeFixture.Await(run);
        Assert.Equal(DispatchExposure.Unknown, outcome.Usage!.Attempts.Single().Exposure);
        Assert.Equal(UsageCompleteness.Unavailable, outcome.Usage.Attempts[0].Usage.Completeness);
        Assert.Null(hooks.Settlements.Single().ProviderOutcome);
        pending.SetException(new InvalidOperationException("LATE_FAULT_CANARY"));
        Assert.Equal(1, provider.Calls); Assert.Equal(0, outcome.CompletedWorkUnits);
    }

    [Theory]
    [InlineData(9, AgentTerminationReason.Completed)]
    [InlineData(10, AgentTerminationReason.ResourceLimit)]
    [InlineData(11, AgentTerminationReason.ResourceLimit)]
    public async Task DeadlineNeighborsRejectAtAndAfterEvenSynchronousProviderReturn(int seconds, AgentTerminationReason reason)
    {
        var clock = new ControlledTimeProvider(); var hooks = new RuntimeHooks();
        var provider = new ScriptedProvider([(request, observation, _) =>
        {
            observation.CaptureUsage(new(3, 2)); clock.Advance(TimeSpan.FromSeconds(seconds));
            return ValueTask.FromResult(RuntimeFixture.Final(request));
        }]);
        var outcome = await RuntimeFixture.Agent(provider, hooks, new(clock)).ExecuteAsync(RuntimeFixture.Request());
        Assert.Equal(reason, outcome.Reason); Assert.Equal(seconds < 10 ? 1 : 0, outcome.CompletedWorkUnits);
        Assert.Equal(3, outcome.Usage!.Attempts[0].Usage.InputTokens); Assert.Single(hooks.Settlements);
    }

    [Fact]
    public async Task ExposureAndProviderShareOneDeadlineAndCallerWinsWhenObservableAtCut()
    {
        var clock = new ControlledTimeProvider(); using var cancellation = new CancellationTokenSource();
        var hooks = new RuntimeHooks { Before = (exposure, _) =>
        { clock.Advance(TimeSpan.FromSeconds(7)); return ValueTask.FromResult<ExposureAcknowledgement?>(RuntimeHooks.Permit(exposure)); } };
        var entered = RuntimeFixture.Barrier(); var response = RuntimeFixture.Barrier<ProviderResponse>();
        var provider = new ScriptedProvider([(_, observation, _) => { observation.CaptureUsage(new(3, 2)); entered.SetResult(); return new(response.Task); }]);
        var run = RuntimeFixture.Agent(provider, hooks, new(clock)).ExecuteAsync(RuntimeFixture.Request(), cancellationToken: cancellation.Token).AsTask();
        await RuntimeFixture.Await(entered.Task); clock.Advance(TimeSpan.FromSeconds(2)); Assert.False(run.IsCompleted);
        cancellation.Cancel(); clock.Advance(TimeSpan.FromSeconds(1));
        var outcome = await RuntimeFixture.Await(run); Assert.Equal(AgentTerminationReason.Cancelled, outcome.Reason);
        Assert.Equal(RuntimeStop.Cancelled, hooks.Settlements.Single().Stop);
        response.SetResult(RuntimeFixture.Final(provider.LastRequest!));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HeldSettlementHasFiniteInjectedClockWaitAndLateContinueCannotReopen(bool cancel)
    {
        var clock = new ControlledTimeProvider(); using var cancellation = new CancellationTokenSource();
        var entered = RuntimeFixture.Barrier<RuntimeSettlement>(); var receipt = RuntimeFixture.Barrier<SettlementAcknowledgement?>();
        var hooks = new RuntimeHooks { After = (settlement, _) => { entered.SetResult(settlement); return new(receipt.Task); } };
        var provider = new ScriptedProvider([(request, observation, token) =>
        { clock.Advance(TimeSpan.FromSeconds(9)); return ScriptedProvider.Final(request, observation, token); }]);
        var run = RuntimeFixture.Agent(provider, hooks, new(clock)).ExecuteAsync(RuntimeFixture.Request(), cancellationToken: cancellation.Token).AsTask();
        var settlement = await RuntimeFixture.Await(entered.Task);
        if (cancel) cancellation.Cancel(); clock.Advance(TimeSpan.FromSeconds(1));
        var outcome = await RuntimeFixture.Await(run); Assert.False(receipt.Task.IsCompleted);
        Assert.Equal(cancel ? AgentTerminationReason.Cancelled : AgentTerminationReason.ResourceLimit, outcome.Reason);
        Assert.Equal(1, outcome.CompletedWorkUnits); Assert.Equal(3, outcome.Usage!.Attempts[0].Usage.InputTokens);
        receipt.SetResult(RuntimeHooks.Continue(settlement)); Assert.Equal(1, provider.Effects);
    }

    [Fact]
    public async Task CutClosureGraceIsFiniteAndDoesNotWaitForCancellationRegistrations()
    {
        var clock = new ControlledTimeProvider(); using var cancellation = new CancellationTokenSource(); using var release = new ManualResetEventSlim();
        var entered = RuntimeFixture.Barrier(); var callbackEntered = RuntimeFixture.Barrier(); var closureEntered = RuntimeFixture.Barrier();
        var response = RuntimeFixture.Barrier<ProviderResponse>(); var receipt = RuntimeFixture.Barrier<SettlementAcknowledgement?>();
        CancellationTokenRegistration registration = default;
        var provider = new ScriptedProvider([(_, observation, token) =>
        {
            registration = token.Register(() => { callbackEntered.TrySetResult(); release.Wait(); });
            observation.CaptureUsage(new(3, 2)); entered.SetResult(); return new(response.Task);
        }]);
        var hooks = new RuntimeHooks { After = (_, _) => { closureEntered.SetResult(); return new(receipt.Task); } };
        var run = RuntimeFixture.Agent(provider, hooks, new(clock, settlementGrace: TimeSpan.FromSeconds(2))).ExecuteAsync(RuntimeFixture.Request(), cancellationToken: cancellation.Token).AsTask();
        await RuntimeFixture.Await(entered.Task); cancellation.Cancel(); await RuntimeFixture.Await(closureEntered.Task);
        clock.Advance(TimeSpan.FromSeconds(2));
        try
        {
            var outcome = await RuntimeFixture.Await(run); Assert.Equal(AgentTerminationReason.Cancelled, outcome.Reason);
            Assert.Equal(3, outcome.Usage!.Attempts[0].Usage.InputTokens); Assert.False(response.Task.IsCompleted);
        }
        finally { release.Set(); registration.Dispose(); response.TrySetResult(RuntimeFixture.Final(provider.LastRequest!)); receipt.TrySetResult(null); }
    }
}

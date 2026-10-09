using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Execution;

public sealed class RetryCutTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ProviderAndSettlementTimeReduceTheSameBackoffDeadline(bool candidate)
    {
        var clock = new ControlledTimeProvider(); var waiting = RuntimeFixture.Barrier();
        clock.TimerCreated = due => { if (due == TimeSpan.FromSeconds(8)) waiting.TrySetResult(); };
        var provider = new ScriptedProvider([(_, o, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(1)); o.CaptureUsage(new(3, 2));
            throw new ProviderFailureException(new(ProviderRetryKind.Transient));
        }, ScriptedProvider.Final]);
        var hooks = new RuntimeHooks { After = (s, _) =>
        { clock.Advance(TimeSpan.FromSeconds(2)); return ValueTask.FromResult<SettlementAcknowledgement?>(RuntimeHooks.Continue(s)); } };
        var pending = AccountingTests.Execute(AccountingTests.Agent(provider, hooks, options: new(clock, settlementGrace: TimeSpan.FromSeconds(3))),
            RetryTests.Request(new(retryPolicy: new(2, TimeSpan.FromSeconds(8)))), candidate);
        await RuntimeFixture.Await(waiting.Task); clock.Advance(TimeSpan.FromSeconds(7));
        var result = await RuntimeFixture.Await(pending);
        Assert.Equal(AgentTerminationReason.ResourceLimit, result.Reason); Assert.Equal(1, provider.Effects);
        Assert.Equal(TimeSpan.FromSeconds(10).Ticks, clock.GetTimestamp());
    }

    [Theory]
    [InlineData(false, false, 1)] [InlineData(true, false, 1)]
    [InlineData(false, true, 3)] [InlineData(true, true, 3)]
    public async Task BackoffUsesInjectedClockAndHonorsOnlyAdmittedHint(bool candidate, bool honor, int seconds)
    {
        var clock = new ControlledTimeProvider(); var waiting = RuntimeFixture.Barrier();
        clock.TimerCreated = due => { if (due == TimeSpan.FromSeconds(seconds)) waiting.TrySetResult(); };
        var provider = new ScriptedProvider([ScriptedProvider.Failure(new(ProviderRetryKind.Throttled, TimeSpan.FromSeconds(3))), ScriptedProvider.Final]);
        var pending = AccountingTests.Execute(AccountingTests.Agent(provider, new RuntimeHooks(), options: new(clock)),
            RetryTests.Request(new(retryPolicy: new(2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), honor))), candidate);
        await RuntimeFixture.Await(waiting.Task);
        clock.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromTicks(1));
        Assert.Equal(1, provider.Effects); Assert.False(pending.IsCompleted);
        clock.Advance(TimeSpan.FromTicks(1));
        var result = await RuntimeFixture.Await(pending);
        Assert.Equal(AgentTerminationReason.Completed, result.Reason); Assert.Equal(2, provider.Effects);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HintBeyondHostCeilingDeclinesWithoutWaiting(bool candidate)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Failure(new(ProviderRetryKind.Throttled, TimeSpan.FromSeconds(3))), ScriptedProvider.Final]);
        var clock = new ControlledTimeProvider();
        var result = await AccountingTests.Execute(AccountingTests.Agent(provider, null, options: new(clock)),
            RetryTests.Request(new(retryPolicy: new(2, maximumDelay: TimeSpan.FromSeconds(2), honorRetryAfter: true))), candidate);
        Assert.Equal(AgentTerminationReason.Failed, result.Reason); Assert.Equal(1, provider.Effects);
        Assert.Equal(0, clock.GetTimestamp());
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task CancellationOrExactDeadlineDuringBackoffCannotDispatch(bool candidate, bool cancel)
    {
        var clock = new ControlledTimeProvider(); var waiting = RuntimeFixture.Barrier();
        using var cancellation = new CancellationTokenSource();
        clock.TimerCreated = due => { if (due == TimeSpan.FromSeconds(2)) waiting.TrySetResult(); };
        var provider = new ScriptedProvider([ScriptedProvider.Failure(new(ProviderRetryKind.Transient)), ScriptedProvider.Final]);
        var pending = AccountingTests.Execute(AccountingTests.Agent(provider, null, options: new(clock)),
            RetryTests.Request(new(retryPolicy: new(2, TimeSpan.FromSeconds(2))), duration: TimeSpan.FromSeconds(2)), candidate, cancellation.Token);
        await RuntimeFixture.Await(waiting.Task);
        if (cancel) cancellation.Cancel(); else clock.Advance(TimeSpan.FromSeconds(2));
        var result = await RuntimeFixture.Await(pending);
        Assert.Equal(cancel ? AgentTerminationReason.Cancelled : AgentTerminationReason.ResourceLimit, result.Reason);
        Assert.Single(result.Usage!.Attempts); Assert.Equal(1, provider.Effects);
        clock.Advance(TimeSpan.FromMinutes(1)); Assert.Equal(1, provider.Effects);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BackoffCutDoesNotWaitForUncooperativeCancellationCallback(bool candidate)
    {
        var clock = new ControlledTimeProvider(); var waiting = RuntimeFixture.Barrier();
        var callbackEntered = RuntimeFixture.Barrier(); var release = RuntimeFixture.Barrier();
        using var caller = new CancellationTokenSource(); CancellationTokenRegistration registration = default;
        clock.TimerCreated = due => { if (due == TimeSpan.FromSeconds(2)) waiting.TrySetResult(); };
        var provider = new ScriptedProvider([(_, o, token) =>
        {
            registration = token.Register(() => { callbackEntered.SetResult(); release.Task.GetAwaiter().GetResult(); });
            o.CaptureUsage(new(3, 2)); throw new ProviderFailureException(new(ProviderRetryKind.Transient));
        }, ScriptedProvider.Final]);
        try
        {
            var pending = AccountingTests.Execute(AccountingTests.Agent(provider, null, options: new(clock)),
                RetryTests.Request(new(retryPolicy: new(2, TimeSpan.FromSeconds(2)))), candidate, caller.Token);
            await RuntimeFixture.Await(waiting.Task); caller.Cancel(); await RuntimeFixture.Await(callbackEntered.Task);
            var result = await RuntimeFixture.Await(pending);
            Assert.Equal(AgentTerminationReason.Cancelled, result.Reason); Assert.False(release.Task.IsCompleted); Assert.Equal(1, provider.Effects);
        }
        finally { release.TrySetResult(); await registration.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task LateProviderSuccessOrRetryableFailureCannotReopenRun(bool candidate, bool failure)
    {
        var clock = new ControlledTimeProvider(); var entered = RuntimeFixture.Barrier<ProviderRequest>(); var release = RuntimeFixture.Barrier();
        Task<ProviderExchangeResult>? physical = null;
        var provider = new ScriptedProvider([ScriptedProvider.Failure(new(ProviderRetryKind.Transient)), async (r, o, _) =>
        {
            o.CaptureUsage(new(3, 2)); entered.SetResult(r); await release.Task;
            if (failure) throw new ProviderFailureException(new(ProviderRetryKind.Transient));
            return RuntimeFixture.Final(r);
        }, ScriptedProvider.Final]);
        var forwarding = new InterfaceScriptedProvider(provider.Scope, (r, t) => new(physical = provider.ExchangeAsync(r, t).AsTask()));
        var pending = AccountingTests.Execute(AccountingTests.Agent(forwarding, new RuntimeHooks(), options: new(clock)), RetryTests.Request(), candidate);
        var heldRequest = await RuntimeFixture.Await(entered.Task); clock.Advance(TimeSpan.FromSeconds(10));
        var result = await RuntimeFixture.Await(pending);
        Assert.Equal(AgentTerminationReason.ResourceLimit, result.Reason); Assert.False(release.Task.IsCompleted);
        Assert.Equal(2, result.Usage!.Attempts.Count); Assert.Equal(6, result.Usage.InputTokens.ObservedTokens);
        Assert.Throws<ProviderContractException>(() => heldRequest.Observation.CaptureUsage(new(99, 99)));
        release.SetResult(); await RuntimeFixture.Await(physical!);
        Assert.Equal(2, forwarding.Calls); Assert.Equal(2, provider.Effects); Assert.Equal(0, result.CompletedWorkUnits);
        Assert.Equal(6, result.Usage.InputTokens.ObservedTokens);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task HeldSettlementMustFinishBeforeRetryAndLateContinueHasNoAuthority(bool candidate, bool expire)
    {
        var clock = new ControlledTimeProvider(); var entered = RuntimeFixture.Barrier<RuntimeSettlement>();
        var release = RuntimeFixture.Barrier<SettlementAcknowledgement?>();
        var provider = new ScriptedProvider([ScriptedProvider.Failure(new(ProviderRetryKind.Transient)), ScriptedProvider.Final]);
        var hooks = new RuntimeHooks { After = (s, _) =>
        {
            if (s.Exposure.Attempt.AttemptNumber > 1) return ValueTask.FromResult<SettlementAcknowledgement?>(RuntimeHooks.Continue(s));
            entered.SetResult(s); return new(release.Task);
        } };
        var pending = AccountingTests.Execute(AccountingTests.Agent(provider, hooks, options: new(clock)),
            RetryTests.Request(new(accountingPolicy: new(new(5, 4), 20, 20), retryPolicy: new(2))), candidate);
        var settlement = await RuntimeFixture.Await(entered.Task);
        Assert.Equal(3, settlement.Accounting!.Input.MeasuredTokens); Assert.Equal(0, settlement.Accounting.Input.ReservedTokens);
        Assert.Equal(1, provider.Effects); Assert.Single(hooks.Exposures);
        if (expire) clock.Advance(TimeSpan.FromSeconds(1)); else release.SetResult(RuntimeHooks.Continue(settlement));
        var result = await RuntimeFixture.Await(pending);
        Assert.Equal(expire ? AgentTerminationReason.Failed : AgentTerminationReason.Completed, result.Reason);
        if (expire) { Assert.False(release.Task.IsCompleted); release.SetResult(RuntimeHooks.Continue(settlement)); }
        Assert.Equal(expire ? 1 : 2, provider.Effects); Assert.Equal(expire ? 1 : 2, result.Usage!.Attempts.Count);
    }
}

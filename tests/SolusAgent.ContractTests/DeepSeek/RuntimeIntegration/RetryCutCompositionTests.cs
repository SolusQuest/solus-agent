using System.Net;
using System.Text;
using System.Text.Json;
using CustomTools;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ContractTests.DeepSeek.Adapter;
using SolusAgent.ContractTests.Runtime.Candidates;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.RuntimeIntegration;

public sealed class RetryCutCompositionTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task HttpRetryAfterAndHostBackoffWaitBeforeReservingAndSending(bool candidate, bool honor)
    {
        var clock = new ControlledTimeProvider(); var waiting = RuntimeFixture.Barrier();
        var hooks = new RuntimeHooks();
        var delay = TimeSpan.FromSeconds(honor ? 3 : 1);
        // The one-second closure timer is created before the hook enters. Signal only the later backoff timer.
        clock.TimerCreated = due => { if (due == delay && hooks.Settlements.Count == 1) waiting.TrySetResult(); };
        var count = 0; var sentAt = new List<long>();
        using var handler = new FakeHandler((_, _) =>
        {
            sentAt.Add(clock.GetTimestamp());
            var response = ++count == 1 ? AdapterFixture.Http(RetryAccountingCompositionTests.MeasuredError, HttpStatusCode.TooManyRequests)
                : AdapterFixture.Http(AdapterFixture.Response());
            response.Headers.TryAddWithoutValidation("Retry-After", "3");
            return Task.FromResult(response);
        });
        using var provider = AdapterFixture.Provider(handler);
        var agent = RuntimeAgentFactory.Create(new(provider, [], hooks), IntegrationFixture.Options(clock));
        var pending = RetryAccountingCompositionTests.Execute(agent, RetryAccountingCompositionTests.Request(new(
            accountingPolicy: new(new(5, 4), 40, 40), retryPolicy: new(2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), honor))), candidate);
        await RuntimeFixture.Await(waiting.Task);
        Assert.Single(hooks.Exposures); Assert.Single(hooks.Settlements);
        Assert.Equal(0, hooks.Settlements[0].Accounting!.Input.ReservedTokens);
        clock.Advance(delay - TimeSpan.FromTicks(1));
        Assert.False(pending.IsCompleted); Assert.Equal(1, handler.Sends); Assert.Single(hooks.Exposures);
        clock.Advance(TimeSpan.FromTicks(1));
        var result = await RuntimeFixture.Await(pending);
        Assert.Equal(AgentTerminationReason.Completed, result.Reason); Assert.Equal(2, handler.Sends);
        Assert.Equal(delay.Ticks, sentAt[1] - sentAt[0]); Assert.Equal(2, hooks.Exposures.Count);
        Assert.Equal(13, result.Usage!.Accounting!.Input.MeasuredTokens);
    }

    [Fact]
    public async Task HttpHintAboveHostCeilingDeclinesRetryWithoutEarlierSend()
    {
        var clock = new ControlledTimeProvider(); var count = 0;
        using var handler = new FakeHandler((_, _) =>
        {
            count++;
            var response = AdapterFixture.Http(RetryAccountingCompositionTests.MeasuredError, HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", "3"); return Task.FromResult(response);
        });
        using var provider = AdapterFixture.Provider(handler); var hooks = new RuntimeHooks();
        var result = await RetryAccountingCompositionTests.Execute(RuntimeAgentFactory.Create(new(provider, [], hooks), IntegrationFixture.Options(clock)),
            RetryAccountingCompositionTests.Request(new(retryPolicy: new(2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), true))), true);
        Assert.Equal(AgentTerminationReason.Failed, result.Reason); Assert.Equal(1, count); Assert.Equal(1, handler.Sends);
        Assert.Single(hooks.Exposures); Assert.Equal(0, clock.GetTimestamp()); Assert.Equal(3, result.Usage!.InputTokens.ObservedTokens);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancellationOrOriginalDeadlineDuringBackoffPreventsRetryAdmission(bool cancel)
    {
        var clock = new ControlledTimeProvider(); var waiting = RuntimeFixture.Barrier(); using var cancellation = new CancellationTokenSource();
        clock.TimerCreated = due => { if (due == TimeSpan.FromSeconds(8)) waiting.TrySetResult(); };
        using var handler = new FakeHandler((_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            return Task.FromResult(AdapterFixture.Http(RetryAccountingCompositionTests.MeasuredError, HttpStatusCode.ServiceUnavailable));
        });
        using var provider = AdapterFixture.Provider(handler);
        var hooks = new RuntimeHooks { After = (s, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(2)); return ValueTask.FromResult<SettlementAcknowledgement?>(RuntimeHooks.Continue(s));
        } };
        var agent = RuntimeAgentFactory.Create(new(provider, [], hooks), new RuntimeOptions(clock, settlementGrace: TimeSpan.FromSeconds(3)));
        var pending = RetryAccountingCompositionTests.Execute(agent, RetryAccountingCompositionTests.Request(new(
            accountingPolicy: new(new(5, 4), 40, 40), retryPolicy: new(2, TimeSpan.FromSeconds(8)))), true, cancellation.Token);
        await RuntimeFixture.Await(waiting.Task);
        Assert.Equal(TimeSpan.FromSeconds(3).Ticks, clock.GetTimestamp());
        if (cancel) cancellation.Cancel(); else clock.Advance(TimeSpan.FromSeconds(7));
        var result = await RuntimeFixture.Await(pending);
        Assert.Equal(cancel ? AgentTerminationReason.Cancelled : AgentTerminationReason.ResourceLimit, result.Reason);
        Assert.Equal(1, handler.Sends); Assert.Single(hooks.Exposures); Assert.Single(result.Usage!.Attempts);
        Assert.Equal(3, result.Usage.Accounting!.Input.MeasuredTokens); Assert.Equal(0, result.Usage.Accounting.Input.ReservedTokens);
        clock.Advance(TimeSpan.FromSeconds(20)); Assert.Equal(1, handler.Sends); Assert.Single(hooks.Exposures);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualFailedAttemptMustFinishSettlementBeforeRetryOrExpiry(bool expire)
    {
        var clock = new ControlledTimeProvider(); var entered = RuntimeFixture.Barrier<RuntimeSettlement>();
        var release = RuntimeFixture.Barrier<SettlementAcknowledgement?>(); var count = 0;
        using var handler = new FakeHandler((_, _) => Task.FromResult(++count == 1
            ? AdapterFixture.Http(RetryAccountingCompositionTests.MeasuredError, HttpStatusCode.ServiceUnavailable)
            : AdapterFixture.Http(AdapterFixture.Response())));
        using var provider = AdapterFixture.Provider(handler);
        var hooks = new RuntimeHooks { After = (s, _) =>
        {
            if (s.Exposure.Attempt.AttemptNumber != 1) return ValueTask.FromResult<SettlementAcknowledgement?>(RuntimeHooks.Continue(s));
            entered.SetResult(s); return new(release.Task);
        } };
        var observed = new ObservedHooks(hooks);
        var pending = RetryAccountingCompositionTests.Execute(RuntimeAgentFactory.Create(new(provider, [], observed), IntegrationFixture.Options(clock)),
            RetryAccountingCompositionTests.Request(new(accountingPolicy: new(new(5, 4), 40, 40), retryPolicy: new(2))), true);
        var settlement = await RuntimeFixture.Await(entered.Task);
        try
        {
            Assert.Equal(1, handler.Sends); Assert.Single(hooks.Exposures); Assert.False(pending.IsCompleted);
            Assert.Equal(3, settlement.Accounting!.Input.MeasuredTokens); Assert.Equal(0, settlement.Accounting.Input.ReservedTokens);
            if (expire) clock.Advance(TimeSpan.FromSeconds(1)); else release.SetResult(RuntimeHooks.Continue(settlement));
            var result = await RuntimeFixture.Await(pending);
            Assert.Equal(expire ? AgentTerminationReason.Failed : AgentTerminationReason.Completed, result.Reason);
            Assert.Equal(expire ? 1 : 2, handler.Sends);
            var snapshot = JsonSerializer.Serialize(result);
            if (expire)
            {
                Assert.False(release.Task.IsCompleted); Assert.False(observed.AfterReturned.Task.IsCompleted);
                release.SetResult(RuntimeHooks.Continue(settlement));
            }
            await RuntimeFixture.Await(observed.AfterReturned.Task);
            Assert.Equal(snapshot, JsonSerializer.Serialize(result)); Assert.Equal(expire ? 1 : 2, handler.Sends);
            Assert.Equal(expire ? 1 : 2, hooks.Exposures.Count);
        }
        finally { release.TrySetResult(RuntimeHooks.Continue(settlement)); }
    }

    [Theory]
    [InlineData("send", false)] [InlineData("body", false)] [InlineData("send", true)] [InlineData("body", true)]
    public async Task HeldRetriedTransportCannotChangeEarlierEffectsOrFinalAccounting(string phase, bool cancel)
    {
        var clock = new ControlledTimeProvider(); using var cancellation = new CancellationTokenSource();
        var held = RuntimeFixture.Barrier(); var release = RuntimeFixture.Barrier(); var drained = RuntimeFixture.Barrier();
        var counter = new CounterCapability(); var count = 0;
        using var handler = new FakeHandler(async (_, _) =>
        {
            switch (++count)
            {
                case 1: return AdapterFixture.Http(AdapterFixture.Response(null, "tool", "tool_calls",
                    calls: [AdapterFixture.Call("counter", "counter", IntegrationFixture.CounterArguments)]));
                case 2: return AdapterFixture.Http(AdapterFixture.Response("earlier-candidate", "earlier-reasoning"));
                case 3: return AdapterFixture.Http(RetryAccountingCompositionTests.MeasuredError, HttpStatusCode.ServiceUnavailable);
                case 4:
                    var late = AdapterFixture.Response("late-candidate", "late-reasoning", usage: new { prompt_tokens = 900, completion_tokens = 800 });
                    if (phase == "send")
                    {
                        held.SetResult(); await release.Task.ConfigureAwait(false); return AdapterFixture.Http(late);
                    }
                    var content = new HeldContent(Encoding.UTF8.GetBytes(late), drained, held, release);
                    content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
                    return new(HttpStatusCode.OK) { Content = content };
                default: throw new InvalidOperationException("Unexpected post-cut transport.");
            }
        });
        using var adapter = AdapterFixture.Provider(handler); var provider = new ObservedProvider(adapter, observedReturn: 4);
        var hooks = new RuntimeHooks();
        var agent = RuntimeAgentFactory.Create(new(provider, [new(new CounterTool(maximumResultBytes: 64), counter)], hooks), IntegrationFixture.Options(clock));
        ScriptedCandidateHost? host = null;
        host = new ScriptedCandidateHost((s, _) => { host!.ApplyEffect(); return CandidateFixture.Feedback(s, CandidateDecision.Accept, CandidateContinuation.Continue); });
        var request = IntegrationFixture.Request(usageLimits: new(maximumPhysicalDispatches: 4, maximumToolInvocations: 1,
            accountingPolicy: new(new(5, 4), 100, 100), retryPolicy: new(2)));
        var pending = CandidateConsumer.RunAsync((ICandidateAgent)agent, request, host, cancellation.Token).AsTask();
        await RuntimeFixture.Await(held.Task);
        try
        {
            Assert.Equal(1, host.Effects); Assert.Equal(1, counter.Effects); Assert.False(provider.Returned.Task.IsCompleted);
            Assert.Equal(5, hooks.Exposures[^1].Accounting!.Input.ReservedTokens);
            if (cancel) cancellation.Cancel(); else clock.Advance(TimeSpan.FromSeconds(10));
            var result = (await RuntimeFixture.Await(pending)).Result;
            Assert.Equal(cancel ? CandidateStopReason.Cancelled : CandidateStopReason.DurationLimit, result.StopReason);
            Assert.Equal(2, result.Outcome.CompletedWorkUnits); Assert.Single(result.Receipts); Assert.Single(host.Submissions);
            Assert.Equal(1, result.ContinuationsAdmitted); Assert.Equal(0, result.RepairsAdmitted);
            Assert.Equal(4, handler.Sends); Assert.Equal(4, result.Outcome.Usage!.Attempts.Count);
            var last = result.Outcome.Usage.Attempts[^1];
            Assert.Equal(phase == "send" ? DispatchExposure.Unknown : DispatchExposure.Dispatched, last.Exposure);
            Assert.Null(last.Usage.InputTokens); Assert.Null(last.Usage.OutputTokens);
            Assert.Equal(2, last.AttemptNumber); Assert.Equal(result.Outcome.Usage.Attempts[2].LogicalCallId, last.LogicalCallId);
            var accounting = result.Outcome.Usage.Accounting!;
            Assert.Equal(23, accounting.Input.MeasuredTokens); Assert.Equal(12, accounting.Output.MeasuredTokens);
            Assert.Equal(5, accounting.Input.UnresolvedTokens); Assert.Equal(4, accounting.Output.UnresolvedTokens);
            Assert.Equal(0, accounting.Input.ReservedTokens); Assert.All(accounting.Attempts, a => Assert.True(a.IsFinalized));
            var frozen = JsonSerializer.Serialize(result);
            Assert.False(release.Task.IsCompleted); release.SetResult();
            if (phase == "body") await RuntimeFixture.Await(drained.Task);
            await RuntimeFixture.Await(provider.Returned.Task);
            Assert.Equal(frozen, JsonSerializer.Serialize(result)); Assert.Same(last, result.Outcome.Usage.Attempts[^1]);
            Assert.Equal(4, handler.Sends); Assert.Equal(4, hooks.Settlements.Count);
            Assert.Single(host.Submissions); Assert.Equal(1, host.Effects); Assert.Equal(1, counter.Effects); Assert.Equal(2, counter.Total);
        }
        finally { release.TrySetResult(); }
    }
}

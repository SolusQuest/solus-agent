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
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.RuntimeIntegration;

public sealed class SettlementAndCutTests
{
    private static RuntimeToolRegistration[] Bindings(CounterTool counter, CounterCapability counterCapability,
        IFunctionTool transform, IToolCapability transformCapability) =>
        [new(counter, counterCapability), new(transform, transformCapability)];

    [Fact]
    public async Task ExposureDenyBeforeDispatchHasNoTransportToolOrHostEffect()
    {
        var counter = new CounterTool(maximumResultBytes: 64); var counterCapability = new CounterCapability();
        var transform = new TransformTool(maximumResultBytes: 64); var transformCapability = new TransformCapability();
        var hooks = new RuntimeHooks
        {
            Before = (exposure, _) => ValueTask.FromResult<ExposureAcknowledgement?>(
                new(exposure, RuntimeHookStatus.Acknowledged, ExposureDecision.Deny, exposure.RequiredAcknowledgement)),
        };
        using var handler = FakeHandler.Reply(AdapterFixture.Response("candidate-one", "deny-reasoning", usage: IntegrationFixture.Usage(1)));
        using var provider = AdapterFixture.Provider(handler);
        IAgent agent = RuntimeAgentFactory.Create(new(provider, Bindings(counter, counterCapability, transform, transformCapability), hooks),
            IntegrationFixture.Options());
        var candidateAgent = Assert.IsAssignableFrom<ICandidateAgent>(agent);
        var host = new ScriptedCandidateHost((submission, _) => CandidateFixture.Feedback(submission));
        var result = (await CandidateConsumer.RunAsync(candidateAgent, IntegrationFixture.Request(), host)).Result;

        Assert.Equal(CandidateStopReason.ProductionStopped, result.StopReason);
        Assert.Equal(AgentTerminationReason.Partial, result.Outcome.Reason);
        Assert.Equal(0, handler.Sends); Assert.Equal(0, result.Outcome.CompletedWorkUnits);
        Assert.Empty(result.Receipts); Assert.Empty(host.Submissions); Assert.Equal(0, host.Effects);
        Assert.Equal(0, counterCapability.Effects); Assert.Equal(0, transformCapability.Effects);
        var attempt = result.Outcome.Usage!.Attempts.Single();
        Assert.Equal(DispatchExposure.NotDispatched, attempt.Exposure);
        Assert.Equal(UsageCompleteness.Unavailable, attempt.Usage.Completeness);
    }

    [Fact]
    public async Task SameAttemptSettlementStopOnAcceptedToolCallsDeliversNoTools()
    {
        var counter = new CounterTool(maximumResultBytes: 64); var counterCapability = new CounterCapability();
        var transform = new TransformTool(maximumResultBytes: 64); var transformCapability = new TransformCapability();
        var hooks = new RuntimeHooks
        {
            After = (settlement, _) => ValueTask.FromResult<SettlementAcknowledgement?>(
                new(settlement.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Stop)),
        };
        using var handler = FakeHandler.Reply(AdapterFixture.Response(null, "settled-batch-reasoning", "tool_calls",
            calls: [AdapterFixture.Call("call-1a", "counter", IntegrationFixture.CounterArguments),
                    AdapterFixture.Call("call-1b", "transform", IntegrationFixture.TransformArguments)], usage: IntegrationFixture.Usage(1)));
        using var provider = AdapterFixture.Provider(handler);
        IAgent agent = RuntimeAgentFactory.Create(new(provider, Bindings(counter, counterCapability, transform, transformCapability), hooks),
            IntegrationFixture.Options());
        var candidateAgent = Assert.IsAssignableFrom<ICandidateAgent>(agent);
        var host = new ScriptedCandidateHost((submission, _) => CandidateFixture.Feedback(submission));
        var result = (await CandidateConsumer.RunAsync(candidateAgent, IntegrationFixture.Request(), host)).Result;

        Assert.Equal(CandidateStopReason.ProductionStopped, result.StopReason);
        Assert.Equal(1, handler.Sends); Assert.Equal(1, result.Outcome.CompletedWorkUnits);
        Assert.Equal(0, counterCapability.Effects); Assert.Equal(0, transformCapability.Effects);
        Assert.Empty(result.Receipts); Assert.Empty(host.Submissions); Assert.Equal(0, host.Effects);
        Assert.True(hooks.Settlements.Single().ProviderInvoked);
        Assert.Equal(ProviderOutcome.Succeeded, hooks.Settlements.Single().ProviderOutcome);
        var attempt = result.Outcome.Usage!.Attempts.Single();
        Assert.Equal(101, attempt.Usage.InputTokens); Assert.Equal(11, attempt.Usage.OutputTokens);
    }

    [Fact]
    public async Task SameAttemptSettlementStopOnAcceptedFinalDeliversNoCandidate()
    {
        var counter = new CounterTool(maximumResultBytes: 64); var counterCapability = new CounterCapability();
        var transform = new TransformTool(maximumResultBytes: 64); var transformCapability = new TransformCapability();
        var hooks = new RuntimeHooks
        {
            After = (settlement, _) => ValueTask.FromResult<SettlementAcknowledgement?>(
                new(settlement.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Stop)),
        };
        using var handler = FakeHandler.Reply(AdapterFixture.Response("candidate-one", "settled-final-reasoning", usage: IntegrationFixture.Usage(1)));
        using var provider = AdapterFixture.Provider(handler);
        IAgent agent = RuntimeAgentFactory.Create(new(provider, Bindings(counter, counterCapability, transform, transformCapability), hooks),
            IntegrationFixture.Options());
        var candidateAgent = Assert.IsAssignableFrom<ICandidateAgent>(agent);
        var host = new ScriptedCandidateHost((submission, _) => CandidateFixture.Feedback(submission));
        var result = (await CandidateConsumer.RunAsync(candidateAgent, IntegrationFixture.Request(), host)).Result;

        Assert.Equal(CandidateStopReason.ProductionStopped, result.StopReason);
        Assert.Equal(1, handler.Sends); Assert.Equal(1, result.Outcome.CompletedWorkUnits);
        Assert.Empty(result.Receipts); Assert.Empty(host.Submissions); Assert.Equal(0, host.Effects);
        Assert.Equal(0, counterCapability.Effects); Assert.Equal(0, transformCapability.Effects);
        var attempt = result.Outcome.Usage!.Attempts.Single();
        Assert.Equal(101, attempt.Usage.InputTokens); Assert.Equal(11, attempt.Usage.OutputTokens);
    }

    [Fact]
    public async Task MissingClosureRetainsItsFailureCategoryWithoutAnyEffect()
    {
        var counter = new CounterTool(maximumResultBytes: 64); var counterCapability = new CounterCapability();
        var transform = new TransformTool(maximumResultBytes: 64); var transformCapability = new TransformCapability();
        var hooks = new RuntimeHooks { After = (_, _) => ValueTask.FromResult<SettlementAcknowledgement?>(null) };
        using var handler = FakeHandler.Reply(AdapterFixture.Response("candidate-one", "missing-closure-reasoning", usage: IntegrationFixture.Usage(1)));
        using var provider = AdapterFixture.Provider(handler);
        IAgent agent = RuntimeAgentFactory.Create(new(provider, Bindings(counter, counterCapability, transform, transformCapability), hooks),
            IntegrationFixture.Options());
        var candidateAgent = Assert.IsAssignableFrom<ICandidateAgent>(agent);
        var host = new ScriptedCandidateHost((submission, _) => CandidateFixture.Feedback(submission));
        var result = (await CandidateConsumer.RunAsync(candidateAgent, IntegrationFixture.Request(), host)).Result;

        Assert.Equal(CandidateStopReason.ProductionFailed, result.StopReason);
        Assert.Equal(AgentTerminationReason.Failed, result.Outcome.Reason);
        Assert.Equal(AgentFailureCode.ExecutionFailed, result.Outcome.FailureCode);
        Assert.Equal(1, handler.Sends); Assert.Equal(1, result.Outcome.CompletedWorkUnits);
        Assert.Empty(result.Receipts); Assert.Empty(host.Submissions); Assert.Equal(0, host.Effects);
        Assert.Equal(0, counterCapability.Effects); Assert.Equal(0, transformCapability.Effects);
        Assert.Single(hooks.Settlements);
        var attempt = result.Outcome.Usage!.Attempts.Single();
        Assert.Equal(101, attempt.Usage.InputTokens); Assert.Equal(11, attempt.Usage.OutputTokens);
    }

    [Theory]
    [InlineData("send", true)] [InlineData("send", false)]
    [InlineData("body", true)] [InlineData("body", false)]
    public async Task HeldTransportCutReturnsBeforeReleaseAndLateCompletionChangesNothing(string phase, bool cancel)
    {
        var clock = new ControlledTimeProvider(); using var cancellation = new CancellationTokenSource();
        var counter = new CounterTool(maximumResultBytes: 64); var counterCapability = new CounterCapability();
        var transform = new TransformTool(maximumResultBytes: 64); var transformCapability = new TransformCapability();
        var held = RuntimeFixture.Barrier();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = AdapterFixture.Response("late-candidate", "late-reasoning", usage: IntegrationFixture.Usage(1));
        // The phase barrier sits at the held operation itself: the send, or the body read boundary.
        using var handler = phase == "send"
            ? new FakeHandler(async (message, _) =>
            {
                IntegrationFixture.AssertTransport(message);
                held.SetResult();
                await release.Task.ConfigureAwait(false);
                return AdapterFixture.Http(late);
            })
            : new FakeHandler((message, _) =>
            {
                IntegrationFixture.AssertTransport(message);
                var content = new HeldContent(Encoding.UTF8.GetBytes(late), drained, held, release);
                content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            });
        using var adapter = AdapterFixture.Provider(handler);
        var provider = new ObservedProvider(adapter);
        var hooks = new RuntimeHooks();
        IAgent agent = RuntimeAgentFactory.Create(new(provider, Bindings(counter, counterCapability, transform, transformCapability), hooks),
            IntegrationFixture.Options(clock));
        var candidateAgent = Assert.IsAssignableFrom<ICandidateAgent>(agent);
        var host = new ScriptedCandidateHost((submission, _) => CandidateFixture.Feedback(submission));
        var run = CandidateConsumer.RunAsync(candidateAgent, IntegrationFixture.Request(units: 2), host, cancellation.Token).AsTask();
        await RuntimeFixture.Await(held.Task);
        Assert.False(provider.Returned.Task.IsCompleted);
        if (phase == "body") Assert.False(drained.Task.IsCompleted);
        if (cancel) cancellation.Cancel(); else clock.Advance(TimeSpan.FromSeconds(10));
        var result = (await RuntimeFixture.Await(run)).Result;

        Assert.Equal(cancel ? CandidateStopReason.Cancelled : CandidateStopReason.DurationLimit, result.StopReason);
        Assert.Equal(cancel ? AgentTerminationReason.Cancelled : AgentTerminationReason.ResourceLimit, result.Outcome.Reason);
        Assert.Equal(0, result.Outcome.CompletedWorkUnits);
        Assert.Empty(result.Receipts); Assert.Empty(host.Submissions); Assert.Equal(0, host.Effects);
        Assert.Equal(0, counterCapability.Effects); Assert.Equal(0, transformCapability.Effects);
        Assert.Equal(1, handler.Sends);
        var attempt = result.Outcome.Usage!.Attempts.Single();
        Assert.Equal(phase == "send" ? DispatchExposure.Unknown : DispatchExposure.Dispatched, attempt.Exposure);
        Assert.Equal(UsageCompleteness.Unavailable, attempt.Usage.Completeness);
        Assert.True(hooks.Settlements.Single().ProviderInvoked);
        Assert.Null(hooks.Settlements.Single().ProviderOutcome);
        var snapshot = JsonSerializer.Serialize(result);

        // Late completion is observed at the same actual guarded provider invocation, after its real return.
        release.SetResult();
        if (phase == "body") await RuntimeFixture.Await(drained.Task);
        await RuntimeFixture.Await(provider.Returned.Task);
        Assert.Equal(1, handler.Sends); Assert.Empty(host.Submissions); Assert.Equal(0, host.Effects);
        Assert.Equal(0, counterCapability.Effects); Assert.Equal(0, transformCapability.Effects);
        Assert.Single(hooks.Settlements); Assert.Same(attempt, result.Outcome.Usage!.Attempts[0]);
        Assert.Equal(snapshot, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task CompleteParsedResponseWithHeldSettlementFreezesUsageAndDeliversNothing()
    {
        var clock = new ControlledTimeProvider(); using var cancellation = new CancellationTokenSource();
        var counter = new CounterTool(maximumResultBytes: 64); var counterCapability = new CounterCapability();
        var transform = new TransformTool(maximumResultBytes: 64); var transformCapability = new TransformCapability();
        var entered = RuntimeFixture.Barrier<RuntimeSettlement>();
        var receipt = RuntimeFixture.Barrier<SettlementAcknowledgement?>();
        var hooks = new RuntimeHooks { After = (settlement, _) => { entered.SetResult(settlement); return new(receipt.Task); } };
        var observedHooks = new ObservedHooks(hooks);
        using var handler = FakeHandler.Reply(AdapterFixture.Response("candidate-one", "held-settlement-reasoning", usage: IntegrationFixture.Usage(1)));
        using var provider = AdapterFixture.Provider(handler);
        IAgent agent = RuntimeAgentFactory.Create(new(provider, Bindings(counter, counterCapability, transform, transformCapability), observedHooks),
            IntegrationFixture.Options(clock));
        var candidateAgent = Assert.IsAssignableFrom<ICandidateAgent>(agent);
        var host = new ScriptedCandidateHost((submission, _) => CandidateFixture.Feedback(submission));
        var run = CandidateConsumer.RunAsync(candidateAgent, IntegrationFixture.Request(units: 2), host, cancellation.Token).AsTask();
        var settlement = await RuntimeFixture.Await(entered.Task);
        Assert.Equal(1, handler.Sends);
        cancellation.Cancel(); clock.Advance(TimeSpan.FromSeconds(1));
        var result = (await RuntimeFixture.Await(run)).Result;

        // Accepted work and captured usage freeze at the cut; the held settlement cannot deliver a candidate.
        Assert.False(receipt.Task.IsCompleted);
        Assert.Equal(CandidateStopReason.Cancelled, result.StopReason);
        Assert.Equal(AgentTerminationReason.Cancelled, result.Outcome.Reason);
        Assert.Equal(1, result.Outcome.CompletedWorkUnits);
        Assert.Empty(result.Receipts); Assert.Empty(host.Submissions); Assert.Equal(0, host.Effects);
        Assert.Equal(0, counterCapability.Effects); Assert.Equal(0, transformCapability.Effects);
        Assert.Equal(1, handler.Sends);
        var attempt = result.Outcome.Usage!.Attempts.Single();
        Assert.Equal(DispatchExposure.Dispatched, attempt.Exposure);
        Assert.Equal(101, attempt.Usage.InputTokens); Assert.Equal(11, attempt.Usage.OutputTokens);
        var snapshot = JsonSerializer.Serialize(result);

        // Late completion is observed at the same actual returned hook producer after release.
        Assert.False(observedHooks.AfterReturned.Task.IsCompleted);
        receipt.SetResult(RuntimeHooks.Continue(settlement));
        await RuntimeFixture.Await(observedHooks.AfterReturned.Task);
        Assert.Equal(1, handler.Sends); Assert.Empty(host.Submissions); Assert.Equal(0, host.Effects);
        Assert.Single(hooks.Settlements); Assert.Same(attempt, result.Outcome.Usage!.Attempts[0]);
        Assert.Equal(snapshot, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("end-at-ceiling", CandidateStopReason.Completed)]
    [InlineData("continue-without-allowance", CandidateStopReason.ContinuationLimit)]
    public async Task ExactCeilingFollowOnStopsBeforeAnotherSendOrEffect(string mode, CandidateStopReason stop)
    {
        var counter = new CounterTool(maximumResultBytes: 64); var counterCapability = new CounterCapability();
        var transform = new TransformTool(maximumResultBytes: 64); var transformCapability = new TransformCapability();
        var wire = new Queue<string>([
            AdapterFixture.Response("candidate-one", "ceiling-reasoning-one", usage: IntegrationFixture.Usage(1)),
            AdapterFixture.Response("candidate-two", "ceiling-reasoning-two", usage: IntegrationFixture.Usage(2)),
        ]);
        using var handler = new FakeHandler((message, _) =>
        {
            IntegrationFixture.AssertTransport(message);
            return Task.FromResult(AdapterFixture.Http(wire.Dequeue()));
        });
        using var provider = AdapterFixture.Provider(handler);
        IAgent agent = RuntimeAgentFactory.Create(new(provider, Bindings(counter, counterCapability, transform, transformCapability), new RuntimeHooks()),
            IntegrationFixture.Options());
        var candidateAgent = Assert.IsAssignableFrom<ICandidateAgent>(agent);
        ScriptedCandidateHost? host = null;
        host = new ScriptedCandidateHost((submission, _) =>
        {
            host!.ApplyEffect();
            // Only the exact-ceiling run reaches a second submission; the denied run keeps requesting Continue.
            return mode == "end-at-ceiling" && host.Submissions.Count == 2
                ? CandidateFixture.Feedback(submission)
                : CandidateFixture.Feedback(submission, CandidateDecision.Accept, CandidateContinuation.Continue);
        });
        var request = mode == "end-at-ceiling"
            ? IntegrationFixture.Request(units: 2, submissions: 2, repairs: 1, continuations: 1)
            : IntegrationFixture.Request(units: 2, submissions: 2, repairs: 1, continuations: 0);
        var result = (await CandidateConsumer.RunAsync(candidateAgent, request, host)).Result;
        var expected = mode == "end-at-ceiling" ? 2 : 1;

        Assert.Equal(stop, result.StopReason);
        Assert.Equal(expected, result.Outcome.CompletedWorkUnits);
        Assert.Equal(expected, handler.Sends);
        Assert.Equal(expected, host.Submissions.Count);
        Assert.Equal(expected, host.Effects);
        Assert.Equal(mode == "end-at-ceiling" ? 1 : 0, result.ContinuationsAdmitted);
        Assert.Equal(0, counterCapability.Effects); Assert.Equal(0, transformCapability.Effects);
    }
}

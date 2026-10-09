using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Candidates;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Execution;

public sealed class ProviderResultAssociationTests
{
    public static TheoryData<bool, int, string, bool> ForeignResults
    {
        get
        {
            var cases = new TheoryData<bool, int, string, bool>();
            foreach (var candidate in new[] { false, true })
            foreach (var scopeField in new[] { 0, 1, 2 })
            foreach (var kind in new[] { "failure", "success", "not-dispatched" })
            foreach (var streamed in new[] { false, true }) cases.Add(candidate, scopeField, kind, streamed);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(ForeignResults))]
    public async Task ForeignScopeCannotSupplyUsageOrNoDispatchEvidence(bool candidate, int field, string kind, bool streamed)
    {
        var scope = new ProviderScope("p", "m");
        var foreignScope = new ProviderScope(field == 1 ? "p" : "foreign-provider", field == 0 ? "m" : "foreign-model");
        var guarded = kind == "not-dispatched" ? (IModelProvider)new DelegateProvider(foreignScope, (_, observation, _) =>
        {
            observation.ObserveDispatch(DispatchExposure.NotDispatched);
            throw new ProviderFailureException(new(ProviderRetryKind.Transient));
        }) : new ScriptedProvider([kind == "failure" ? ScriptedProvider.Failure(new(ProviderRetryKind.Throttled)) : ScriptedProvider.Final], foreignScope);
        ProviderExchangeResult? foreign = null;
        var provider = new InterfaceScriptedProvider(scope, async (request, token) =>
        {
            if (streamed)
            {
                request.Observation.ObserveDispatch(DispatchExposure.Dispatched);
                request.Observation.CaptureUsage(new(11, 6));
            }
            foreign = await guarded.ExchangeAsync(new(foreignScope, request.Attempt, request.Inputs), token);
            return foreign;
        });
        var hooks = new RuntimeHooks(); var host = Host();
        var outcome = await Execute(candidate, provider, hooks, host);
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(0, outcome.CompletedWorkUnits);
        Assert.Equal(1, provider.Calls); Assert.Empty(host.Submissions);
        var fact = Assert.Single(outcome.Usage!.Attempts); var settlement = Assert.Single(hooks.Settlements);
        Assert.Equal(ProviderOutcome.Rejected, settlement.ProviderOutcome);
        Assert.Equal(ProviderError.InvalidAssociation, settlement.ProviderError); Assert.True(settlement.ProviderInvoked);
        Assert.Same(fact, settlement.Observation);
        Assert.Equal(streamed ? DispatchExposure.Dispatched : DispatchExposure.Unknown, fact.Exposure);
        Assert.Equal(streamed ? 11L : null, fact.Usage.InputTokens); Assert.Equal(streamed ? 6L : null, fact.Usage.OutputTokens);
        Assert.Equal(kind == "not-dispatched" ? DispatchExposure.NotDispatched : DispatchExposure.Dispatched, foreign!.Observation.Exposure);
        if (kind != "success") Assert.NotNull(foreign.Retry);
        Assert.Equal(foreign.Observation.PhysicalAttemptId, fact.PhysicalAttemptId);
        Assert.Equal(foreign.Observation.LogicalCallId, fact.LogicalCallId);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AssociatedFailureKeepsItsUsageAndDoesNotRetry(bool candidate)
    {
        var guarded = new ScriptedProvider([ScriptedProvider.Failure(new(ProviderRetryKind.Transient)), ScriptedProvider.Final]);
        var forwarding = new InterfaceScriptedProvider(guarded.Scope, (request, token) =>
            guarded.ExchangeAsync(new(request.Scope, request.Attempt, request.Inputs), token));
        var hooks = new RuntimeHooks(); var host = Host();
        var outcome = await Execute(candidate, forwarding, hooks, host);
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(1, guarded.Effects); Assert.Equal(1, forwarding.Calls);
        Assert.Equal(0, outcome.CompletedWorkUnits); Assert.Empty(host.Submissions);
        Assert.Equal(ProviderOutcome.Failed, Assert.Single(hooks.Settlements).ProviderOutcome);
        Assert.Equal(ProviderError.ProviderFailed, hooks.Settlements[0].ProviderError);
        Assert.Equal(3, Assert.Single(outcome.Usage!.Attempts).Usage.InputTokens);
        Assert.Equal(2, outcome.Usage.Attempts[0].Usage.OutputTokens);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AssociatedPayloadRejectionStillKeepsReturnedUsage(bool candidate)
    {
        var guarded = new ScriptedProvider([(request, observation, _) =>
        {
            observation.CaptureUsage(new(3, 2));
            return ValueTask.FromResult(RuntimeFixture.Final(request, new string('x', 129)));
        }]);
        var forwarding = new InterfaceScriptedProvider(guarded.Scope, (request, token) =>
            guarded.ExchangeAsync(new(request.Scope, request.Attempt, request.Inputs), token));
        var hooks = new RuntimeHooks(); var host = Host();
        var outcome = await Execute(candidate, forwarding, hooks, host, bounds: new(maximumResponseBytes: 128));
        Assert.Equal(AgentTerminationReason.ResourceLimit, outcome.Reason); Assert.Equal(0, outcome.CompletedWorkUnits);
        Assert.Equal(ProviderError.LimitExceeded, Assert.Single(hooks.Settlements).ProviderError);
        Assert.Equal(3, Assert.Single(outcome.Usage!.Attempts).Usage.InputTokens);
        Assert.Equal(2, outcome.Usage.Attempts[0].Usage.OutputTokens); Assert.Empty(host.Submissions);
        Assert.Equal(1, forwarding.Calls);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task LateForeignFailureCannotReplaceRequestSnapshotAfterCut(bool candidate, bool cancel)
    {
        var clock = new ControlledTimeProvider(); using var cancellation = new CancellationTokenSource();
        var entered = RuntimeFixture.Barrier(); var pending = RuntimeFixture.Barrier<ProviderExchangeResult>();
        var foreignScope = new ProviderScope("foreign", "foreign");
        var guarded = new ScriptedProvider([ScriptedProvider.Failure(new(ProviderRetryKind.Throttled))], foreignScope);
        ProviderExchangeResult? foreign = null;
        var forwarding = new InterfaceScriptedProvider(new("p", "m"), async (request, token) =>
        {
            request.Observation.ObserveDispatch(DispatchExposure.Dispatched); request.Observation.CaptureUsage(new(11, 6));
            foreign = await guarded.ExchangeAsync(new(foreignScope, request.Attempt, request.Inputs), token);
            entered.SetResult(); return await pending.Task;
        });
        var hooks = new RuntimeHooks(); var host = Host();
        var run = Execute(candidate, forwarding, hooks, host, options: new(clock), token: cancellation.Token);
        await RuntimeFixture.Await(entered.Task);
        if (cancel) cancellation.Cancel(); else clock.Advance(TimeSpan.FromSeconds(10));
        var outcome = await RuntimeFixture.Await(run);
        Assert.False(pending.Task.IsCompleted); Assert.Equal(cancel ? AgentTerminationReason.Cancelled : AgentTerminationReason.ResourceLimit, outcome.Reason);
        var fact = Assert.Single(outcome.Usage!.Attempts); var settlement = Assert.Single(hooks.Settlements);
        Assert.Equal(11, fact.Usage.InputTokens); Assert.Equal(6, fact.Usage.OutputTokens);
        Assert.Equal(DispatchExposure.Dispatched, fact.Exposure); Assert.Null(settlement.ProviderOutcome);
        Assert.Same(fact, settlement.Observation); Assert.Empty(host.Submissions);
        pending.SetResult(foreign!); await pending.Task;
        Assert.Same(fact, outcome.Usage.Attempts[0]); Assert.Single(hooks.Settlements); Assert.Equal(1, forwarding.Calls);
    }

    private static ScriptedCandidateHost Host() => new((submission, _) => CandidateFixture.Feedback(submission));
    private static async Task<AgentOutcome> Execute(bool candidate, IModelProvider provider, RuntimeHooks hooks, ScriptedCandidateHost host,
        ProviderExchangeBounds? bounds = null, SolusAgent.Runtime.Startup.RuntimeOptions? options = null, CancellationToken token = default)
    {
        var agent = RuntimeFixture.Agent(provider, hooks, options, bounds);
        var request = DispatchLimitTests.Request(logical: 2, physical: 1);
        return candidate ? (await ((ICandidateAgent)agent).ExecuteCandidatesAsync(new(request, new(3, 2, 2)), host, cancellationToken: token)).Outcome
            : await agent.ExecuteAsync(request, cancellationToken: token);
    }
}

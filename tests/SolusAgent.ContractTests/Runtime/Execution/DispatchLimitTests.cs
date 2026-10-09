using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Execution;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Execution;

public sealed class DispatchLimitTests
{
    internal static AgentRequest Request(int? logical = null, int? physical = null, Guid? executionId = null) =>
        new(executionId ?? Guid.NewGuid(), "i", [], new(8, TimeSpan.FromSeconds(10)), AgentCapability.DispatchLimits,
            new(logical, physical));

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RequiredDispatchLimitsAreAdvertisedAndNullPoliciesAllowFinal(bool nullPolicy)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final]);
        var agent = RuntimeFixture.Agent(provider);
        Assert.True(agent.SupportedCapabilities.HasFlag(AgentCapability.DispatchLimits));
        var request = nullPolicy ? RuntimeFixture.Request(required: AgentCapability.DispatchLimits) : Request();
        var outcome = await agent.ExecuteAsync(request);
        Assert.Equal(AgentTerminationReason.Completed, outcome.Reason);
        Assert.Equal(1, outcome.CompletedWorkUnits); Assert.Equal(1, provider.Effects);
        Assert.Equal(UsageInventoryCoverage.Complete, outcome.Usage!.Coverage);
    }

    [Theory]
    [InlineData(1, 3, false)] [InlineData(3, 1, false)]
    [InlineData(2, 2, true)] [InlineData(3, 3, true)]
    [InlineData(null, 1, false)] [InlineData(1, null, false)]
    [InlineData(null, 2, true)] [InlineData(2, null, true)] [InlineData(null, null, true)]
    public async Task OrdinaryToolTurnConsumesProviderSlotsButToolEffectsDoNot(int? logical, int? physical, bool completes)
    {
        var capability = new ConfigurationCapability(); var tool = new ConfigurationTool(); var hooks = new RuntimeHooks();
        var guarded = new ScriptedProvider([(r, _, _) => ValueTask.FromResult(new ProviderResponse(r.Scope, r.Attempt,
            ProviderFinish.ToolCalls, null, [new ToolCall("call", tool.Descriptor.Name, "{\"text\":\"data\"}")])), ScriptedProvider.Final]);
        var provider = new InterfaceScriptedProvider(guarded.Scope, guarded.ExchangeAsync);
        var outcome = await RuntimeAgentFactory.Create(new(provider, [new(tool, capability)], hooks)).ExecuteAsync(Request(logical, physical));
        var count = completes ? 2 : 1;
        Assert.Equal(completes ? AgentTerminationReason.Completed : AgentTerminationReason.ResourceLimit, outcome.Reason);
        Assert.Equal(count, provider.Calls); Assert.Equal(count, guarded.Effects);
        Assert.Equal(count, hooks.Exposures.Count); Assert.Equal(count, hooks.Settlements.Count);
        Assert.Equal(1, capability.Effects); Assert.Equal(count, outcome.CompletedWorkUnits);
        Assert.Equal(UsageInventoryCoverage.Complete, outcome.Usage!.Coverage);
        Assert.Equal(count, outcome.Usage.Attempts.Count);
        Assert.Equal(count, outcome.Usage.Attempts.Select(a => a.LogicalCallId).Distinct().Count());
        Assert.Equal(count, outcome.Usage.Attempts.Select(a => a.PhysicalAttemptId).Distinct().Count());
        Assert.All(outcome.Usage.Attempts, a => { Assert.Equal(1, a.AttemptNumber); Assert.Equal(DispatchExposure.Dispatched, a.Exposure); });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DeniedExposureRetainsAdmittedAttemptWithoutProviderOrCompletedWork(bool candidate)
    {
        var provider = new InterfaceScriptedProvider(new("p", "m"), (_, _) => throw new InvalidOperationException());
        var hooks = new RuntimeHooks { Before = (e, _) => ValueTask.FromResult<ExposureAcknowledgement?>(
            new(e, RuntimeHookStatus.Acknowledged, ExposureDecision.Deny)) };
        var agent = RuntimeFixture.Agent(provider, hooks);
        var host = new ScriptedCandidateHost((s, _) => Candidates.CandidateFixture.Feedback(s));
        var outcome = candidate ? (await ((ICandidateAgent)agent).ExecuteCandidatesAsync(new(Request(1, 1), new(3, 2, 2)), host)).Outcome
            : await agent.ExecuteAsync(Request(1, 1));
        Assert.Equal(AgentTerminationReason.Partial, outcome.Reason); Assert.Equal(0, outcome.CompletedWorkUnits);
        Assert.Equal(0, provider.Calls); Assert.Empty(host.Submissions);
        Assert.Single(hooks.Exposures); Assert.Single(hooks.Settlements);
        Assert.False(hooks.Settlements[0].ProviderInvoked);
        Assert.Equal(DispatchExposure.NotDispatched, outcome.Usage!.Attempts.Single().Exposure);
        Assert.Equal(UsageInventoryCoverage.Complete, outcome.Usage.Coverage);
    }

    [Theory]
    [InlineData(DispatchExposure.Unknown)] [InlineData(DispatchExposure.Dispatched)] [InlineData(DispatchExposure.NotDispatched)]
    public async Task ProviderFailureRetainsInventoryAndNeverBecomesCompletedWork(DispatchExposure exposure)
    {
        var provider = new InterfaceScriptedProvider(new("p", "m"), (r, _) =>
        { r.Observation.ObserveDispatch(exposure); throw new InvalidOperationException(); });
        var hooks = new RuntimeHooks();
        var outcome = await RuntimeFixture.Agent(provider, hooks).ExecuteAsync(Request(1, 1));
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(0, outcome.CompletedWorkUnits);
        Assert.Equal(1, provider.Calls); Assert.Equal(exposure, outcome.Usage!.Attempts.Single().Exposure);
        Assert.True(hooks.Settlements.Single().ProviderInvoked);
        Assert.Equal(ProviderOutcome.Failed, hooks.Settlements[0].ProviderOutcome);
    }

    [Theory]
    [InlineData(DispatchExposure.NotDispatched)] [InlineData(DispatchExposure.Unknown)] [InlineData(DispatchExposure.Dispatched)]
    public void FinalNoDispatchRefundIsIdempotentAndCannotRefundAnotherAttempt(DispatchExposure exposure)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final]); var request = Request(physical: 1);
        using var cut = new RunCut(TimeProvider.System, request.Bounds.MaximumDuration, CancellationToken.None);
        var state = new RunState(request, new(provider, [], new RuntimeHooks()), new(), cut); state.Initialize();
        var first = state.AdmitTurn()!;
        var final = new UsageAttemptObservation(first.Attempt.ExecutionId, first.Attempt.LogicalCallId,
            first.Attempt.PhysicalAttemptId, first.Attempt.AttemptNumber, exposure, new());
        state.Retain(first.Attempt, final); state.Retain(first.Attempt, final);
        var next = state.AdmitTurn();
        Assert.Equal(exposure == DispatchExposure.NotDispatched, next is not null);
        if (next is not null)
        {
            // The old refund must not free the new attempt's reservation; its provisional placeholder cannot free it either.
            state.Retain(first.Attempt, final);
            Assert.Null(state.AdmitTurn());
            Assert.Equal(2, state.Usage().Attempts.Count);
        }
        Assert.Equal(RuntimeStop.ResourceLimit, state.AdmissionStop); Assert.Equal(0, state.Completed);
    }

    [Theory]
    [InlineData(false, "pre")] [InlineData(true, "pre")]
    [InlineData(false, "permission")] [InlineData(true, "permission")]
    [InlineData(false, "unknown-provider")] [InlineData(true, "unknown-provider")]
    [InlineData(false, "dispatched-provider")] [InlineData(true, "dispatched-provider")]
    public async Task CancellationAroundDispatchGateRetainsOnlyActualAdmittedFacts(bool candidate, string phase)
    {
        using var cancel = new CancellationTokenSource();
        var entered = RuntimeFixture.Barrier(); var pending = RuntimeFixture.Barrier<ProviderExchangeResult>();
        var provider = new InterfaceScriptedProvider(new("p", "m"), (r, _) =>
        {
            if (phase == "dispatched-provider") r.Observation.ObserveDispatch(DispatchExposure.Dispatched);
            entered.SetResult(); return new(pending.Task);
        });
        var hooks = new RuntimeHooks { Before = (e, _) =>
        { if (phase == "permission") cancel.Cancel(); return ValueTask.FromResult<ExposureAcknowledgement?>(RuntimeHooks.Permit(e)); } };
        var host = new ScriptedCandidateHost((s, _) => Candidates.CandidateFixture.Feedback(s));
        var agent = RuntimeFixture.Agent(provider, hooks);
        if (phase == "pre") cancel.Cancel();
        var run = candidate ? CandidateRun() : agent.ExecuteAsync(Request(1, 1), cancellationToken: cancel.Token).AsTask();
        if (phase.EndsWith("provider", StringComparison.Ordinal)) { await RuntimeFixture.Await(entered.Task); cancel.Cancel(); }
        var outcome = await RuntimeFixture.Await(run);
        Assert.Equal(AgentTerminationReason.Cancelled, outcome.Reason); Assert.Equal(0, outcome.CompletedWorkUnits);
        Assert.Equal(phase.EndsWith("provider", StringComparison.Ordinal) ? 1 : 0, provider.Calls);
        Assert.Empty(host.Submissions); Assert.Equal(UsageInventoryCoverage.Complete, outcome.Usage!.Coverage);
        if (phase == "pre") { Assert.Empty(outcome.Usage.Attempts); Assert.Empty(hooks.Exposures); }
        else
        {
            Assert.Single(hooks.Settlements);
            Assert.Equal(phase == "permission" ? DispatchExposure.NotDispatched
                : phase == "unknown-provider" ? DispatchExposure.Unknown : DispatchExposure.Dispatched, outcome.Usage.Attempts.Single().Exposure);
            Assert.False(pending.Task.IsCompleted);
            if (phase.EndsWith("provider", StringComparison.Ordinal)) pending.SetException(new InvalidOperationException("late"));
        }
        async Task<AgentOutcome> CandidateRun() => (await ((ICandidateAgent)agent).ExecuteCandidatesAsync(
            new(Request(1, 1), new(3, 2, 2)), host, cancellationToken: cancel.Token)).Outcome;
    }

    [Fact]
    public async Task ConcurrentOrdinaryRunsWithEqualExecutionIdEachOwnTheirLimit()
    {
        var entered = RuntimeFixture.Barrier(); var release = RuntimeFixture.Barrier(); var calls = 0;
        async ValueTask<ProviderResponse> Step(ProviderRequest r, ProviderObservation _, CancellationToken token)
        { if (Interlocked.Increment(ref calls) == 2) entered.SetResult(); await release.Task; return RuntimeFixture.Final(r); }
        var guarded = new ScriptedProvider([Step, Step]); var provider = new InterfaceScriptedProvider(guarded.Scope, guarded.ExchangeAsync);
        var hooks = new RuntimeHooks(); var agent = RuntimeFixture.Agent(provider, hooks); var id = Guid.NewGuid();
        var a = agent.ExecuteAsync(Request(1, 1, id)).AsTask(); var b = agent.ExecuteAsync(Request(1, 1, id)).AsTask();
        await RuntimeFixture.Await(entered.Task); release.SetResult(); var results = await Task.WhenAll(a, b);
        Assert.All(results, r => { Assert.Equal(AgentTerminationReason.Completed, r.Reason); Assert.Equal(1, r.CompletedWorkUnits); });
        var attempts = results.SelectMany(r => r.Usage!.Attempts).ToArray();
        Assert.Equal(2, provider.Calls); Assert.Equal(2, hooks.Exposures.Count); Assert.Equal(2, attempts.Length);
        Assert.Equal(2, attempts.Select(a => a.LogicalCallId).Distinct().Count());
        Assert.Equal(2, attempts.Select(a => a.PhysicalAttemptId).Distinct().Count());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancellationDuringFinalAdmissionValidationCreatesNoAttemptOrExposure(bool candidate)
    {
        using var cancel = new CancellationTokenSource(); var provider = new AdmissionCancellingProvider(cancel);
        var hooks = new RuntimeHooks(); var agent = RuntimeFixture.Agent(provider, hooks); provider.Armed = true;
        var host = new ScriptedCandidateHost((s, _) => Candidates.CandidateFixture.Feedback(s));
        var outcome = candidate ? (await ((ICandidateAgent)agent).ExecuteCandidatesAsync(new(Request(1, 1), new(3, 2, 2)), host,
            cancellationToken: cancel.Token)).Outcome : await agent.ExecuteAsync(Request(1, 1), cancellationToken: cancel.Token);
        Assert.Equal(AgentTerminationReason.Cancelled, outcome.Reason); Assert.Empty(outcome.Usage!.Attempts);
        Assert.Empty(hooks.Exposures); Assert.Empty(hooks.Settlements); Assert.Empty(host.Submissions);
        Assert.Equal(0, provider.Calls); Assert.Equal(0, outcome.CompletedWorkUnits);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HeldPermissionCannotDispatchAfterCancelledCountAdmission(bool candidate)
    {
        using var cancel = new CancellationTokenSource();
        var entered = RuntimeFixture.Barrier<RuntimeExposure>(); var permission = RuntimeFixture.Barrier<ExposureAcknowledgement?>();
        var hooks = new RuntimeHooks { Before = (e, _) => { entered.SetResult(e); return new(permission.Task); } };
        var provider = new InterfaceScriptedProvider(new("p", "m"), (_, _) => throw new InvalidOperationException());
        var agent = RuntimeFixture.Agent(provider, hooks);
        var host = new ScriptedCandidateHost((s, _) => Candidates.CandidateFixture.Feedback(s));
        var run = candidate ? CandidateRun() : agent.ExecuteAsync(Request(1, 1), cancellationToken: cancel.Token).AsTask();
        var exposure = await RuntimeFixture.Await(entered.Task); cancel.Cancel(); var outcome = await RuntimeFixture.Await(run);
        Assert.Equal(AgentTerminationReason.Cancelled, outcome.Reason); Assert.Equal(0, provider.Calls);
        Assert.Equal(DispatchExposure.NotDispatched, outcome.Usage!.Attempts.Single().Exposure);
        Assert.Single(hooks.Settlements); Assert.False(hooks.Settlements[0].ProviderInvoked);
        Assert.False(permission.Task.IsCompleted); Assert.Empty(host.Submissions);
        permission.SetResult(RuntimeHooks.Permit(exposure)); await permission.Task;
        Assert.Equal(0, provider.Calls); Assert.Single(outcome.Usage.Attempts);
        async Task<AgentOutcome> CandidateRun() => (await ((ICandidateAgent)agent).ExecuteCandidatesAsync(
            new(Request(1, 1), new(3, 2, 2)), host, cancellationToken: cancel.Token)).Outcome;
    }

    private sealed class AdmissionCancellingProvider(CancellationTokenSource cancellation) : IModelProvider
    {
        public bool Armed { get; set; }
        public int Calls { get; private set; }
        public ProviderScope Scope { get; } = new("p", "m");
        public ProviderCapabilities Capabilities
        { get { if (Armed) cancellation.Cancel(); return DelegateProvider.All; } }
        public ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException(); }
    }
}

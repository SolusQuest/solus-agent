using CustomTools;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Candidates;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Tools;

public sealed class ToolBudgetIntegrationTests
{
    private const AgentCapability Required = AgentCapability.DispatchLimits | AgentCapability.UsageThresholds | AgentCapability.ToolInvocationLimit;

    public static IEnumerable<object?[]> Boundaries()
    {
        foreach (var candidate in new[] { false, true })
        foreach (var dispatches in new[] { 1, 2 })
        foreach (var toolLimit in new[] { 1, 2 })
        foreach (var input in new long?[] { null, 4, 5 }) yield return [candidate, dispatches, toolLimit, input];
    }

    [Theory, MemberData(nameof(Boundaries))]
    public async Task ToolBatchAdmissionPrecedesNextProductionCountAndTokenChecks(bool candidate, int dispatches, int toolLimit, long? input)
    {
        var counter = new CounterCapability(); var transform = new TransformCapability(); var hooks = new RuntimeHooks();
        var provider = new ScriptedProvider([(r, o, _) =>
        {
            o.CaptureUsage(new(input, 2));
            return ValueTask.FromResult(new ProviderResponse(r.Scope, r.Attempt, ProviderFinish.ToolCalls, null,
                [ToolFixture.Counter(), ToolFixture.Transform()]));
        }, ScriptedProvider.Final]);
        var agent = ToolFixture.Agent(provider, ToolFixture.Bindings(new(maximumResultBytes: 64), counter, new TransformTool(), transform), hooks: hooks);
        var request = new AgentRequest(Guid.NewGuid(), "synthetic", [], new(8, TimeSpan.FromSeconds(10)), Required,
            new(maximumLogicalCalls: dispatches, maximumPhysicalDispatches: dispatches, inputTokenThreshold: 5, maximumToolInvocations: toolLimit));
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s)); var progress = new List<AgentProgress>();
        var outcome = candidate ? (await ((ICandidateAgent)agent).ExecuteCandidatesAsync(new(request, new(2, 1, 1)), host, new InlineProgress(progress.Add))).Outcome
            : await agent.ExecuteAsync(request, new InlineProgress(progress.Add));
        var expected = toolLimit == 1 || dispatches == 1 || input >= 5 ? AgentTerminationReason.ResourceLimit
            : input is null ? AgentTerminationReason.Partial : AgentTerminationReason.Completed;
        var turns = expected == AgentTerminationReason.Completed ? 2 : 1;
        Assert.Equal(Required, agent.SupportedCapabilities & Required);
        Assert.Equal(expected, outcome.Reason); Assert.Equal(turns, provider.Effects); Assert.Equal(turns, outcome.CompletedWorkUnits);
        Assert.Equal(turns, hooks.Exposures.Count); Assert.Equal(turns, hooks.Settlements.Count);
        Assert.Equal(toolLimit == 2 ? 1 : 0, counter.Effects); Assert.Equal(toolLimit == 2 ? 1 : 0, transform.Effects);
        ToolAllowanceTests.Counts(outcome.Usage, toolLimit == 2 ? 2 : 0, 0, 0);
        Assert.Equal(turns, outcome.Usage!.Attempts.Count);
        Assert.Equal(input is null ? TokenObservationCoverage.Unavailable : TokenObservationCoverage.Complete, outcome.Usage.InputTokens.Coverage);
        Assert.Equal(input is null ? null : input + (turns == 2 ? 3 : 0), outcome.Usage.InputTokens.ObservedTokens);
        Assert.Equal(turns * 2, outcome.Usage.OutputTokens.ObservedTokens);
        Assert.Equal(candidate && turns == 2 ? 1 : 0, host.Submissions.Count);
        if (toolLimit == 1) Assert.Empty(progress);
        else
        {
            ToolAllowanceTests.Counts(progress[0].Usage, 2, 0, 0);
            Assert.Equal(input, progress[0].Usage!.InputTokens.ObservedTokens);
            Assert.Single(progress[0].Usage!.Attempts);
        }
    }

    [Fact]
    public async Task FinalNoSendRefundAllowsToolProductionButDoesNotRefundItsDispatchedAttempt()
    {
        var calls = 0; var counter = new CounterCapability(); var transform = new TransformCapability();
        var provider = new DelegateProvider(new("p", "m"), (r, o, _) =>
        {
            if (++calls == 1)
            {
                o.ObserveDispatch(DispatchExposure.NotDispatched);
                return ValueTask.FromResult(RuntimeFixture.Final(r));
            }
            o.ObserveDispatch(DispatchExposure.Dispatched);
            return ValueTask.FromResult(new ProviderResponse(r.Scope, r.Attempt, ProviderFinish.ToolCalls, null,
                [ToolFixture.Counter(), ToolFixture.Transform()]));
        });
        var hooks = new RuntimeHooks();
        var agent = (ICandidateAgent)RuntimeAgentFactory.Create(new(provider,
            ToolFixture.Bindings(new(maximumResultBytes: 64), counter, new TransformTool(), transform), hooks));
        var request = new AgentRequest(Guid.NewGuid(), "synthetic", [], new(8, TimeSpan.FromSeconds(10)), Required,
            new(maximumLogicalCalls: 3, maximumPhysicalDispatches: 1, inputTokenThreshold: 5, maximumToolInvocations: 2));
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s, instruction: CandidateContinuation.Continue));
        var result = await agent.ExecuteCandidatesAsync(new(request, new(3, 1, 1)), host);
        Assert.Equal(CandidateStopReason.RuntimeLimit, result.StopReason);
        Assert.Equal(2, calls); Assert.Equal(2, hooks.Exposures.Count); Assert.Equal(2, hooks.Settlements.Count);
        Assert.Single(host.Submissions); Assert.Single(result.Receipts); Assert.Equal(1, result.ContinuationsAdmitted);
        Assert.Equal(1, counter.Effects); Assert.Equal(1, transform.Effects); ToolAllowanceTests.Counts(result.Outcome.Usage, 2, 0, 0);
        Assert.Equal(new[] { DispatchExposure.NotDispatched, DispatchExposure.Dispatched }, result.Outcome.Usage!.Attempts.Select(a => a.Exposure));
        Assert.Equal(TokenObservationCoverage.Partial, result.Outcome.Usage.InputTokens.Coverage);
        Assert.Equal(0, result.Outcome.Usage.InputTokens.ObservedTokens);
        Assert.All(result.Outcome.Usage.Attempts, a => Assert.Null(a.Usage.InputTokens));
    }
}

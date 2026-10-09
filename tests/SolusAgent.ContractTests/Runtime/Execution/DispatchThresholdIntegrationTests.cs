using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Candidates;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Execution;

public sealed class DispatchThresholdIntegrationTests
{
    private static AgentRequest Request(int? logical, int? physical, long threshold = 5) =>
        new(Guid.NewGuid(), "i", [], new(8, TimeSpan.FromSeconds(10)),
            AgentCapability.DispatchLimits | AgentCapability.UsageThresholds, new(logical, physical, inputTokenThreshold: threshold));

    public static IEnumerable<object?[]> Boundaries()
    {
        foreach (var candidates in new[] { false, true })
        foreach (var physical in new[] { false, true })
        foreach (var count in new[] { 1, 2 })
        foreach (var input in new long?[] { null, 0, 4, 5, 6 }) yield return [candidates, physical, count, input];
    }

    [Theory, MemberData(nameof(Boundaries))]
    public async Task KnownCountExhaustionWinsBeforeTokenComparisonAfterAcceptedToolBatch(bool candidates, bool physical, int count, long? input)
    {
        var cap = new ConfigurationCapability(); var tool = new ConfigurationTool(); var hooks = new RuntimeHooks();
        var guarded = new ScriptedProvider([(r, o, _) =>
        {
            o.CaptureUsage(new(input, null));
            return ValueTask.FromResult(new ProviderResponse(r.Scope, r.Attempt, ProviderFinish.ToolCalls, null,
                [new ToolCall("call", tool.Descriptor.Name, "{\"text\":\"data\"}")]));
        }, ScriptedProvider.Final]);
        var provider = new InterfaceScriptedProvider(guarded.Scope, guarded.ExchangeAsync);
        var agent = RuntimeAgentFactory.Create(new(provider, [new(tool, cap)], hooks));
        var request = Request(physical ? null : count, physical ? count : null);
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s));
        var outcome = candidates ? (await ((ICandidateAgent)agent).ExecuteCandidatesAsync(new(request, new(8, 8, 8)), host)).Outcome
            : await agent.ExecuteAsync(request);
        var expected = count == 1 || input >= 5 ? AgentTerminationReason.ResourceLimit
            : input is null ? AgentTerminationReason.Partial : AgentTerminationReason.Completed;
        var admitted = expected == AgentTerminationReason.Completed ? 2 : 1;
        Assert.Equal(expected, outcome.Reason); Assert.Equal(admitted, provider.Calls); Assert.Equal(admitted, guarded.Effects);
        Assert.Equal(admitted, hooks.Exposures.Count); Assert.Equal(admitted, hooks.Settlements.Count);
        Assert.Equal(1, cap.Effects); Assert.Equal(admitted, outcome.CompletedWorkUnits);
        Assert.Equal(admitted, outcome.Usage!.Attempts.Count); Assert.Equal(UsageInventoryCoverage.Complete, outcome.Usage.Coverage);
        Assert.Equal(input is null ? TokenObservationCoverage.Unavailable : TokenObservationCoverage.Complete, outcome.Usage.InputTokens.Coverage);
        Assert.Equal(candidates && expected == AgentTerminationReason.Completed ? 1 : 0, host.Submissions.Count);
    }

    [Theory]
    [InlineData(false, null)] [InlineData(true, null)]
    [InlineData(false, 5L)] [InlineData(true, 5L)] [InlineData(false, 6L)] [InlineData(true, 6L)]
    public async Task AcceptedFinalAtCountBoundaryStillCompletesWithReachedOrUnknownTokens(bool candidates, long? input)
    {
        var guarded = new ScriptedProvider([(r, o, _) =>
        { o.CaptureUsage(new(input, null)); return ValueTask.FromResult(RuntimeFixture.Final(r)); }]);
        var provider = new InterfaceScriptedProvider(guarded.Scope, guarded.ExchangeAsync);
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s)); var agent = RuntimeFixture.Agent(provider);
        var outcome = candidates ? (await ((ICandidateAgent)agent).ExecuteCandidatesAsync(new(Request(1, 1), new(8, 8, 8)), host)).Outcome
            : await agent.ExecuteAsync(Request(1, 1));
        Assert.Equal(AgentTerminationReason.Completed, outcome.Reason); Assert.Equal(1, outcome.CompletedWorkUnits);
        Assert.Equal(1, provider.Calls); Assert.Single(outcome.Usage!.Attempts); Assert.Equal(candidates ? 1 : 0, host.Submissions.Count);
    }

    [Theory]
    [InlineData(false, null)] [InlineData(true, null)]
    [InlineData(false, 4L)] [InlineData(true, 4L)] [InlineData(false, 5L)] [InlineData(true, 5L)]
    public async Task CandidateCountPreflightPrecedesCorrectionRetentionAndMissingTokenStop(bool physical, long? input)
    {
        var guarded = new ScriptedProvider([(r, o, _) =>
        { o.CaptureUsage(new(input, null)); return ValueTask.FromResult(RuntimeFixture.Final(r)); }]);
        var provider = new InterfaceScriptedProvider(guarded.Scope, guarded.ExchangeAsync);
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s, CandidateDecision.Reject,
            CandidateContinuation.Continue, new string('x', 300)));
        var result = await CandidateFixture.Agent(provider, options: new(maximumRetainedBytes: 256)).ExecuteCandidatesAsync(
            new(Request(physical ? null : 1, physical ? 1 : null), new(8, 8, 8)), host);
        Assert.Equal(CandidateStopReason.RuntimeLimit, result.StopReason); Assert.Equal(AgentTerminationReason.ResourceLimit, result.Outcome.Reason);
        Assert.Equal(1, provider.Calls); Assert.Single(host.Submissions); Assert.Single(result.Receipts);
        Assert.Equal(0, result.RepairsAdmitted); Assert.Equal(1, result.Outcome.CompletedWorkUnits);
    }

    [Fact]
    public async Task NoSendRefundAndZeroAggregatePermitNextProductionUnderBothGuarantees()
    {
        var calls = 0;
        var guarded = new DelegateProvider(new("p", "m"), (r, o, _) =>
        {
            o.ObserveDispatch(++calls == 1 ? DispatchExposure.NotDispatched : DispatchExposure.Dispatched);
            if (calls > 1) o.CaptureUsage(new(5, 0));
            return ValueTask.FromResult(RuntimeFixture.Final(r));
        });
        var provider = new InterfaceScriptedProvider(guarded.Scope, guarded.ExchangeAsync); var hooks = new RuntimeHooks();
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s, instruction: CandidateContinuation.Continue));
        var result = await CandidateFixture.Agent(provider, hooks).ExecuteCandidatesAsync(new(Request(null, 1), new(8, 8, 8)), host);
        Assert.Equal(CandidateStopReason.RuntimeLimit, result.StopReason); Assert.Equal(2, provider.Calls);
        Assert.Equal(2, hooks.Exposures.Count); Assert.Equal(2, host.Submissions.Count); Assert.Equal(2, result.Outcome.CompletedWorkUnits);
        Assert.Equal(DispatchExposure.NotDispatched, result.Outcome.Usage!.Attempts[0].Exposure);
        Assert.Null(result.Outcome.Usage.Attempts[0].Usage.InputTokens);
        Assert.Equal(5, result.Outcome.Usage.InputTokens.ObservedTokens);
        Assert.Equal(TokenObservationCoverage.Complete, result.Outcome.Usage.InputTokens.Coverage);
    }

    [Theory]
    [InlineData(false, false, 2)] [InlineData(true, false, 2)] [InlineData(false, true, 2)] [InlineData(true, true, 2)]
    [InlineData(false, false, 3)] [InlineData(true, false, 3)] [InlineData(false, true, 3)] [InlineData(true, true, 3)]
    public async Task CountExhaustionAlsoWinsOverOverflowWithoutSuppressingCurrentToolBatch(bool candidates, bool physical, int count)
    {
        var cap = new ConfigurationCapability(); var tool = new ConfigurationTool(); var hooks = new RuntimeHooks();
        ValueTask<ProviderResponse> Step(ProviderRequest r, ProviderObservation o, long input, string call)
        {
            o.CaptureUsage(new(input, 0));
            return ValueTask.FromResult(new ProviderResponse(r.Scope, r.Attempt, ProviderFinish.ToolCalls, null,
                [new ToolCall(call, tool.Descriptor.Name, "{\"text\":\"data\"}")]));
        }
        var guarded = new ScriptedProvider([(r, o, _) => Step(r, o, long.MaxValue - 1, "a"),
            (r, o, _) => Step(r, o, 2, "b"), ScriptedProvider.Final]);
        var provider = new InterfaceScriptedProvider(guarded.Scope, guarded.ExchangeAsync);
        var agent = RuntimeAgentFactory.Create(new(provider, [new(tool, cap)], hooks));
        var request = Request(physical ? null : count, physical ? count : null, long.MaxValue);
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s));
        var outcome = candidates ? (await ((ICandidateAgent)agent).ExecuteCandidatesAsync(new(request, new(8, 8, 8)), host)).Outcome
            : await agent.ExecuteAsync(request);
        Assert.Equal(count == 2 ? AgentTerminationReason.ResourceLimit : AgentTerminationReason.Partial, outcome.Reason);
        Assert.Equal(2, provider.Calls); Assert.Equal(2, cap.Effects); Assert.Equal(2, outcome.CompletedWorkUnits);
        Assert.Equal(2, hooks.Exposures.Count); Assert.Empty(host.Submissions);
        Assert.Equal(TokenObservationCoverage.Overflow, outcome.Usage!.InputTokens.Coverage);
        Assert.Null(outcome.Usage.InputTokens.ObservedTokens); Assert.Equal(2, outcome.Usage.Attempts.Count);
    }
}

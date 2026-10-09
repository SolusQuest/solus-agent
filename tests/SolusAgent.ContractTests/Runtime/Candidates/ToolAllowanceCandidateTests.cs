using CustomTools;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Tools;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Candidates;

public sealed class ToolAllowanceCandidateTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FollowOnAllowanceIsChargedOnceWhileToolBudgetSpansEpisodes(bool repair)
    {
        var cap = new CounterCapability(); var transform = new TransformCapability();
        var provider = new ScriptedProvider([
            (r, o, _) => ToolFixture.Calls(r, o, [ToolFixture.Counter("first")]), ScriptedProvider.Final,
            (r, o, _) => ToolFixture.Calls(r, o, [ToolFixture.Transform("next"), ToolFixture.Counter("unstarted_prefix")])]);
        var agent = (ICandidateAgent)ToolFixture.Agent(provider,
            ToolFixture.Bindings(new(maximumResultBytes: 64), cap, new TransformTool(), transform));
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s,
            repair ? CandidateDecision.Reject : CandidateDecision.Accept, CandidateContinuation.Continue, repair ? "correction" : null));
        var result = await agent.ExecuteCandidatesAsync(new(ToolAllowanceTests.Request(2), new(4, 1, 1)), host);
        Assert.Equal(CandidateStopReason.RuntimeLimit, result.StopReason); Assert.Equal(AgentTerminationReason.ResourceLimit, result.Outcome.Reason);
        Assert.Single(host.Submissions); Assert.Single(result.Receipts);
        Assert.Equal(repair ? 1 : 0, result.RepairsAdmitted); Assert.Equal(repair ? 0 : 1, result.ContinuationsAdmitted);
        Assert.Equal(3, result.Outcome.CompletedWorkUnits); Assert.Equal(3, provider.Effects);
        Assert.Equal(1, cap.Effects); Assert.Equal(0, transform.Effects);
        ToolAllowanceTests.Counts(result.Outcome.Usage, 1, 0, 0);
        Assert.Single(provider.LastRequest!.Inputs, input => input.ToolResult is not null);
        if (repair) Assert.Single(provider.LastRequest.Inputs, input => input.Text == "correction");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OneModelWorkUnitStillAllowsACompleteAdmittedTwoToolBatch(bool candidate)
    {
        var cap = new CounterCapability(); var transform = new TransformCapability();
        var provider = new ScriptedProvider([(r, o, _) => ToolFixture.Calls(r, o, [ToolFixture.Counter(), ToolFixture.Transform()])]);
        var outcome = await ToolAllowanceTests.Run(ToolFixture.Agent(provider,
            ToolFixture.Bindings(new(maximumResultBytes: 64), cap, new TransformTool(), transform)), ToolAllowanceTests.Request(2, units: 1), candidate);
        Assert.Equal(AgentTerminationReason.ResourceLimit, outcome.Reason); Assert.Equal(1, outcome.CompletedWorkUnits);
        Assert.Equal(1, cap.Effects); Assert.Equal(1, transform.Effects); Assert.Equal(1, provider.Effects);
        ToolAllowanceTests.Counts(outcome.Usage, 2, 0, 0);
    }
}

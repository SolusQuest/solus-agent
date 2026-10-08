using AprHost;
using CustomTools;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.ContractTests.Runtime.Tools;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Consumption;

public sealed class EpisodeAccountingTests
{
    [Fact]
    public async Task RepairEpisodeWithIntermediateToolTurnsChargesTheCorrectionAndRepairOnce()
    {
        var requests = new List<ProviderRequest>();
        var steps = new Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>>[]
        {
            (r, o, _) => { requests.Add(r); return ConsumptionFixture.Final(r, o, "apr-item:initial:1", ConsumptionFixture.TurnReplay(1)); },
            (r, o, _) => { requests.Add(r); return ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("c1", 2)], ConsumptionFixture.TurnReplay(2)); },
            (r, o, _) => { requests.Add(r); return ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Transform("t1", "x")], ConsumptionFixture.TurnReplay(3)); },
            (r, o, _) => { requests.Add(r); return ConsumptionFixture.Final(r, o, "apr-item:fixed:" + ConsumptionFixture.CorrectedValue(r), ConsumptionFixture.TurnReplay(4)); },
            (r, o, _) => { requests.Add(r); return ConsumptionFixture.Final(r, o, "apr-item:final:1", ConsumptionFixture.TurnReplay(5)); },
        };
        var startup = new ProductionStartup(steps, options: new RuntimeOptions(requireContinuation: true));
        var host = new AprBusinessHost(startup.Agent, startup.Agent);
        var channel = new ExchangeChannel([
            new ExchangePolicy(false, CandidateContinuation.Continue, CorrectedValue: 9),
            new ExchangePolicy(true, CandidateContinuation.Continue),
            new ExchangePolicy(true, CandidateContinuation.End)]);
        var result = await host.ExecuteCandidatesAsync(
            ConsumptionFixture.Candidates(ConsumptionFixture.Request(5), submissions: 3, repairs: 1, continuations: 1), channel);

        Assert.Equal(CandidateStopReason.Completed, result.StopReason);
        Assert.Equal(5, result.Outcome.CompletedWorkUnits);
        Assert.Equal(1, result.RepairsAdmitted); Assert.Equal(1, result.ContinuationsAdmitted);
        Assert.Equal(5, startup.Provider.Effects); Assert.Equal(1, startup.CounterCapability.Effects);
        Assert.Equal(1, startup.TransformCapability.Effects);
        var correction = ConsumptionFixture.Correction(9);
        Assert.DoesNotContain(requests[0].Inputs, i => i.Text == correction);
        foreach (var request in requests.Skip(1))
        {
            // One follow-on episode inserts its correction once even across several intermediate tool turns.
            Assert.Equal(1, request.Inputs.Count(i => i.Text == correction));
        }

        Assert.Equal("apr-item:fixed:9", channel.Submissions[1].Payload);
        Assert.Equal(channel.Submissions[0].SubmissionId, channel.Submissions[1].RepairsSubmissionId);
    }

    [Fact]
    public async Task ContinuationEpisodeWithIntermediateToolTurnsChargesTheContinuationOnce()
    {
        var requests = new List<ProviderRequest>();
        var steps = new Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>>[]
        {
            (r, o, _) => { requests.Add(r); return ConsumptionFixture.Final(r, o, "apr-item:initial:1", ConsumptionFixture.TurnReplay(1)); },
            (r, o, _) => { requests.Add(r); return ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("c1", 2)], ConsumptionFixture.TurnReplay(2)); },
            (r, o, _) => { requests.Add(r); return ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Transform("t1", "x")], ConsumptionFixture.TurnReplay(3)); },
            (r, o, _) => { requests.Add(r); return ConsumptionFixture.Final(r, o, "apr-item:final:" + ConsumptionFixture.CounterTotal(r, "c1"), ConsumptionFixture.TurnReplay(4)); },
        };
        var startup = new ProductionStartup(steps, options: new RuntimeOptions(requireContinuation: true));
        var host = new AprBusinessHost(startup.Agent, startup.Agent);
        var channel = new ExchangeChannel([
            new ExchangePolicy(true, CandidateContinuation.Continue),
            new ExchangePolicy(true, CandidateContinuation.End)]);
        var result = await host.ExecuteCandidatesAsync(
            ConsumptionFixture.Candidates(ConsumptionFixture.Request(4), submissions: 2, repairs: 1, continuations: 1), channel);

        Assert.Equal(CandidateStopReason.Completed, result.StopReason);
        Assert.Equal(4, result.Outcome.CompletedWorkUnits);
        Assert.Equal(0, result.RepairsAdmitted); Assert.Equal(1, result.ContinuationsAdmitted);
        Assert.Equal(4, startup.Provider.Effects); Assert.Equal(1, startup.CounterCapability.Effects);
        Assert.Equal(1, startup.TransformCapability.Effects);
        Assert.All(requests, r => Assert.Equal(1, r.Inputs.Count(i => i.Kind == ProviderInputKind.InputData)));
        Assert.Equal("apr-item:final:2", channel.Submissions[1].Payload);
        Assert.Null(channel.Submissions[1].RepairsSubmissionId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task WorkUnitCeilingIsLiteralBeforeEveryModelAdmissionAfterToolTurns(int units)
    {
        var steps = new Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>>[]
        {
            (r, o, _) => ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("c1", 2)], ConsumptionFixture.TurnReplay(1)),
            (r, o, _) => ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("c2", 3)], ConsumptionFixture.TurnReplay(2)),
        };
        var startup = new ProductionStartup(steps, options: new RuntimeOptions(requireContinuation: true));
        var host = new AprBusinessHost(startup.Agent, startup.Agent);
        var channel = new ExchangeChannel([new ExchangePolicy(true, CandidateContinuation.Continue)]);
        var result = await host.ExecuteCandidatesAsync(ConsumptionFixture.Candidates(ConsumptionFixture.Request(units)), channel);

        // Tools execute inside the final allowed accepted model response, then the next model admission is denied.
        Assert.Equal(CandidateStopReason.WorkUnitLimit, result.StopReason);
        Assert.Equal(AgentTerminationReason.ResourceLimit, result.Outcome.Reason);
        Assert.Equal(units, result.Outcome.CompletedWorkUnits);
        Assert.Equal(units, startup.Provider.Effects);
        Assert.Equal(units, startup.CounterCapability.Effects);
        Assert.Empty(result.Receipts); Assert.Empty(channel.Submissions);
    }

    [Fact]
    public async Task SubmissionCeilingDoesNotCountToolTurnsAndEndSucceedsAtExactCeilings()
    {
        var steps = new Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>>[]
        {
            (r, o, _) => ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("c1", 1)], ConsumptionFixture.TurnReplay(1)),
            (r, o, _) => ConsumptionFixture.Final(r, o, "apr-item:initial:" + ConsumptionFixture.CounterTotal(r, "c1"), ConsumptionFixture.TurnReplay(2)),
            (r, o, _) => ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("c2", 1)], ConsumptionFixture.TurnReplay(3)),
            (r, o, _) => ConsumptionFixture.Final(r, o, "apr-item:fixed:" + ConsumptionFixture.CorrectedValue(r), ConsumptionFixture.TurnReplay(4)),
        };
        var startup = new ProductionStartup(steps, options: new RuntimeOptions(requireContinuation: true));
        var host = new AprBusinessHost(startup.Agent, startup.Agent);
        var channel = new ExchangeChannel([
            new ExchangePolicy(false, CandidateContinuation.Continue, CorrectedValue: 3),
            new ExchangePolicy(true, CandidateContinuation.End)]);
        var result = await host.ExecuteCandidatesAsync(
            ConsumptionFixture.Candidates(ConsumptionFixture.Request(4), submissions: 2, repairs: 1, continuations: 1), channel);

        Assert.Equal(CandidateStopReason.Completed, result.StopReason);
        Assert.Equal(4, result.Outcome.CompletedWorkUnits);
        Assert.Equal(2, result.Receipts.Count); Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(2, startup.CounterCapability.Effects);
        Assert.Equal(1, result.RepairsAdmitted);
    }

    [Theory]
    [InlineData(true, CandidateStopReason.ContinuationLimit)]
    [InlineData(false, CandidateStopReason.RepairLimit)]
    public async Task ZeroFollowOnAllowanceBlocksBeforeAnyNewProviderOrToolEffects(bool accepted, CandidateStopReason stop)
    {
        var steps = new Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>>[]
        {
            (r, o, _) => ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("c1", 1)], ConsumptionFixture.TurnReplay(1)),
            (r, o, _) => ConsumptionFixture.Final(r, o, "apr-item:initial:" + ConsumptionFixture.CounterTotal(r, "c1"), ConsumptionFixture.TurnReplay(2)),
        };
        var startup = new ProductionStartup(steps, options: new RuntimeOptions(requireContinuation: true));
        var host = new AprBusinessHost(startup.Agent, startup.Agent);
        var channel = new ExchangeChannel([
            new ExchangePolicy(accepted, CandidateContinuation.Continue, CorrectedValue: 4)]);
        var result = await host.ExecuteCandidatesAsync(
            ConsumptionFixture.Candidates(ConsumptionFixture.Request(), submissions: 2, repairs: 0, continuations: 0), channel);

        Assert.Equal(stop, result.StopReason);
        Assert.Equal(2, result.Outcome.CompletedWorkUnits);
        Assert.Equal(2, startup.Provider.Effects); Assert.Equal(1, startup.CounterCapability.Effects);
        Assert.Equal(accepted ? 1 : 0, result.AcceptedCount);
        Assert.Equal(0, result.RepairsAdmitted); Assert.Equal(0, result.ContinuationsAdmitted);
        Assert.Single(result.Receipts); Assert.Single(channel.Submissions);
    }
}

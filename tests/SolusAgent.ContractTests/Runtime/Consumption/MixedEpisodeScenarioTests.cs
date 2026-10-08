using System.Text;
using AprHost;
using CustomTools;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.ConsumerProbes.ScribeHost;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.ContractTests.Runtime.Tools;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Consumption;

public sealed class MixedEpisodeScenarioTests
{
    [Fact]
    public async Task AprHostRunsFiveTurnToolCandidateMixedProductionInOneInvocation()
    {
        var requests = new List<ProviderRequest>();
        var steps = new Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>>[]
        {
            (r, o, _) => { requests.Add(r); Assert.Null(r.Continuation);
                return ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("c1", 7)], ConsumptionFixture.TurnReplay(1)); },
            (r, o, _) => { requests.Add(r); Assert.Equal(ConsumptionFixture.TurnReplay(1), ConsumptionFixture.Replay(r));
                return ConsumptionFixture.Final(r, o, "apr-item:initial:" + ConsumptionFixture.CounterTotal(r, "c1"),
                    ConsumptionFixture.TurnReplay(2)); },
            (r, o, _) => { requests.Add(r); Assert.Equal(ConsumptionFixture.TurnReplay(2), ConsumptionFixture.Replay(r));
                return ConsumptionFixture.Final(r, o, "apr-item:fixed:" + ConsumptionFixture.CorrectedValue(r),
                    ConsumptionFixture.TurnReplay(3)); },
            (r, o, _) => { requests.Add(r); Assert.Equal(ConsumptionFixture.TurnReplay(3), ConsumptionFixture.Replay(r));
                return ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("c2", 3)], ConsumptionFixture.TurnReplay(4)); },
            (r, o, _) => { requests.Add(r); Assert.Equal(ConsumptionFixture.TurnReplay(4), ConsumptionFixture.Replay(r));
                return ConsumptionFixture.Final(r, o, "apr-item:final:" + ConsumptionFixture.CounterTotal(r, "c2"),
                    ConsumptionFixture.TurnReplay(5)); },
        };
        var startup = new ProductionStartup(steps, options: new RuntimeOptions(requireContinuation: true));
        var host = new AprBusinessHost(startup.Agent, startup.Agent);
        var channel = new ExchangeChannel([
            new ExchangePolicy(false, CandidateContinuation.Continue, CorrectedValue: 42),
            new ExchangePolicy(true, CandidateContinuation.Continue),
            new ExchangePolicy(true, CandidateContinuation.End)]);
        var progress = new List<AgentProgress>();
        var result = await host.ExecuteCandidatesAsync(
            ConsumptionFixture.Candidates(ConsumptionFixture.Request(5), submissions: 3, repairs: 1, continuations: 1),
            channel, new InlineProgress(progress.Add));

        Assert.Equal(CandidateStopReason.Completed, result.StopReason);
        Assert.Equal(AgentTerminationReason.Completed, result.Outcome.Reason);
        Assert.Equal(5, result.Outcome.CompletedWorkUnits);
        Assert.Equal(3, result.Receipts.Count);
        Assert.Equal(CandidateDecision.Reject, result.Receipts[0].Decision); Assert.Equal(CandidateContinuation.Continue, result.Receipts[0].Continuation);
        Assert.True(result.Receipts[1].IsAccepted); Assert.True(result.Receipts[2].IsAccepted);
        Assert.Equal(CandidateContinuation.End, result.Receipts[2].Continuation);
        Assert.Equal(1, result.RepairsAdmitted); Assert.Equal(1, result.ContinuationsAdmitted); Assert.Equal(2, result.AcceptedCount);

        var submissions = channel.Submissions;
        Assert.Equal(3, submissions.Count); Assert.Equal(3, submissions.Select(s => s.SubmissionId).Distinct().Count());
        Assert.Null(submissions[0].RepairsSubmissionId);
        Assert.Equal(submissions[0].SubmissionId, submissions[1].RepairsSubmissionId);
        Assert.Null(submissions[2].RepairsSubmissionId);
        Assert.Equal("apr-item:initial:7", submissions[0].Payload);
        Assert.Equal("apr-item:fixed:42", submissions[1].Payload);
        Assert.Equal("apr-item:final:10", submissions[2].Payload);

        // Tool output causally supplies the accepted values and the Host correction causally supplies the repair.
        Assert.Equal(2, startup.CounterCapability.Effects); Assert.Equal(0, startup.TransformCapability.Effects);
        Assert.Equal(2, host.Acceptance.AcceptedCount);
        Assert.Equal(42, host.Acceptance.Accepted[0].Item.Value);
        Assert.Equal(submissions[0].SubmissionId, host.Acceptance.Accepted[0].RepairsSubmissionId);
        Assert.Equal(10, host.Acceptance.Accepted[1].Item.Value);

        // Runtime completion is not business completion: effects stay zero until the explicit Host operation.
        Assert.Equal(0, host.Acceptance.EffectCount);
        Assert.Equal(2, host.Acceptance.ApplyEffects());
        Assert.Equal(0, host.Acceptance.ApplyEffects());
        Assert.Equal(2, host.Acceptance.EffectCount);

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, progress.Select(p => p.CompletedWorkUnits));
        Assert.Equal(5, result.Outcome.Usage!.Attempts.Count);
        Assert.Equal(5, result.Outcome.Usage.Attempts.Select(a => a.PhysicalAttemptId).Distinct().Count());
        Assert.Equal(5, result.Outcome.Usage.Attempts.Select(a => a.LogicalCallId).Distinct().Count());
        Assert.All(result.Outcome.Usage.Attempts, a => Assert.Equal(3, a.Usage.InputTokens));

        Assert.Equal(5, requests.Count);
        Assert.All(requests, r => Assert.Equal(ProviderInputKind.HostInstruction, r.Inputs[0].Kind));
        Assert.All(requests, r => Assert.Equal(ConsumptionFixture.Instructions, r.Inputs[0].Text));
        Assert.All(requests, r => Assert.Equal(new[] { "counter", "transform" }, r.Tools.Select(t => t.Name)));
        Assert.All(requests, r => Assert.Equal(new[] { "counter_increment", "text_transform" }, r.Tools.Select(t => t.CapabilityId)));
        Assert.Equal(ProviderInputKind.InputData, requests[0].Inputs[1].Kind);
        Assert.Equal(ConsumptionFixture.InstructionLikeData, requests[0].Inputs[1].Text);
        Assert.Equal(new[] { ProviderInputKind.HostInstruction, ProviderInputKind.InputData, ProviderInputKind.ModelData,
            ProviderInputKind.ToolResultData }, requests[1].Inputs.Select(i => i.Kind));
        Assert.Equal(new[] { ProviderInputKind.HostInstruction, ProviderInputKind.InputData, ProviderInputKind.ModelData,
            ProviderInputKind.ToolResultData, ProviderInputKind.ModelData, ProviderInputKind.InputData }, requests[2].Inputs.Select(i => i.Kind));
        Assert.Equal("apr-item:initial:7", requests[2].Inputs[4].Model!.Text);
        Assert.Equal(ConsumptionFixture.Correction(42), requests[2].Inputs[^1].Text);
        var recorded = ConsumptionFixture.ToolResult(requests[4], "c1");
        Assert.Equal("c1", recorded.Call.CallId); Assert.Equal("counter", recorded.Call.ToolName);
        Assert.Equal("{\"amount\":7}", recorded.Call.ArgumentsJson);
        Assert.Equal(new[] { "c1", "c2" }, requests[4].Inputs.Where(i => i.ToolResult is not null).Select(i => i.ToolResult!.Call.CallId));
    }

    [Fact]
    public async Task ScribeHostRunsFiveTurnToolCandidateMixedProductionInOneFreshInvocation()
    {
        var requests = new List<ProviderRequest>();
        var steps = new Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>>[]
        {
            (r, o, _) => { requests.Add(r); Assert.Null(r.Continuation);
                return ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Transform("t1", "fact one")], ConsumptionFixture.TurnReplay(1)); },
            (r, o, _) => { requests.Add(r); Assert.Equal(ConsumptionFixture.TurnReplay(1), ConsumptionFixture.Replay(r));
                return ConsumptionFixture.Final(r, o, ScribeCandidatePayload.Format("alpha", ConsumptionFixture.TransformText(r, "t1")),
                    ConsumptionFixture.TurnReplay(2)); },
            (r, o, _) => { requests.Add(r); Assert.Equal(ConsumptionFixture.TurnReplay(2), ConsumptionFixture.Replay(r));
                Assert.True(ScribeCandidateCorrection.TryParseRequestedFact(ConsumptionFixture.CorrectedData(r), out var requested));
                return ConsumptionFixture.Final(r, o, ScribeCandidatePayload.Format("alpha", requested), ConsumptionFixture.TurnReplay(3)); },
            (r, o, _) => { requests.Add(r); Assert.Equal(ConsumptionFixture.TurnReplay(3), ConsumptionFixture.Replay(r));
                return ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Transform("t2", "fact two")], ConsumptionFixture.TurnReplay(4)); },
            (r, o, _) => { requests.Add(r); Assert.Equal(ConsumptionFixture.TurnReplay(4), ConsumptionFixture.Replay(r));
                return ConsumptionFixture.Final(r, o, ScribeCandidatePayload.Format("beta", ConsumptionFixture.TransformText(r, "t2")),
                    ConsumptionFixture.TurnReplay(5)); },
        };
        var startup = new ProductionStartup(steps, options: new RuntimeOptions(requireContinuation: true));
        var manifest = new ScribeManifest(["alpha", "beta"]);
        var control = new ScribeHostControl(ConsumptionFixture.Instructions,
            new AgentExecutionBounds(5, TimeSpan.FromMinutes(1)), new CandidateExecutionBounds(3, 1, 1));
        var host = new ScribeBusinessHost(manifest, new ScribeProgress(manifest, []), control, ExchangePlans.Scribe([
            new ExchangePolicy(false, CandidateContinuation.Continue, CorrectedFact: "correction-derived fact"),
            new ExchangePolicy(true, CandidateContinuation.Continue),
            new ExchangePolicy(true, CandidateContinuation.End)]));
        var progress = new List<AgentProgress>();
        var result = await host.ExecuteFreshCandidatesAsync(startup.Agent, new InlineProgress(progress.Add));

        Assert.Equal(CandidateStopReason.Completed, result.StopReason);
        Assert.Equal(5, result.Outcome.CompletedWorkUnits);
        Assert.Equal(1, result.RepairsAdmitted); Assert.Equal(1, result.ContinuationsAdmitted); Assert.Equal(2, result.AcceptedCount);
        Assert.All(result.Receipts, receipt => Assert.Equal(host.CurrentExecutionId, receipt.ExecutionId));
        Assert.Equal(new[] { false, true, true }, host.Exchanges.Select(e => e.Accepted));
        Assert.Equal(ScribePayloadValidation.Valid, host.Exchanges[0].Validation);
        Assert.Equal(host.Exchanges[0].SubmissionId, host.Exchanges[1].RepairsSubmissionId);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, progress.Select(p => p.CompletedWorkUnits));

        // The Host derives its accepted facts from the actual tool results and the correction-derived repair.
        Assert.Equal(new[] { "alpha", "beta" }, host.Progress.AcceptedFacts.Select(f => f.Member));
        Assert.Equal("correction-derived fact", host.Progress.AcceptedFacts[0].Text);
        Assert.Equal("FACT TWO", host.Progress.AcceptedFacts[1].Text);
        Assert.True(host.Progress.IsComplete);
        Assert.Equal(0, host.ExternalEffects);
        Assert.Equal(2, startup.TransformCapability.Effects); Assert.Equal(0, startup.CounterCapability.Effects);
        Assert.Equal(5, requests.Count);
        Assert.All(requests, r => Assert.Equal(ConsumptionFixture.Instructions, r.Inputs[0].Text));
    }

    [Theory]
    [InlineData("apr")]
    [InlineData("scribe")]
    public async Task ChangedCorrectionMetamorphicallyChangesTheRepairPayload(string hostKind)
    {
        var first = await Run(hostKind, 41, "fact from correction one");
        var second = await Run(hostKind, 42, "fact from correction two");
        Assert.NotEqual(first, second);
        Assert.Equal(hostKind == "apr" ? "apr-item:fixed:41" : "alpha|fact from correction one", first);
        Assert.Equal(hostKind == "apr" ? "apr-item:fixed:42" : "alpha|fact from correction two", second);
        Assert.DoesNotContain("PRE_SCRIPTED", first); Assert.DoesNotContain("PRE_SCRIPTED", second);

        static async Task<string> Run(string hostKind, long value, string fact)
        {
            var steps = new Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>>[]
            {
                (r, o, _) => ConsumptionFixture.Final(r, o, hostKind == "apr" ? "apr-item:initial:PRE_SCRIPTED" : "PRE_SCRIPTED", ConsumptionFixture.TurnReplay(1)),
                (r, o, _) => ConsumptionFixture.Final(r, o, hostKind == "apr"
                    ? "apr-item:fixed:" + ConsumptionFixture.CorrectedValue(r)
                    : ScribeCandidatePayload.Format("alpha", RequestedFact(r)), ConsumptionFixture.TurnReplay(2)),
                (r, o, _) => ConsumptionFixture.Final(r, o, hostKind == "apr" ? "apr-item:final:1" : ScribeCandidatePayload.Format("beta", "final"),
                    ConsumptionFixture.TurnReplay(3)),
            };
            var startup = new ProductionStartup(steps, options: new RuntimeOptions(requireContinuation: true));
            if (hostKind == "apr")
            {
                var host = new AprBusinessHost(startup.Agent, startup.Agent);
                var channel = new ExchangeChannel([
                    new ExchangePolicy(false, CandidateContinuation.Continue, CorrectedValue: value),
                    new ExchangePolicy(true, CandidateContinuation.Continue),
                    new ExchangePolicy(true, CandidateContinuation.End)]);
                var result = await host.ExecuteCandidatesAsync(
                    ConsumptionFixture.Candidates(ConsumptionFixture.Request(3), submissions: 3, repairs: 1, continuations: 1), channel);
                Assert.Equal(CandidateStopReason.Completed, result.StopReason);
                return channel.Submissions[1].Payload;
            }

            var manifest = new ScribeManifest(["alpha", "beta"]);
            var control = new ScribeHostControl(ConsumptionFixture.Instructions,
                new AgentExecutionBounds(3, TimeSpan.FromMinutes(1)), new CandidateExecutionBounds(3, 1, 1));
            var scribe = new ScribeBusinessHost(manifest, new ScribeProgress(manifest, []), control, ExchangePlans.Scribe([
                new ExchangePolicy(false, CandidateContinuation.Continue, CorrectedFact: fact),
                new ExchangePolicy(true, CandidateContinuation.Continue),
                new ExchangePolicy(true, CandidateContinuation.End)]));
            var observed = await scribe.ExecuteFreshCandidatesAsync(startup.Agent);
            Assert.Equal(CandidateStopReason.Completed, observed.StopReason);
            return scribe.Progress.AcceptedFacts[0].Member + "|" + scribe.Progress.AcceptedFacts[0].Text;

            static string RequestedFact(ProviderRequest request)
            {
                Assert.True(ScribeCandidateCorrection.TryParseRequestedFact(ConsumptionFixture.CorrectedData(request), out var requested));
                return requested;
            }
        }
    }

    [Fact]
    public async Task ContinuationReplayAndClassifiedHistoryStayFullyAssociatedAcrossToolAndCandidateTurns()
    {
        var requests = new List<ProviderRequest>();
        var steps = new Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>>[]
        {
            (r, o, _) => { requests.Add(r);
                return ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("c1", 4)], ConsumptionFixture.TurnReplay(1)); },
            (r, o, _) => { requests.Add(r);
                return ConsumptionFixture.Final(r, o, "apr-item:initial:" + ConsumptionFixture.CounterTotal(r, "c1"), ConsumptionFixture.TurnReplay(2)); },
            (r, o, _) => { requests.Add(r);
                return ConsumptionFixture.Final(r, o, "apr-item:fixed:" + ConsumptionFixture.CorrectedValue(r), ConsumptionFixture.TurnReplay(3)); },
            (r, o, _) => { requests.Add(r);
                return ConsumptionFixture.Final(r, o, "apr-item:final:1", ConsumptionFixture.TurnReplay(4)); },
        };
        var startup = new ProductionStartup(steps, options: new RuntimeOptions(requireContinuation: true));
        var host = new AprBusinessHost(startup.Agent, startup.Agent);
        var channel = new ExchangeChannel([
            new ExchangePolicy(false, CandidateContinuation.Continue, CorrectedValue: 5),
            new ExchangePolicy(true, CandidateContinuation.Continue),
            new ExchangePolicy(true, CandidateContinuation.End)]);
        var result = await host.ExecuteCandidatesAsync(
            ConsumptionFixture.Candidates(ConsumptionFixture.Request(4), submissions: 3, repairs: 1, continuations: 1), channel);
        Assert.Equal(CandidateStopReason.Completed, result.StopReason);

        for (var turn = 1; turn < requests.Count; turn++)
        {
            Assert.Equal(ConsumptionFixture.TurnReplay(turn), ConsumptionFixture.Replay(requests[turn]));
            Assert.Equal(ConsumptionFixture.TurnReplay(turn), Encoding.UTF8.GetString(requests[turn].Continuation!.CopyReplayBytes()));
        }

        Assert.Equal(new[] { ProviderInputKind.ModelData, ProviderInputKind.ToolResultData },
            requests[1].Inputs.Skip(2).Select(i => i.Kind));
        Assert.Null(requests[1].Inputs[2].Model!.Text);
        Assert.Equal("initial:4", requests[2].Inputs[4].Model!.Text!["apr-item:".Length..]);
        Assert.Equal(new[] { "c1" }, requests[2].Inputs.Where(i => i.ToolResult is not null).Select(i => i.ToolResult!.Call.CallId));
        Assert.Equal(new[] { ProviderInputKind.ModelData, ProviderInputKind.ToolResultData, ProviderInputKind.ModelData,
            ProviderInputKind.InputData, ProviderInputKind.ModelData }, requests[3].Inputs.Skip(2).Select(i => i.Kind));
        Assert.Equal(ConsumptionFixture.Correction(5), requests[3].Inputs[^2].Text);
        Assert.Equal(4, result.Outcome.Usage!.Attempts.Count);
        Assert.Equal(7, requests[3].Inputs.Count);
    }
}

using System.Text.Json;
using AprHost;
using CustomTools;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ConsumerProbes.ScribeHost;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.ContractTests.Runtime.Tools;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Consumption;

public sealed class ConfinementAndIsolationTests
{
    [Fact]
    public async Task RestrictedAndCredentialCanariesStayOffEverySafeSurface()
    {
        var aprRequests = new List<ProviderRequest>();
        var scribeRequests = new List<ProviderRequest>();
        var aprStartup = new ProductionStartup(
        [
            (r, o, _) => { aprRequests.Add(r); return ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("c1", 7)], ConsumptionFixture.TurnReplay(1)); },
            (r, o, _) => { aprRequests.Add(r); return ConsumptionFixture.Final(r, o, "apr-item:initial:" + ConsumptionFixture.CounterTotal(r, "c1"), ConsumptionFixture.TurnReplay(2)); },
            (r, o, _) => { aprRequests.Add(r); return ConsumptionFixture.Final(r, o, "apr-item:fixed:" + ConsumptionFixture.CorrectedValue(r), ConsumptionFixture.TurnReplay(3)); },
            (r, o, _) => { aprRequests.Add(r); return ConsumptionFixture.Final(r, o, "apr-item:final:1", ConsumptionFixture.TurnReplay(4)); },
        ], options: new RuntimeOptions(requireContinuation: true));
        var aprHost = new AprBusinessHost(aprStartup.Agent, aprStartup.Agent);
        var aprChannel = new ExchangeChannel([
            new ExchangePolicy(false, CandidateContinuation.Continue, CorrectedValue: 6),
            new ExchangePolicy(true, CandidateContinuation.Continue),
            new ExchangePolicy(true, CandidateContinuation.End)]);
        var aprProgress = new List<AgentProgress>();
        var aprResult = await aprHost.ExecuteCandidatesAsync(
            ConsumptionFixture.Candidates(ConsumptionFixture.Request(4), submissions: 3, repairs: 1, continuations: 1),
            aprChannel, new InlineProgress(aprProgress.Add));

        var scribeStartup = new ProductionStartup(
        [
            (r, o, _) => { scribeRequests.Add(r); return ConsumptionFixture.ToolCalls(r, o,
                [ToolFixture.Transform("t1", "fact one " + ConsumptionFixture.ToolArgumentCanary)], ConsumptionFixture.TurnReplay(1)); },
            (r, o, _) => { scribeRequests.Add(r); return ConsumptionFixture.Final(r, o, ScribeCandidatePayload.Format("alpha",
                ConsumptionFixture.TransformText(r, "t1")), ConsumptionFixture.TurnReplay(2)); },
            (r, o, _) => { scribeRequests.Add(r); return ConsumptionFixture.Final(r, o, ScribeCandidatePayload.Format("beta",
                "beta " + ConsumptionFixture.ModelFactCanary), ConsumptionFixture.TurnReplay(3)); },
        ], options: new RuntimeOptions(requireContinuation: true));
        var manifest = new ScribeManifest(["alpha", "beta"]);
        var control = new ScribeHostControl(ConsumptionFixture.Instructions,
            new AgentExecutionBounds(3, TimeSpan.FromMinutes(1)), new CandidateExecutionBounds(3, 1, 1));
        var scribeHost = new ScribeBusinessHost(manifest, new ScribeProgress(manifest, []), control, ExchangePlans.Scribe([
            new ExchangePolicy(true, CandidateContinuation.Continue),
            new ExchangePolicy(true, CandidateContinuation.End)]));
        var scribeProgress = new List<AgentProgress>();
        var scribeResult = await scribeHost.ExecuteFreshCandidatesAsync(scribeStartup.Agent, new InlineProgress(scribeProgress.Add));

        Assert.Equal(CandidateStopReason.Completed, aprResult.StopReason);
        Assert.Equal(CandidateStopReason.Completed, scribeResult.StopReason);

        // Positive controls: the restricted channels really carry each nonempty canary.
        Assert.All(aprRequests, r => Assert.Equal(ConsumptionFixture.Instructions, r.Inputs[0].Text));
        Assert.Contains(aprRequests, r => r.Inputs.Any(i => i.Text == ConsumptionFixture.Correction(6)));
        Assert.Contains(scribeRequests, r => r.Inputs.Any(i => i.ToolResult?.Call.ArgumentsJson
            .Contains(ConsumptionFixture.ToolArgumentCanary, StringComparison.Ordinal) == true));
        Assert.Contains(aprRequests, r => ConsumptionFixture.Replay(r).Contains("CONTINUATION_CANARY", StringComparison.Ordinal));
        Assert.Contains(scribeHost.Progress.AcceptedFacts, f => f.Text.Contains(ConsumptionFixture.ModelFactCanary, StringComparison.Ordinal));
        Assert.Contains(scribeHost.Progress.AcceptedFacts, f => f.Text.Contains("TOOL_ARGUMENT_CANARY", StringComparison.OrdinalIgnoreCase));

        var safe = JsonSerializer.Serialize(aprResult) + JsonSerializer.Serialize(scribeResult)
            + JsonSerializer.Serialize(aprProgress) + JsonSerializer.Serialize(scribeProgress)
            + JsonSerializer.Serialize(aprResult.Outcome.Usage!.Attempts) + JsonSerializer.Serialize(scribeResult.Outcome.Usage!.Attempts)
            + JsonSerializer.Serialize(RuntimeAgentFactory.Describe(aprStartup.Configuration))
            + JsonSerializer.Serialize(RuntimeAgentFactory.Describe(scribeStartup.Configuration))
            + string.Join(string.Empty, aprResult.Receipts.Select(receipt => receipt.ToString()))
            + aprResult.Outcome.ToString() + aprHost.Acceptance + scribeHost + aprStartup.Agent + scribeStartup.Agent
            + aprStartup.Configuration + scribeStartup.Configuration + aprHost + scribeHost.Progress;
        foreach (var canary in new[] { "CORRECTION_CANARY", "CONTINUATION_CANARY", "SCRIPTED_PRIVATE_CREDENTIAL_CANARY",
            "MODEL_FACT_CANARY", "TOOL_ARGUMENT_CANARY", "INPUT_CANARY" })
        {
            Assert.DoesNotContain(canary, safe);
        }
    }

    [Fact]
    public async Task TrustedInstructionAndInstalledBindingsStayImmutableDespiteInstructionLikeData()
    {
        var requests = new List<ProviderRequest>();
        var startup = new ProductionStartup(
        [
            (r, o, _) => { requests.Add(r); return ConsumptionFixture.ToolCalls(r, o,
                [ToolFixture.Transform("t1", ConsumptionFixture.InstructionLikeData)], ConsumptionFixture.TurnReplay(1)); },
            (r, o, _) => { requests.Add(r); return ConsumptionFixture.Final(r, o, ScribeCandidatePayload.Format("extra_tool", "ignored"),
                ConsumptionFixture.TurnReplay(2)); },
            (r, o, _) => { requests.Add(r); return ConsumptionFixture.Final(r, o, ScribeCandidatePayload.Format("alpha", "real fact"),
                ConsumptionFixture.TurnReplay(3)); },
        ], options: new RuntimeOptions(requireContinuation: true));
        var manifest = new ScribeManifest(["alpha", "beta"]);
        var control = new ScribeHostControl(ConsumptionFixture.Instructions,
            new AgentExecutionBounds(3, TimeSpan.FromMinutes(1)), new CandidateExecutionBounds(3, 1, 1));
        var host = new ScribeBusinessHost(manifest, new ScribeProgress(manifest, []), control, ExchangePlans.Scribe([
            new ExchangePolicy(false, CandidateContinuation.Continue, CorrectedFact: "requested fact"),
            new ExchangePolicy(true, CandidateContinuation.End)]));
        var result = await host.ExecuteFreshCandidatesAsync(startup.Agent);

        Assert.Equal(CandidateStopReason.Completed, result.StopReason);
        // Instruction-like input and tool data never move into trusted control, bindings or validation.
        Assert.All(requests, r =>
        {
            Assert.Equal(ProviderInputKind.HostInstruction, r.Inputs[0].Kind);
            Assert.Equal(ConsumptionFixture.Instructions, r.Inputs[0].Text);
            Assert.Equal(new[] { "counter", "transform" }, r.Tools.Select(t => t.Name));
            Assert.Equal(new[] { "counter_increment", "text_transform" }, r.Tools.Select(t => t.CapabilityId));
            Assert.All(r.Tools, t => Assert.Equal("closed_scalar_object", t.InputSchema.Profile));
        });
        Assert.Equal(ScribePayloadValidation.UnselectedMember, host.Exchanges[0].Validation);
        Assert.Equal(ScribePayloadValidation.Valid, host.Exchanges[1].Validation);
        Assert.Equal("real fact", host.Progress.AcceptedFacts.Single().Text);
    }

    [Fact]
    public async Task EqualExecutionIdsAcrossConcurrentHostRunsKeepHistoriesAndReceiptsIsolated()
    {
        var step = (Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>>)((r, o, _) =>
        {
            var tag = r.Inputs[1].Text!;
            var models = r.Inputs.Count(i => i.Model is not null);
            return models switch
            {
                0 => ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("c1", 1)]),
                1 => ConsumptionFixture.Final(r, o, "apr-item:" + tag + ":" + ConsumptionFixture.CounterTotal(r, "c1")),
                2 => ConsumptionFixture.Final(r, o, "apr-item:fixed:" + ConsumptionFixture.CorrectedValue(r)),
                _ => ConsumptionFixture.Final(r, o, "apr-item:last:1"),
            };
        });
        var startup = new ProductionStartup(Enumerable.Repeat(step, 8).ToArray());
        var executionId = Guid.NewGuid();
        var aHost = new AprBusinessHost(startup.Agent, startup.Agent);
        var bHost = new AprBusinessHost(startup.Agent, startup.Agent);
        var aChannel = new ExchangeChannel([
            new ExchangePolicy(false, CandidateContinuation.Continue, CorrectedValue: 1),
            new ExchangePolicy(true, CandidateContinuation.Continue),
            new ExchangePolicy(true, CandidateContinuation.End)]);
        var bChannel = new ExchangeChannel([
            new ExchangePolicy(false, CandidateContinuation.Continue, CorrectedValue: 2),
            new ExchangePolicy(true, CandidateContinuation.Continue),
            new ExchangePolicy(true, CandidateContinuation.End)]);
        var aRun = aHost.ExecuteCandidatesAsync(ConsumptionFixture.Candidates(
            ConsumptionFixture.Request(4, executionId, data: "run-a"), submissions: 3, repairs: 1, continuations: 1), aChannel).AsTask();
        var bRun = bHost.ExecuteCandidatesAsync(ConsumptionFixture.Candidates(
            ConsumptionFixture.Request(4, executionId, data: "run-b"), submissions: 3, repairs: 1, continuations: 1), bChannel).AsTask();
        var aResult = await RuntimeFixture.Await(aRun);
        var bResult = await RuntimeFixture.Await(bRun);

        Assert.Equal(CandidateStopReason.Completed, aResult.StopReason);
        Assert.Equal(CandidateStopReason.Completed, bResult.StopReason);
        Assert.Equal("apr-item:fixed:1", aChannel.Submissions[1].Payload);
        Assert.Equal("apr-item:fixed:2", bChannel.Submissions[1].Payload);
        Assert.Equal(aChannel.Submissions[0].SubmissionId, aChannel.Submissions[1].RepairsSubmissionId);
        Assert.Equal(bChannel.Submissions[0].SubmissionId, bChannel.Submissions[1].RepairsSubmissionId);
        Assert.All(aResult.Receipts, receipt => Assert.Contains(aChannel.Submissions, s => s.SubmissionId == receipt.SubmissionId));
        Assert.All(bResult.Receipts, receipt => Assert.Contains(bChannel.Submissions, s => s.SubmissionId == receipt.SubmissionId));
        Assert.Empty(aChannel.Submissions.Select(s => s.SubmissionId).Intersect(bChannel.Submissions.Select(s => s.SubmissionId)));
        Assert.Equal(8, aResult.Outcome.Usage!.Attempts.Concat(bResult.Outcome.Usage!.Attempts)
            .Select(a => a.PhysicalAttemptId).Distinct().Count());
        Assert.Equal(4, aResult.Outcome.Usage.Attempts.Count); Assert.Equal(4, bResult.Outcome.Usage!.Attempts.Count);
        Assert.Equal(2, aResult.RepairsAdmitted + bResult.RepairsAdmitted);
        // Capability sharing across concurrent runs stays explicit Host authority on the installed bindings.
        Assert.Equal(2, startup.CounterCapability.Effects);
    }

    [Fact]
    public async Task ScribeSecondFreshRunUsesNewIdentitiesWithoutTranscriptOrRestoration()
    {
        var runOneRequest = (ProviderRequest?)null;
        var runTwoRequest = (ProviderRequest?)null;
        var first = new ProductionStartup(
        [
            (r, o, _) => { runOneRequest = r; return ConsumptionFixture.Final(r, o, ScribeCandidatePayload.Format("alpha", "FIRST FACT")); },
        ]);
        var manifest = new ScribeManifest(["alpha", "beta"]);
        var control = new ScribeHostControl(ConsumptionFixture.Instructions,
            new AgentExecutionBounds(2, TimeSpan.FromMinutes(1)), new CandidateExecutionBounds(2, 1, 1));
        var firstHost = new ScribeBusinessHost(manifest, new ScribeProgress(manifest, []), control, ExchangePlans.Scribe([
            new ExchangePolicy(true, CandidateContinuation.End)]));
        var firstResult = await firstHost.ExecuteFreshCandidatesAsync(first.Agent);

        var second = new ProductionStartup(
        [
            (r, o, _) => { runTwoRequest = r; return ConsumptionFixture.Final(r, o, ScribeCandidatePayload.Format("beta", "SECOND FACT")); },
        ]);
        var secondHost = new ScribeBusinessHost(manifest, firstHost.Progress, control, ExchangePlans.Scribe([
            new ExchangePolicy(true, CandidateContinuation.End)]));
        var secondResult = await secondHost.ExecuteFreshCandidatesAsync(second.Agent);

        Assert.Equal(CandidateStopReason.Completed, firstResult.StopReason);
        Assert.Equal(CandidateStopReason.Completed, secondResult.StopReason);
        Assert.NotEqual(firstHost.CurrentExecutionId, secondHost.CurrentExecutionId);
        Assert.Empty(firstHost.Exchanges.Select(e => e.SubmissionId).Intersect(secondHost.Exchanges.Select(e => e.SubmissionId)));
        Assert.All(firstResult.Receipts, r => Assert.Equal(firstHost.CurrentExecutionId, r.ExecutionId));
        Assert.All(secondResult.Receipts, r => Assert.Equal(secondHost.CurrentExecutionId, r.ExecutionId));
        Assert.Empty(firstResult.Outcome.Usage!.Attempts.Select(a => a.PhysicalAttemptId)
            .Intersect(secondResult.Outcome.Usage!.Attempts.Select(a => a.PhysicalAttemptId)));

        // The second fresh run reconstructs only validated facts and unresolved work; no transcript or saved state.
        Assert.Equal(new[] { "alpha", "beta" }, secondHost.Progress.AcceptedFacts.Select(f => f.Member));
        Assert.Equal(new[]
        {
            ConsumptionFixture.Instructions,
            "selected members: alpha, beta",
            "accepted fact: alpha = FIRST FACT",
            "unresolved member: beta",
        }, runTwoRequest!.Inputs.Select(i => i.Text));
        Assert.Equal(new[]
        {
            ConsumptionFixture.Instructions,
            "selected members: alpha, beta",
            "unresolved member: alpha",
            "unresolved member: beta",
        }, runOneRequest!.Inputs.Select(i => i.Text));
    }

    [Fact]
    public async Task AprContextRequiredRunRemainsUnsupportedWithoutAContextSeam()
    {
        var startup = new ProductionStartup(
        [
            (r, o, _) => ConsumptionFixture.Final(r, o, "apr-item:initial:1"),
        ]);
        var host = new AprBusinessHost(startup.Agent, startup.Agent);
        var execution = ConsumptionFixture.Request(4);
        var envelope = new AgentContextEnvelope(Guid.NewGuid(), 1, 1, "{\"origin\":\"00000000-0000-0000-0000-000000000001\"}"u8.ToArray());
        var contextRequest = new ContextExecutionRequest(execution, ContextExecutionIntent.ContinueRun, envelope);
        var channel = new ExchangeChannel([new ExchangePolicy(true, CandidateContinuation.End)]);

        var rejected = await host.ExecuteWithContextAsync(contextRequest);
        Assert.Equal(ContextAdmission.Rejected, rejected.Admission);
        Assert.Equal(ContextRejectionCode.UnsupportedContext, rejected.RejectionCode);

        var run = await host.RunAsync(contextRequest, null, ConsumptionFixture.Candidates(execution), channel);
        Assert.Equal(ContextAdmission.Rejected, run.Context!.Admission);
        Assert.Null(run.Candidates);
        Assert.Equal(0, startup.Provider.Effects);
        Assert.Empty(channel.Submissions);
        Assert.Equal(0, host.Acceptance.AcceptedCount);
    }
}

using System.Text;
using System.Text.Json.Nodes;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Capacity;

public sealed class CapacityCandidateTests
{
    [Theory]
    [InlineData(64, false, false)]
    [InlineData(64, true, false)]
    [InlineData(65, false, false)]
    [InlineData(65, false, true)]
    [InlineData(128, false, false)]
    public async Task ExpandedCandidateLedgerCapturesAndRestoresUnderCumulativeRecordBudget(int count, bool pending, bool repair)
    {
        var provider = new PersistentProvider { Respond = r => new(r.Scope, r.Attempt, ProviderFinish.Final, "complete", []) };
        var host = new ScriptedCandidateHost((s, _) => ValueTask.FromResult<CandidateFeedback?>(new(s.ExecutionId, s.SubmissionId,
            CandidateAcknowledgement.Acknowledged, repair ? CandidateDecision.Reject : CandidateDecision.Accept,
            pending || provider.Effects < count ? CandidateContinuation.Continue : CandidateContinuation.End,
            repair ? "bounded repair" : null)));
        var sink = new RestrictedContextHost();
        var source = await ((IContextCandidateAgent)CapacityFixture.Agent(provider)).ExecuteCandidatesWithContextAsync(
            new(CapacityFixture.Request(count), ContextExecutionIntent.Fresh), new(count, count, count), host, sink);
        Assert.Equal(pending ? CandidateStopReason.SubmissionLimit : repair ? CandidateStopReason.HostEnded : CandidateStopReason.Completed,
            source.Candidates!.StopReason);
        Assert.Equal(count, provider.Effects); Assert.Equal(count, host.Submissions.Count);
        Assert.Equal(count, source.Candidates.Receipts.Count); Assert.Equal(count, source.Candidates.Outcome.Usage!.Attempts.Count);
        Assert.Equal(repair ? count - 1 : 0, source.Candidates.RepairsAdmitted);
        Assert.Equal(ContextCaptureStatus.Delivered, source.Context.CaptureStatus);
        var envelope = sink.CopyRestrictedContext(); Assert.InRange(envelope.PayloadByteCount, 1, 2 * CapacityFixture.MiB);
        var node = JsonNode.Parse(envelope.CopyRestrictedPayload())!;
        var records = node["Records"]!.AsArray().Count;
        var episodes = node["Candidates"]!["Episodes"]!.AsArray().Count;
        var submissions = node["Candidates"]!["Submissions"]!.AsArray().Count;
        Assert.Equal(count + (pending ? 1 : 0), episodes); Assert.Equal(count, submissions);
        Assert.True(records + episodes + submissions < CapacityFixture.Options().MaximumRecords);

        // Smaller current cumulative capacity rejects the real captured ledger before claim/import/callback.
        var lowerAuthority = new SelectedAuthority(envelope, source.Context.Checkpoint!); var lowerProvider = new PersistentProvider();
        var lowerHost = AcceptingHost(); var lowerRequest = CapacityFixture.Request(1);
        var lowerAgent = (IContextCandidateAgent)RuntimeAgentFactory.Create(new(lowerProvider, [], new RuntimeHooks(), CapacityFixture.Bounds,
            contextAuthority: lowerAuthority), new(maximumAttempts: 128, maximumRecords: records + episodes + submissions - 1,
                maximumRetainedBytes: 256 * CapacityFixture.MiB));
        var rejected = await lowerAgent.ExecuteCandidatesWithContextAsync(new(lowerRequest, ContextExecutionIntent.ContinueRun, envelope,
            OrdinaryFixture.Grant(source.Context, lowerRequest.ExecutionId)), new(1, 0, 1), lowerHost);
        Assert.Equal(ContextAdmission.Rejected, rejected.Context.Admission); Assert.Equal(0, lowerAuthority.Claims);
        Assert.Equal(0, lowerProvider.Imports); Assert.Equal(0, lowerProvider.Effects); Assert.Empty(lowerHost.Submissions);

        var authority = new SelectedAuthority(envelope, source.Context.Checkpoint!);
        var nextProvider = new PersistentProvider { Respond = r => new(r.Scope, r.Attempt, ProviderFinish.Final, "next", []) };
        var nextHost = AcceptingHost(); var request = CapacityFixture.Request(1); var nextSink = new RestrictedContextHost();
        var agent = (IContextCandidateAgent)RuntimeAgentFactory.Create(new(nextProvider, [], new RuntimeHooks(), CapacityFixture.Bounds,
            contextAuthority: authority), CapacityFixture.Options());
        var resumed = await agent.ExecuteCandidatesWithContextAsync(new(request, ContextExecutionIntent.ContinueRun, envelope,
            OrdinaryFixture.Grant(source.Context, request.ExecutionId)), new(1, 0, 1), nextHost, nextSink);
        Assert.Equal(ContextAdmission.Supplied, resumed.Context.Admission); Assert.Equal(1, authority.Claims); Assert.Equal(1, nextProvider.Imports);
        Assert.Equal(pending ? 1 : 0, nextProvider.Effects); Assert.Equal(pending ? 1 : 0, nextHost.Submissions.Count);
        Assert.Equal(count, resumed.Candidates!.HistoricalReceipts.Count);
        Assert.Equal(source.Candidates.Receipts.Select(r => r.SubmissionId), resumed.Candidates.HistoricalReceipts.Select(r => r.SubmissionId));
        Assert.Equal(pending ? 1 : 0, resumed.Candidates.Receipts.Count);
        Assert.Equal(ContextCaptureStatus.Delivered, resumed.Context.CaptureStatus);
    }

    [Fact]
    public async Task IndependentApiOnlyProducerHonorsExplicitHostPayloadSelection()
    {
        var payload = CapacityFixture.Artifact(CapacityFixture.MiB);
        var producer = new ScriptedCandidateAgent((_, _) => ValueTask.FromResult(payload));
        var host = AcceptingHost();
        var result = await CandidateConsumer.RunAsync(producer, new(CapacityFixture.Request(1), new(1, 0, 0, CapacityFixture.MiB)), host);
        Assert.Equal(CandidateStopReason.Completed, result.Result.StopReason); Assert.Single(host.Submissions);
        CapacityFixture.Same(payload, host.Submissions[0].Payload); Assert.Equal(1, producer.TotalProductionStarted);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LargeFinalDeliveryUsesCurrentHostBoundInOrdinaryAndCombinedEntrypoints(bool combined, bool optIn)
    {
        var payload = CapacityFixture.Artifact(CapacityFixture.MiB);
        var provider = new PersistentProvider { Respond = r => new(r.Scope, r.Attempt, ProviderFinish.Final, payload, []) };
        var agent = (IContextCandidateAgent)CapacityFixture.Agent(provider);
        var request = CapacityFixture.Request(1); var sink = new RestrictedContextHost();
        ScriptedCandidateHost? host = null;
        host = new((s, _) => { CapacityFixture.Same(payload, s.Payload); host!.ApplyEffect(); return ValueTask.FromResult<CandidateFeedback?>(
            new(s.ExecutionId, s.SubmissionId, CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept, CandidateContinuation.End)); });
        var bounds = new CandidateExecutionBounds(1, 0, 0, optIn ? CapacityFixture.MiB : CandidateSubmission.DefaultPayloadBytes);
        ContextCandidateExecutionResult? context = null;
        var result = combined
            ? (context = await CandidateConsumer.RunWithContextAsync(agent, new(request, ContextExecutionIntent.Fresh), bounds, host, sink)).Candidates!
            : (await CandidateConsumer.RunAsync(agent, new(request, bounds), host)).Result;
        Assert.Equal(optIn ? CandidateStopReason.Completed : CandidateStopReason.RuntimeLimit, result.StopReason);
        Assert.Equal(1, result.Outcome.CompletedWorkUnits); Assert.Equal(1, provider.Effects);
        Assert.Equal(3, result.Outcome.Usage!.InputTokens.ObservedTokens); Assert.Equal(optIn ? 1 : 0, host.Submissions.Count);
        Assert.Equal(optIn ? 1 : 0, host.Effects); Assert.Equal(optIn ? 1 : 0, result.Receipts.Count);
        Assert.DoesNotContain(payload[..100], result.ToString());
        if (combined)
        {
            Assert.Equal(ContextCaptureStatus.Delivered, context!.Context.CaptureStatus);
            if (!optIn)
            {
                // A larger current Host bound can deliver the already accepted unsent Final; no new provider dispatch.
                var envelope = sink.CopyRestrictedContext(); var resumedProvider = new PersistentProvider();
                var current = CapacityFixture.Request(1); var authority = new SelectedAuthority(envelope, context.Context.Checkpoint!);
                var resumedAgent = (IContextCandidateAgent)RuntimeAgentFactory.Create(new(resumedProvider, [], new RuntimeHooks(),
                    CapacityFixture.Bounds, contextAuthority: authority), CapacityFixture.Options());
                var resumed = await resumedAgent.ExecuteCandidatesWithContextAsync(new(current, ContextExecutionIntent.ContinueRun, envelope,
                    OrdinaryFixture.Grant(context.Context, current.ExecutionId)), new(1, 0, 0, CapacityFixture.MiB), host);
                Assert.Equal(ContextAdmission.Supplied, resumed.Context.Admission); Assert.Equal(CandidateStopReason.Completed, resumed.Candidates!.StopReason);
                Assert.Equal(0, resumedProvider.Effects); Assert.Equal(1, resumedProvider.Imports); Assert.Equal(1, authority.Claims);
                Assert.Single(host.Submissions); Assert.Equal(request.ExecutionId, host.Submissions[0].ExecutionId);
                Assert.Equal(1, host.Effects); Assert.Empty(resumed.Candidates.Outcome.Usage!.Attempts);
            }
        }
    }

    [Fact]
    public async Task LargeRepairPreservesOriginalSubmissionAndCorrectionClassification()
    {
        var initial = CapacityFixture.Artifact(CapacityFixture.MiB, "a"); var repaired = CapacityFixture.Artifact(CapacityFixture.MiB, "b");
        var provider = new ScriptedProvider([
            (r, o, _) => { o.CaptureUsage(new(3, 2)); return ValueTask.FromResult(new ProviderResponse(r.Scope, r.Attempt, ProviderFinish.Final, initial, [])); },
            (r, o, _) => { Assert.Equal(ProviderInputKind.InputData, r.Inputs[^1].Kind); Assert.Equal("bounded repair", r.Inputs[^1].Text);
                Assert.Equal(ProviderInputKind.HostInstruction, r.Inputs[0].Kind); o.CaptureUsage(new(3, 2));
                return ValueTask.FromResult(new ProviderResponse(r.Scope, r.Attempt, ProviderFinish.Final, repaired, [])); }
        ]);
        var first = Guid.Empty; var calls = 0;
        var host = new ScriptedCandidateHost((s, _) =>
        {
            calls++;
            if (calls == 1) { first = s.SubmissionId; CapacityFixture.Same(initial, s.Payload); return ValueTask.FromResult<CandidateFeedback?>(
                new(s.ExecutionId, s.SubmissionId, CandidateAcknowledgement.Acknowledged, CandidateDecision.Reject, CandidateContinuation.Continue, "bounded repair")); }
            CapacityFixture.Same(repaired, s.Payload); Assert.Equal(first, s.RepairsSubmissionId);
            return ValueTask.FromResult<CandidateFeedback?>(new(s.ExecutionId, s.SubmissionId, CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept, CandidateContinuation.End));
        });
        var result = await ((ICandidateAgent)CapacityFixture.Agent(provider)).ExecuteCandidatesAsync(new(CapacityFixture.Request(), new(2, 1, 0, CapacityFixture.MiB)), host);
        Assert.Equal(CandidateStopReason.Completed, result.StopReason); Assert.Equal(2, host.Submissions.Count);
        Assert.Equal(2, provider.Effects); Assert.Equal(1, result.RepairsAdmitted); Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(CandidateDecision.Reject, result.Receipts[0].Decision); Assert.Equal(CandidateDecision.Accept, result.Receipts[1].Decision);
    }

    [Fact]
    public async Task TrustedInvokedCandidateBeyondGlobalCeilingRejectsBeforeClaimImportOrHost()
    {
        var provider = new PersistentProvider { Respond = r => new(r.Scope, r.Attempt, ProviderFinish.Final, "complete", []) }; var sink = new RestrictedContextHost();
        var agent = (IContextCandidateAgent)CapacityFixture.Agent(provider); var sourceRequest = CapacityFixture.Request(1);
        var source = await agent.ExecuteCandidatesWithContextAsync(new(sourceRequest, ContextExecutionIntent.Fresh), new(1, 0, 0, CapacityFixture.MiB),
            AcceptingHost(), sink);
        Assert.Equal(ContextCaptureStatus.Delivered, source.Context.CaptureStatus);
        Assert.Equal(CandidateStopReason.Completed, source.Candidates!.StopReason);
        var original = sink.CopyRestrictedContext(); var node = JsonNode.Parse(original.CopyRestrictedPayload())!;
        node["Records"]![1]!["Final"]!["Text"] = CapacityFixture.Artifact(CapacityFixture.MiB + 1);
        var changed = new AgentContextEnvelope(original.ImplementationId, original.FormatVersion, original.CompatibilityVersion, Encoding.UTF8.GetBytes(node.ToJsonString()));
        Assert.True(changed.PayloadByteCount < 2 * CapacityFixture.MiB);
        var current = CapacityFixture.Request(1); var authority = new SelectedAuthority(changed, source.Context.Checkpoint!);
        var nextProvider = new PersistentProvider(); var host = AcceptingHost();
        var next = (IContextCandidateAgent)RuntimeAgentFactory.Create(new(nextProvider, [], new RuntimeHooks(), CapacityFixture.Bounds,
            contextAuthority: authority), CapacityFixture.Options());
        var result = await next.ExecuteCandidatesWithContextAsync(new(current, ContextExecutionIntent.ContinueRun, changed,
            OrdinaryFixture.Grant(source.Context, current.ExecutionId)), new(1, 0, 0, CapacityFixture.MiB), host);
        Assert.Equal(ContextAdmission.Rejected, result.Context.Admission); Assert.Equal(0, authority.Claims);
        Assert.Equal(0, nextProvider.Imports); Assert.Equal(0, nextProvider.Effects); Assert.Empty(host.Submissions);
    }

    private static ScriptedCandidateHost AcceptingHost() => new((s, _) => ValueTask.FromResult<CandidateFeedback?>(
        new(s.ExecutionId, s.SubmissionId, CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept, CandidateContinuation.End)));
}

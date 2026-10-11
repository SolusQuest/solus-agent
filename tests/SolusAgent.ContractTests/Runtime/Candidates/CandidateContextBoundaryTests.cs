using System.Text.Json;
using System.Text.Json.Nodes;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Candidates;

public sealed class CandidateContextBoundaryTests
{
    [Fact]
    public async Task HeldHostLateCompletionCannotRewriteFrozenUnknownOrEnableReplay()
    {
        var entered = RuntimeFixture.Barrier<CandidateSubmission>(); var release = RuntimeFixture.Barrier<CandidateFeedback?>();
        var host = new ScriptedCandidateHost((s, _) => { entered.TrySetResult(s); return new(release.Task); });
        var sink = new RestrictedContextHost(); using var cancel = new CancellationTokenSource();
        var pending = CandidateConsumer.RunWithContextAsync(CandidateContextFixture.Agent(new()),
            new(CandidateContextFixture.Request(), ContextExecutionIntent.Fresh), new(3, 2, 2), host, sink, cancellationToken: cancel.Token).AsTask();
        var submission = await RuntimeFixture.Await(entered.Task); cancel.Cancel();
        var result = await RuntimeFixture.Await(pending); var envelope = sink.CopyRestrictedContext();
        var bytes = envelope.CopyRestrictedPayload(); var safe = JsonSerializer.Serialize(result);
        Assert.Equal(CandidateStopReason.Cancelled, result.Candidates!.StopReason);
        Assert.Equal(CandidateAcknowledgement.Unknown, Assert.Single(result.Candidates.Receipts).Acknowledgement);
        release.TrySetResult(new(submission.ExecutionId, submission.SubmissionId, CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept, CandidateContinuation.End));
        await release.Task;
        Assert.Equal(safe, JsonSerializer.Serialize(result)); Assert.Equal(bytes, envelope.CopyRestrictedPayload());
        var provider = new PersistentProvider(); var nextHost = CandidateContextFixture.Host();
        var resumed = await CandidateConsumer.RunWithContextAsync(CandidateContextFixture.Agent(provider,
            new SelectedAuthority(envelope, result.Context.Checkpoint!)),
            CandidateContextFixture.Resume(result, envelope, CandidateContextFixture.Request()), new(3, 2, 2), nextHost);
        Assert.Equal(ContextAdmission.Supplied, resumed.Context.Admission); Assert.Equal(CandidateStopReason.UnknownAcknowledgement, resumed.Candidates!.StopReason);
        Assert.Equal(0, provider.Effects); Assert.Empty(nextHost.Submissions); Assert.Equal(0, resumed.Candidates.AcceptedCount);
        Assert.DoesNotContain("RESTRICTED_FINAL", safe); Assert.DoesNotContain("RESTRICTED_REPLAY", safe); Assert.DoesNotContain("CREDENTIAL", safe);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentPermissionAndProviderImportFailureNeverDispatch(bool rejectImport)
    {
        var source = await CandidateContextFixture.Capture(host: CandidateContextFixture.Host(CandidateDecision.Reject, CandidateContinuation.Continue, "CORRECTION_CANARY"),
            bounds: new(2, 0, 0));
        var provider = new PersistentProvider { RejectImport = rejectImport }; var host = CandidateContextFixture.Host();
        var authority = new SelectedAuthority(source.Envelope, source.Result.Context.Checkpoint!);
        var hooks = new RuntimeHooks { Before = (e, _) => ValueTask.FromResult<ExposureAcknowledgement?>(
            new(e, RuntimeHookStatus.Acknowledged, ExposureDecision.Deny, e.RequiredAcknowledgement)) };
        var request = CandidateContextFixture.Resume(source.Result, source.Envelope, CandidateContextFixture.Request());
        var result = await CandidateConsumer.RunWithContextAsync(CandidateContextFixture.Agent(provider, authority, hooks: hooks), request, new(2, 1, 0), host);
        Assert.Equal(1, authority.Claims); Assert.Equal(1, provider.Imports); Assert.Equal(0, provider.Effects); Assert.Empty(host.Submissions);
        Assert.Equal(rejectImport ? ContextAdmission.Rejected : ContextAdmission.Supplied, result.Context.Admission);
        if (!rejectImport) { Assert.Equal(CandidateStopReason.ProductionStopped, result.Candidates!.StopReason); Assert.Equal(1, result.Candidates.RepairsAdmitted); }
        var duplicate = await CandidateConsumer.RunWithContextAsync(CandidateContextFixture.Agent(provider, authority), request, new(2, 1, 0), host);
        Assert.Equal(ContextAdmission.Rejected, duplicate.Context.Admission); Assert.Equal(1, provider.Imports);
    }
    [Theory]
    [InlineData(0, CandidateStopReason.RepairLimit)]
    [InlineData(1, CandidateStopReason.Completed)]
    public async Task HistoricalRejectionUsesOnlyCurrentNewAdmissionAllowance(int repairs, CandidateStopReason stop)
    {
        var source = await CandidateContextFixture.Capture(host: CandidateContextFixture.Host(CandidateDecision.Reject, CandidateContinuation.Continue, "CORRECTION_CANARY"), bounds: new(2, 0, 0));
        var provider = new PersistentProvider(); var host = CandidateContextFixture.Host();
        var result = await CandidateConsumer.RunWithContextAsync(CandidateContextFixture.Agent(provider,
            new SelectedAuthority(source.Envelope, source.Result.Context.Checkpoint!)),
            CandidateContextFixture.Resume(source.Result, source.Envelope, CandidateContextFixture.Request()), new(2, repairs, 0), host);
        Assert.Equal(ContextAdmission.Supplied, result.Context.Admission); Assert.Equal(stop, result.Candidates!.StopReason);
        Assert.Equal(repairs, result.Candidates.RepairsAdmitted); Assert.Equal(repairs, provider.Effects); Assert.Equal(repairs, host.Submissions.Count);
        if (repairs != 0) { Assert.Equal(source.Host.Submissions[0].SubmissionId, host.Submissions[0].RepairsSubmissionId); Assert.Single(provider.LastRequest!.Inputs, i => i.Text == "CORRECTION_CANARY"); }
    }
    [Theory]
    [InlineData("marker")]
    [InlineData("correction-position")]
    [InlineData("correction-text")]
    [InlineData("predecessor")]
    [InlineData("admission-round")]
    [InlineData("duplicate-final")]
    public async Task TrustedCorrectionAndEpisodeCorruptionRejectBeforeClaim(string field)
    {
        var source = await CandidateContextFixture.Capture(host: CandidateContextFixture.Host(CandidateDecision.Reject, CandidateContinuation.Continue, "CORRECTION_CANARY"),
            options: new(maximumRecords: 6, requireContinuation: true));
        var node = JsonNode.Parse(source.Envelope.CopyRestrictedPayload())!;
        var episode = node["Candidates"]!["Episodes"]![1]!;
        switch (field)
        {
            case "marker": episode["CorrectionIndex"] = null; break;
            case "correction-position": episode["CorrectionIndex"] = 0; break;
            case "correction-text": node["Records"]![2]!["Text"] = "OTHER_CANARY"; break;
            case "predecessor": episode["Predecessor"] = Guid.NewGuid(); break;
            case "admission-round": episode["AdmissionRound"] = Guid.NewGuid(); break;
            case "duplicate-final": node["Candidates"]!["Submissions"]!.AsArray().Add(node["Candidates"]!["Submissions"]![0]!.DeepClone()); break;
        }
        var envelope = new AgentContextEnvelope(source.Envelope.ImplementationId, 1, 1, JsonSerializer.SerializeToUtf8Bytes(node));
        var provider = new PersistentProvider(); var host = CandidateContextFixture.Host(); var authority = new SelectedAuthority(envelope, source.Result.Context.Checkpoint!);
        var result = await CandidateConsumer.RunWithContextAsync(CandidateContextFixture.Agent(provider, authority),
            CandidateContextFixture.Resume(source.Result, envelope, CandidateContextFixture.Request()), new(2, 1, 0), host);
        Assert.Equal(ContextAdmission.Rejected, result.Context.Admission); Assert.Equal(0, authority.Claims); Assert.Equal(0, provider.Imports); Assert.Equal(0, provider.Effects); Assert.Empty(host.Submissions);
    }
    [Fact]
    public async Task ThrowingSinkAndSlowExportCannotChangeCandidateOutcome()
    {
        var clock = new ControlledTimeProvider(); var provider = new PersistentProvider { BeforeExport = () => clock.Advance(TimeSpan.FromSeconds(60)) };
        var result = await CandidateConsumer.RunWithContextAsync(CandidateContextFixture.Agent(provider, options: new(timeProvider: clock, requireContinuation: true)),
            new(CandidateContextFixture.Request(), ContextExecutionIntent.Fresh), new(1, 0, 0), CandidateContextFixture.Host(), new ThrowingSink());
        Assert.Equal(CandidateStopReason.Completed, result.Candidates!.StopReason); Assert.Equal(ContextCaptureStatus.Failed, result.Context.CaptureStatus);
        Assert.Equal(1, result.Candidates.AcceptedCount); Assert.NotNull(result.Context.Checkpoint);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TooSmallCandidateRecordCapacityStopsBeforeProvider(int maximumRecords)
    {
        var provider = new PersistentProvider(); var host = CandidateContextFixture.Host();
        var result = await CandidateConsumer.RunWithContextAsync(CandidateContextFixture.Agent(provider, options: new(maximumRecords: maximumRecords)),
            new(CandidateContextFixture.Request(), ContextExecutionIntent.Fresh), new(1, 0, 0), host);
        Assert.Equal(CandidateStopReason.RuntimeLimit, result.Candidates!.StopReason); Assert.Equal(0, provider.Effects); Assert.Empty(host.Submissions);
    }
    private sealed class ThrowingSink : IRestrictedContextSink { public void Capture(AgentContextEnvelope context) => throw new InvalidOperationException("SINK_CANARY"); }
}

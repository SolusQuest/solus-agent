using System.Text.Json;
using System.Text.Json.Nodes;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Candidates;

internal static class CandidateContextFixture
{
    internal static AgentRequest Request(int work = 4, AgentUsageLimits? limits = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), "CURRENT_HOST_INSTRUCTIONS", [], new(work, TimeSpan.FromSeconds(30)), AgentCapability.None, limits);
    internal static IContextCandidateAgent Agent(PersistentProvider provider, IRuntimeContextAuthority? authority = null,
        RuntimeOptions? options = null, RuntimeHooks? hooks = null, RuntimeToolRegistration[]? tools = null) =>
        (IContextCandidateAgent)RuntimeAgentFactory.Create(new(provider, tools ?? [], hooks ?? new(), contextAuthority: authority), options ?? new(requireContinuation: true));
    internal static ScriptedCandidateHost Host(CandidateDecision decision = CandidateDecision.Accept, CandidateContinuation continuation = CandidateContinuation.End,
        string? correction = null) => new((s, _) => ValueTask.FromResult<CandidateFeedback?>(new(s.ExecutionId, s.SubmissionId,
            CandidateAcknowledgement.Acknowledged, decision, continuation, correction)));
    internal static ContextExecutionRequest Resume(ContextCandidateExecutionResult result, AgentContextEnvelope envelope, AgentRequest request,
        ContextExecutionIntent intent = ContextExecutionIntent.ContinueRun) => new(request, intent, envelope,
            new(Guid.NewGuid(), result.Context.Checkpoint!, request.ExecutionId, Guid.NewGuid()));
    internal static async Task<(ContextCandidateExecutionResult Result, AgentContextEnvelope Envelope, ScriptedCandidateHost Host)> Capture(
        PersistentProvider? provider = null, ScriptedCandidateHost? host = null, AgentRequest? request = null, CandidateExecutionBounds? bounds = null,
        IProgress<AgentProgress>? progress = null, RuntimeOptions? options = null)
    {
        var sink = new RestrictedContextHost(); host ??= Host();
        var result = await CandidateConsumer.RunWithContextAsync(Agent(provider ?? new(), options: options),
            new(request ?? Request(), ContextExecutionIntent.Fresh), bounds ?? new(3, 2, 2), host, sink, progress);
        Assert.Equal(ContextCaptureStatus.Delivered, result.Context.CaptureStatus);
        return (result, sink.CopyRestrictedContext(), host);
    }
}

public sealed class CandidateContextTests
{
    [Theory]
    [InlineData(CandidateDecision.Accept)]
    [InlineData(CandidateDecision.Reject)]
    public async Task EndContinuesAsZeroWorkAndNewRunRetainsOriginalReceipts(CandidateDecision decision)
    {
        var source = await CandidateContextFixture.Capture(host: CandidateContextFixture.Host(decision));
        var provider = new PersistentProvider(); var host = CandidateContextFixture.Host(); var sink = new RestrictedContextHost();
        var resumed = await CandidateConsumer.RunWithContextAsync(CandidateContextFixture.Agent(provider,
            new SelectedAuthority(source.Envelope, source.Result.Context.Checkpoint!)),
            CandidateContextFixture.Resume(source.Result, source.Envelope, CandidateContextFixture.Request()), new(1, 0, 0), host, sink);
        Assert.Equal(ContextAdmission.Supplied, resumed.Context.Admission);
        Assert.Equal(decision == CandidateDecision.Accept ? CandidateStopReason.Completed : CandidateStopReason.HostEnded, resumed.Candidates!.StopReason);
        Assert.Equal(0, provider.Effects); Assert.Empty(host.Submissions); Assert.Equal(0, resumed.Candidates.Outcome.CompletedWorkUnits);
        Assert.Empty(resumed.Candidates.Receipts); Assert.Single(resumed.Candidates.HistoricalReceipts);
        Assert.Equal(source.Result.Candidates!.Receipts[0].ExecutionId, resumed.Candidates.HistoricalReceipts[0].ExecutionId);
        Assert.Equal(ContextCaptureStatus.Delivered, resumed.Context.CaptureStatus);
        var envelope = sink.CopyRestrictedContext(); var nextProvider = new PersistentProvider(); var nextHost = CandidateContextFixture.Host();
        var next = await CandidateConsumer.RunWithContextAsync(CandidateContextFixture.Agent(nextProvider,
            new SelectedAuthority(envelope, resumed.Context.Checkpoint!)),
            CandidateContextFixture.Resume(resumed, envelope, CandidateContextFixture.Request(), ContextExecutionIntent.NewRunFromContext), new(1, 0, 0), nextHost);
        Assert.Equal(ContextAdmission.Supplied, next.Context.Admission); Assert.Equal(1, nextProvider.Effects);
        Assert.Single(next.Candidates!.Receipts); Assert.Single(next.Candidates.HistoricalReceipts);
        Assert.Equal(0, next.Candidates.RepairsAdmitted); Assert.Equal(0, next.Candidates.ContinuationsAdmitted);
    }

    [Theory]
    [InlineData(CandidateAcknowledgement.Unknown)]
    [InlineData(CandidateAcknowledgement.Failed)]
    public async Task UncertainOriginalSubmissionNeverReplays(CandidateAcknowledgement acknowledgement)
    {
        var host = new ScriptedCandidateHost((s, _) => ValueTask.FromResult<CandidateFeedback?>(new(s.ExecutionId, s.SubmissionId, acknowledgement)));
        var source = await CandidateContextFixture.Capture(host: host); var provider = new PersistentProvider(); var nextHost = CandidateContextFixture.Host();
        var result = await CandidateConsumer.RunWithContextAsync(CandidateContextFixture.Agent(provider,
            new SelectedAuthority(source.Envelope, source.Result.Context.Checkpoint!)),
            CandidateContextFixture.Resume(source.Result, source.Envelope, CandidateContextFixture.Request()), new(3, 2, 2), nextHost);
        Assert.Equal(ContextAdmission.Supplied, result.Context.Admission); Assert.Equal(0, provider.Effects); Assert.Empty(nextHost.Submissions);
        Assert.Equal(source.Host.Submissions[0].SubmissionId, Assert.Single(result.Candidates!.HistoricalReceipts).SubmissionId);
        Assert.Empty(result.Candidates.Outcome.Usage!.Attempts);
    }

    [Fact]
    public async Task AcceptedReceiptSurvivesLaterFailureAndRetryKeepsExactHistory()
    {
        var firstProvider = new PersistentProvider(); string? expected = null; ProviderRequest? original = null;
        firstProvider.Respond = r => firstProvider.Effects == 1 ? ToolContextFixture.Final(r)
            : Fail(r);
        ProviderResponse Fail(ProviderRequest r) { expected = ToolContextFixture.History(r); original = r; throw new ProviderFailureException(new(ProviderRetryKind.Transient)); }
        var source = await CandidateContextFixture.Capture(firstProvider, CandidateContextFixture.Host(continuation: CandidateContinuation.Continue),
            CandidateContextFixture.Request(limits: new(maximumPhysicalDispatches: 2, retryPolicy: new(2))));
        Assert.Equal(1, source.Result.Candidates!.AcceptedCount); Assert.Equal(1, source.Result.Candidates.ContinuationsAdmitted);
        var nextProvider = new PersistentProvider(); var host = CandidateContextFixture.Host();
        var resumed = await CandidateConsumer.RunWithContextAsync(CandidateContextFixture.Agent(nextProvider,
            new SelectedAuthority(source.Envelope, source.Result.Context.Checkpoint!)),
            CandidateContextFixture.Resume(source.Result, source.Envelope, CandidateContextFixture.Request(limits: new(maximumPhysicalDispatches: 1, retryPolicy: new(2)))),
            new(1, 0, 0), host);
        Assert.Equal(ContextAdmission.Supplied, resumed.Context.Admission); Assert.Equal(CandidateStopReason.Completed, resumed.Candidates!.StopReason);
        Assert.Equal(2, resumed.Candidates.AcceptedCount); Assert.Equal(0, resumed.Candidates.ContinuationsAdmitted);
        Assert.Equal(expected, ToolContextFixture.History(nextProvider.LastRequest!));
        Assert.Equal(original!.Attempt.LogicalCallId, nextProvider.LastRequest!.Attempt.LogicalCallId);
        Assert.Equal(original.Attempt.PhysicalAttemptId, nextProvider.LastRequest.History!.OperationOrigin!.PhysicalAttemptId);
        Assert.Equal(2, nextProvider.LastRequest.Attempt.AttemptNumber); Assert.Equal(6, resumed.Context.History.Sum(h => h.Usage.InputTokens.ObservedTokens));
        Assert.Equal(3, resumed.Candidates.Outcome.Usage!.InputTokens.ObservedTokens);
    }

    [Fact]
    public async Task FinalSavedBeforeObserverFailureIsSubmittedOnceWithoutNewProvider()
    {
        var source = await CandidateContextFixture.Capture(progress: new CallbackProgress(() => throw new InvalidOperationException("OBSERVER_CANARY")));
        Assert.Empty(source.Host.Submissions); Assert.Equal(CandidateStopReason.ProgressObserverFailed, source.Result.Candidates!.StopReason);
        var provider = new PersistentProvider(); var host = CandidateContextFixture.Host(); var sink = new RestrictedContextHost();
        var result = await CandidateConsumer.RunWithContextAsync(CandidateContextFixture.Agent(provider,
            new SelectedAuthority(source.Envelope, source.Result.Context.Checkpoint!)),
            CandidateContextFixture.Resume(source.Result, source.Envelope, CandidateContextFixture.Request()), new(1, 0, 0), host, sink);
        Assert.Equal(ContextAdmission.Supplied, result.Context.Admission); Assert.Equal(0, provider.Effects); Assert.Single(host.Submissions);
        Assert.Equal(source.Result.Candidates.Outcome.ExecutionId, host.Submissions[0].ExecutionId);
        Assert.Single(result.Candidates!.HistoricalReceipts); Assert.Empty(result.Candidates.Receipts);
        Assert.Equal(ContextCaptureStatus.Delivered, result.Context.CaptureStatus);
    }

    [Fact]
    public async Task OrdinaryAndCandidateModesRejectBeforeClaimOrEffects()
    {
        var source = await CandidateContextFixture.Capture(); var provider = new PersistentProvider();
        var authority = new SelectedAuthority(source.Envelope, source.Result.Context.Checkpoint!);
        var ordinary = await OrdinaryFixture.Agent(provider, authority).ExecuteWithContextAsync(
            new(OrdinaryFixture.Request(), ContextExecutionIntent.ContinueRun, source.Envelope,
                new(Guid.NewGuid(), source.Result.Context.Checkpoint!, Guid.NewGuid(), Guid.NewGuid())));
        Assert.Equal(ContextRejectionCode.InvalidRunTransition, ordinary.RejectionCode); Assert.Equal(0, authority.Claims);
        var other = await OrdinaryFixture.Capture(); provider = new(); authority = new(other.Envelope, other.Result.Checkpoint!);
        var host = CandidateContextFixture.Host(); var request = CandidateContextFixture.Request();
        var candidate = await CandidateConsumer.RunWithContextAsync(CandidateContextFixture.Agent(provider, authority),
            new(request, ContextExecutionIntent.ContinueRun, other.Envelope, new(Guid.NewGuid(), other.Result.Checkpoint!, request.ExecutionId, Guid.NewGuid())), new(1, 0, 0), host);
        Assert.Equal(ContextRejectionCode.InvalidRunTransition, candidate.Context.RejectionCode); Assert.Equal(0, authority.Claims); Assert.Equal(0, provider.Effects); Assert.Empty(host.Submissions);
    }

    [Theory]
    [InlineData("submission-id")]
    [InlineData("origin")]
    [InlineData("cursor")]
    [InlineData("first-attempt")]
    [InlineData("round-counter")]
    [InlineData("episode-work")]
    public async Task TrustedSemanticCorruptionRejectsBeforeClaim(string field)
    {
        var source = await CandidateContextFixture.Capture(); var document = JsonNode.Parse(source.Envelope.CopyRestrictedPayload())!;
        switch (field)
        {
            case "submission-id": document["Candidates"]!["Submissions"]![0]!["SubmissionId"] = Guid.NewGuid(); break;
            case "origin": document["Candidates"]!["Submissions"]![0]!["ExecutionId"] = Guid.NewGuid(); break;
            case "cursor": document["Candidates"]!["Cursor"] = 0; break;
            case "first-attempt": document["Candidates"]!["Episodes"]![0]!["FirstAttempt"]!["PhysicalAttemptId"] = Guid.NewGuid(); break;
            case "round-counter": document["Rounds"]![0]!["Candidates"]!["Submissions"] = 0; break;
            case "episode-work": document["Candidates"]!["Episodes"]![0]!["WorkId"] = Guid.NewGuid(); break;
        }
        var envelope = new AgentContextEnvelope(source.Envelope.ImplementationId, 1, 1, JsonSerializer.SerializeToUtf8Bytes(document));
        var authority = new SelectedAuthority(envelope, source.Result.Context.Checkpoint!); var provider = new PersistentProvider(); var host = CandidateContextFixture.Host();
        var result = await CandidateConsumer.RunWithContextAsync(CandidateContextFixture.Agent(provider, authority),
            CandidateContextFixture.Resume(source.Result, envelope, CandidateContextFixture.Request()), new(1, 0, 0), host);
        Assert.Equal(ContextAdmission.Rejected, result.Context.Admission); Assert.Equal(0, authority.Claims); Assert.Equal(0, provider.Imports); Assert.Equal(0, provider.Effects); Assert.Empty(host.Submissions);
    }
    internal sealed class CallbackProgress(Action callback) : IProgress<AgentProgress> { public void Report(AgentProgress value) => callback(); }
}

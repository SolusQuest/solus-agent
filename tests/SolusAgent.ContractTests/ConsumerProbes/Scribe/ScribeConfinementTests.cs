using System.Text.Json;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ConsumerProbes.ScribeHost;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.ConsumerProbes.Scribe;

/// <summary>AC4: literal outcome categories, Host-calculated coverage and restricted-diagnostics confinement.</summary>
public sealed class ScribeConfinementTests
{
    private const string ProgressCanary = "PROGRESS_FACT_CANARY business fact only";

    [Fact]
    public async Task PrematureCompletedOutcomeDoesNotEstablishManifestCompletionOrEffects()
    {
        var host = ScribeFixtures.CreateHost([new ScribeExchangePlan(true, ScribeDelivery.Delivered, CandidateContinuation.Continue)]);
        var round = new ScribeRound(host, [new ScribeProductionStep("intro", "INTRO_FACT_CANARY early completion")]);
        var observed = await round.RunAsync();

        Assert.Equal(CandidateStopReason.Completed, observed.Result.StopReason);
        Assert.Equal(AgentTerminationReason.Completed, observed.Result.Outcome.Reason);

        // Host coverage is calculated separately from the premature scripted terminal outcome.
        Assert.False(host.Progress.IsComplete);
        Assert.Equal(1, host.Progress.AcceptedCount);
        Assert.Equal(3, host.Progress.SelectedCount);
        Assert.Equal(new[] { "usage", "limits" }, host.Progress.UnresolvedMembers);

        // Provider and tool work plus a candidate result imply no patches, storage or publication.
        Assert.Equal(0, host.ExternalEffects);
    }

    [Fact]
    public async Task SubmissionAndWorkUnitAdmissionExhaustionAreLiteralResourceLimits()
    {
        var submissionHost = ScribeFixtures.CreateHost(
        [
            new ScribeExchangePlan(true, ScribeDelivery.Delivered, CandidateContinuation.Continue),
            new ScribeExchangePlan(true, ScribeDelivery.Delivered, CandidateContinuation.Continue),
        ], maximumSubmissions: 2);
        var submissionRound = new ScribeRound(submissionHost,
        [
            new ScribeProductionStep("intro", "INTRO_FACT_CANARY first"),
            new ScribeProductionStep("usage", "USAGE_FACT_CANARY second"),
            new ScribeProductionStep("limits", "LIMITS_FACT_CANARY blocked"),
        ]);
        var submissionObserved = await submissionRound.RunAsync();
        Assert.Equal(CandidateStopReason.SubmissionLimit, submissionObserved.Result.StopReason);
        Assert.Equal(AgentTerminationReason.ResourceLimit, submissionObserved.Result.Outcome.Reason);
        Assert.Equal(2, submissionRound.Agent.TotalProductionStarted);

        var workHost = ScribeFixtures.CreateHost(
        [
            new ScribeExchangePlan(true, ScribeDelivery.Delivered, CandidateContinuation.Continue),
            new ScribeExchangePlan(true, ScribeDelivery.Delivered, CandidateContinuation.Continue),
        ], maximumWorkUnits: 2);
        var workRound = new ScribeRound(workHost,
        [
            new ScribeProductionStep("intro", "INTRO_FACT_CANARY first"),
            new ScribeProductionStep("usage", "USAGE_FACT_CANARY second"),
            new ScribeProductionStep("limits", "LIMITS_FACT_CANARY blocked"),
        ]);
        var workObserved = await workRound.RunAsync();
        Assert.Equal(CandidateStopReason.WorkUnitLimit, workObserved.Result.StopReason);
        Assert.Equal(AgentTerminationReason.ResourceLimit, workObserved.Result.Outcome.Reason);
        Assert.Equal(2, workRound.Agent.TotalProductionStarted);

        // Resource limits stay distinct from the literal Partial, Cancelled and Failed categories.
        Assert.NotEqual(CandidateStopReason.HostEnded, submissionObserved.Result.StopReason);
        Assert.NotEqual(CandidateStopReason.WorkUnitLimit, submissionObserved.Result.StopReason);
        Assert.NotEqual(submissionObserved.Result.StopReason, workObserved.Result.StopReason);
    }

    [Fact]
    public async Task OrdinaryDiagnosticsExcludePayloadCorrectionProgressContinuationAndCredentialCanares()
    {
        var host = ScribeFixtures.CreateHost(
        [
            new ScribeExchangePlan(true, ScribeDelivery.Delivered, CandidateContinuation.Continue),
            new ScribeExchangePlan(false, ScribeDelivery.Delivered, CandidateContinuation.End),
        ], [("intro", ProgressCanary)]);
        var round = new ScribeRound(host,
        [
            new ScribeProductionStep("usage", "USAGE_FACT_CANARY produced payload canary"),
            new ScribeProductionStep("limits", "LIMITS_FACT_CANARY rejected payload canary"),
        ]);
        var observed = await round.RunAsync();
        Assert.Equal(CandidateStopReason.HostEnded, observed.Result.StopReason);

        // The restricted startup channel deliberately inspects the exact synthetic provider continuation.
        var attempt = new ProviderAttempt(round.ContextRequest.Request.ExecutionId, Guid.NewGuid(), Guid.NewGuid());
        var continuationAttempt = await round.Runtime.RunContinuationAttemptAsync(attempt, ScribeRuntimeStartup.ContinuationCanary);
        Assert.Equal(RuntimeStop.None, continuationAttempt.AdmissionStop);
        var continuation = continuationAttempt.Provider!.Response!.Continuation;
        Assert.NotNull(continuation);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(ScribeRuntimeStartup.ContinuationCanary), continuation!.CopyReplayBytes());
        Assert.True(continuation.Matches(new ProviderContinuation(round.Runtime.Provider.Scope, attempt,
            System.Text.Encoding.UTF8.GetBytes(ScribeRuntimeStartup.ContinuationCanary))));

        var surfaces = new List<string>
        {
            JsonSerializer.Serialize(observed.Result),
            JsonSerializer.Serialize(observed.Result.Outcome),
            JsonSerializer.Serialize(round.Runtime.Configuration.Describe()),
            round.Runtime.Configuration.Describe().ToString()!,
            host.ToString()!,
            host.Progress.ToString()!,
            host.Manifest.ToString()!,
            new CandidateSubmission(round.ContextRequest.Request.ExecutionId, Guid.NewGuid(), "payload").ToString()!,
            new CandidateFeedback(round.ContextRequest.Request.ExecutionId, Guid.NewGuid(), CandidateAcknowledgement.Unknown).ToString()!,
        };
        surfaces.AddRange(observed.Result.Receipts.Select(receipt => JsonSerializer.Serialize(receipt)));
        surfaces.AddRange(observed.Progress.Select(progress => JsonSerializer.Serialize(progress)));
        surfaces.AddRange(round.Runtime.Diagnostics.Select(diagnostic => JsonSerializer.Serialize(diagnostic)));
        surfaces.AddRange(host.Progress.AcceptedFacts.Select(fact => fact.ToString()!));

        var restrictedCanaries = new[]
        {
            ScribeBusinessHost.CorrectionCanary,
            ConfigurationProvider.ModelCanary,
            ScribeRuntimeStartup.ContinuationCanary,
            ConfigurationProvider.CredentialCanary,
            ConfigurationHooks.CredentialCanary,
            ConfigurationCapability.CredentialCanary,
            ProgressCanary,
            "USAGE_FACT_CANARY produced payload canary",
            "LIMITS_FACT_CANARY rejected payload canary",
        };
        foreach (var surface in surfaces)
        {
            Assert.NotEmpty(surface);
            foreach (var canary in restrictedCanaries)
            {
                Assert.DoesNotContain(canary, surface, StringComparison.Ordinal);
            }
        }
    }
}

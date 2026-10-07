using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.ConsumerProbes.Apr;

/// <summary>Honest usage observations and ordered Runtime.Api exposure/settlement through the composed APR scenario.</summary>
public sealed class AprUsageExposureTests
{
    [Fact]
    public async Task CompleteKnownProviderUsageTravelsThroughProgressAndOutcome()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetProductions = 3 });
        var execution = AprFixtures.Request();
        var progress = new List<AgentProgress>();

        var result = await AprFixtures.WithTimeout(startup.Host.ExecuteCandidatesAsync(
            AprFixtures.Candidates(execution), AlwaysAccept(), new AprFixtures.InlineProgress(progress.Add)));

        var usage = result.Outcome.Usage;
        Assert.NotNull(usage);
        Assert.Equal(UsageInventoryCoverage.Complete, usage.Coverage);
        Assert.Equal(6, usage.Attempts.Count);
        Assert.All(usage.Attempts, attempt => Assert.Equal(execution.ExecutionId, attempt.ExecutionId));
        Assert.All(usage.Attempts, attempt => Assert.Equal(DispatchExposure.Dispatched, attempt.Exposure));
        Assert.All(usage.Attempts, attempt => Assert.Equal(UsageCompleteness.Complete, attempt.Usage.Completeness));
        Assert.All(usage.Attempts, attempt => Assert.Equal(3, attempt.Usage.InputTokens));
        Assert.All(usage.Attempts, attempt => Assert.Equal(2, attempt.Usage.OutputTokens));
        Assert.NotEmpty(progress);
        Assert.All(progress, observation => Assert.NotNull(observation.Usage));
    }

    [Fact]
    public async Task PartialProviderUsageKeepsUnknownMeasurementsUnknown()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetProductions = 3, Usage = AprUsageMode.Partial });
        var execution = AprFixtures.Request();

        var result = await AprFixtures.WithTimeout(
            startup.Host.ExecuteCandidatesAsync(AprFixtures.Candidates(execution), AlwaysAccept()));

        var usage = result.Outcome.Usage;
        Assert.NotNull(usage);
        Assert.All(usage.Attempts, attempt => Assert.Equal(UsageCompleteness.Partial, attempt.Usage.Completeness));
        Assert.All(usage.Attempts, attempt => Assert.Equal(11, attempt.Usage.InputTokens));
        // The unknown output measurement stays unknown instead of becoming zero.
        Assert.All(usage.Attempts, attempt => Assert.Null(attempt.Usage.OutputTokens));
    }

    [Fact]
    public async Task UnavailableProviderUsageIsRetainedWithoutFabricatedZero()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetProductions = 3, Usage = AprUsageMode.Unavailable });
        var execution = AprFixtures.Request();

        var result = await AprFixtures.WithTimeout(
            startup.Host.ExecuteCandidatesAsync(AprFixtures.Candidates(execution), AlwaysAccept()));

        var usage = result.Outcome.Usage;
        Assert.NotNull(usage);
        Assert.All(usage.Attempts, attempt => Assert.Equal(UsageCompleteness.Unavailable, attempt.Usage.Completeness));
        Assert.All(usage.Attempts, attempt => Assert.Null(attempt.Usage.InputTokens));
        Assert.All(usage.Attempts, attempt => Assert.Null(attempt.Usage.OutputTokens));
        // Dispatch knowledge and accounting stay independent of measurement availability.
        Assert.All(usage.Attempts, attempt => Assert.Equal(DispatchExposure.Dispatched, attempt.Exposure));
        Assert.All(usage.Attempts, attempt => Assert.Null(attempt.Accounting));
    }

    [Fact]
    public async Task RetainedMeasurementSurvivesLaterFeedbackFailure()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetProductions = 3 });
        var execution = AprFixtures.Request();
        var feedback = new ScriptedAprFeedback((submission, index, _) =>
        {
            if (index == 0)
            {
                return ValueTask.FromResult<CandidateFeedback?>(AprDecisions.Accept(submission.ExecutionId, submission.SubmissionId));
            }

            throw new InvalidOperationException("synthetic-feedback-failure");
        });

        var result = await AprFixtures.WithTimeout(
            startup.Host.ExecuteCandidatesAsync(AprFixtures.Candidates(execution), feedback));

        Assert.Equal(CandidateStopReason.FailedAcknowledgement, result.StopReason);
        var usage = result.Outcome.Usage;
        Assert.NotNull(usage);
        Assert.Equal(4, usage.Attempts.Count);
        Assert.All(usage.Attempts, attempt => Assert.Equal(3, attempt.Usage.InputTokens));
    }

    [Fact]
    public async Task MatchingPermissionAllowsDispatchAndSameAttemptClosure()
    {
        var startup = AprStartup.Create();
        var request = AprFixtures.Request();

        var attempt = await AprFixtures.WithTimeout(startup.Scenario.RunConfiguredAttemptAsync(request,
            new ProviderAttempt(request.ExecutionId, Guid.NewGuid(), Guid.NewGuid())));

        Assert.Equal(RuntimeStop.None, attempt.AdmissionStop);
        Assert.Equal(RuntimeStop.None, attempt.SettlementStop);
        Assert.NotNull(attempt.Provider);
        Assert.Equal(ProviderOutcome.Succeeded, attempt.Provider!.Outcome);
        Assert.Equal(new[] { "expose", "ack", "settle" }, startup.Hooks.Events);
        Assert.Equal(3, attempt.Observation.Usage.InputTokens);
    }

    [Fact]
    public async Task HeldExposurePermissionLeavesActualProviderEffectsZero()
    {
        var startup = AprStartup.Create();
        var request = AprFixtures.Request();
        using var cancellation = new CancellationTokenSource();
        var held = new TaskCompletionSource<ExposureAcknowledgement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        startup.Hooks.Exposure = (_, _) => new ValueTask<ExposureAcknowledgement?>(held.Task);

        var pending = startup.Scenario.RunConfiguredAttemptAsync(request,
            new ProviderAttempt(request.ExecutionId, Guid.NewGuid(), Guid.NewGuid()), cancellation.Token).AsTask();
        for (var tick = 0; tick < 500 && !startup.Hooks.Events.Contains("expose"); tick++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(0, startup.ProviderEffects);
        Assert.False(startup.Tool.Started.Task.IsCompleted);

        cancellation.Cancel();
        var attempt = await pending;
        Assert.Equal(RuntimeStop.Cancelled, attempt.AdmissionStop);
        Assert.Null(attempt.Provider);
        Assert.Equal(0, startup.ProviderEffects);
    }

    [Fact]
    public async Task DeniedExposurePreventsProviderDispatch()
    {
        var startup = AprStartup.Create();
        var request = AprFixtures.Request();
        startup.Hooks.Exposure = (exposure, _) => ValueTask.FromResult<ExposureAcknowledgement?>(
            new(exposure, RuntimeHookStatus.Acknowledged, ExposureDecision.Deny));

        var attempt = await AprFixtures.WithTimeout(startup.Scenario.RunConfiguredAttemptAsync(request,
            new ProviderAttempt(request.ExecutionId, Guid.NewGuid(), Guid.NewGuid())));

        Assert.Equal(RuntimeStop.ExposureDenied, attempt.AdmissionStop);
        Assert.Null(attempt.Provider);
        Assert.Equal(0, startup.ProviderEffects);
    }

    [Fact]
    public async Task MissingExposureReceiptPreventsProviderDispatch()
    {
        var startup = AprStartup.Create();
        var request = AprFixtures.Request();
        startup.Hooks.Exposure = (_, _) => ValueTask.FromResult<ExposureAcknowledgement?>(null);

        var attempt = await AprFixtures.WithTimeout(startup.Scenario.RunConfiguredAttemptAsync(request,
            new ProviderAttempt(request.ExecutionId, Guid.NewGuid(), Guid.NewGuid())));

        Assert.Equal(RuntimeStop.ExposureMissing, attempt.AdmissionStop);
        Assert.Null(attempt.Provider);
        Assert.Equal(0, startup.ProviderEffects);
    }

    [Fact]
    public async Task UnknownExposureReceiptPreventsProviderDispatch()
    {
        var startup = AprStartup.Create();
        var request = AprFixtures.Request();
        startup.Hooks.Exposure = (exposure, _) => ValueTask.FromResult<ExposureAcknowledgement?>(
            new(exposure, RuntimeHookStatus.Unknown));

        var attempt = await AprFixtures.WithTimeout(startup.Scenario.RunConfiguredAttemptAsync(request,
            new ProviderAttempt(request.ExecutionId, Guid.NewGuid(), Guid.NewGuid())));

        Assert.Equal(RuntimeStop.ExposureUnknown, attempt.AdmissionStop);
        Assert.Null(attempt.Provider);
        Assert.Equal(0, startup.ProviderEffects);
    }

    [Fact]
    public async Task MismatchedExposureReceiptPreventsProviderDispatch()
    {
        var startup = AprStartup.Create();
        var request = AprFixtures.Request();
        startup.Hooks.Exposure = (exposure, _) => ValueTask.FromResult<ExposureAcknowledgement?>(new ExposureAcknowledgement(
            new RuntimeExposure(exposure.Scope,
                new ProviderAttempt(exposure.Attempt.ExecutionId, Guid.NewGuid(), Guid.NewGuid()),
                exposure.RequiredAcknowledgement),
            RuntimeHookStatus.Acknowledged, ExposureDecision.Permit, ExposureStrength.Volatile));

        var attempt = await AprFixtures.WithTimeout(startup.Scenario.RunConfiguredAttemptAsync(request,
            new ProviderAttempt(request.ExecutionId, Guid.NewGuid(), Guid.NewGuid())));

        Assert.Equal(RuntimeStop.ExposureMismatch, attempt.AdmissionStop);
        Assert.Null(attempt.Provider);
        Assert.Equal(0, startup.ProviderEffects);
    }

    [Fact]
    public async Task FailedClosureRetainsAttemptUsageAndBlocksLaterWork()
    {
        var startup = AprStartup.Create();
        var request = AprFixtures.Request();
        startup.Hooks.Settlement = (settlement, _) => ValueTask.FromResult<SettlementAcknowledgement?>(
            new(settlement.Exposure, RuntimeHookStatus.Failed));

        var first = await AprFixtures.WithTimeout(startup.Scenario.RunConfiguredAttemptAsync(request,
            new ProviderAttempt(request.ExecutionId, Guid.NewGuid(), Guid.NewGuid())));

        Assert.Equal(RuntimeStop.None, first.AdmissionStop);
        Assert.Equal(RuntimeStop.SettlementFailed, first.SettlementStop);
        // The same-attempt closure retains the original attempt and its actual measurement.
        Assert.Equal(DispatchExposure.Dispatched, first.Observation.Exposure);
        Assert.Equal(3, first.Observation.Usage.InputTokens);
        Assert.Equal(2, first.Observation.Usage.OutputTokens);

        var second = await AprFixtures.WithTimeout(startup.Scenario.RunConfiguredAttemptAsync(request,
            new ProviderAttempt(request.ExecutionId, Guid.NewGuid(), Guid.NewGuid())));

        Assert.Equal(RuntimeStop.SettlementFailed, second.AdmissionStop);
        Assert.Null(second.Provider);
        Assert.Equal(1, startup.ProviderEffects);
    }

    [Fact]
    public async Task UnknownClosureRetainsAttemptUsageAndBlocksLaterWork()
    {
        var startup = AprStartup.Create();
        var request = AprFixtures.Request();
        startup.Hooks.Settlement = (settlement, _) => ValueTask.FromResult<SettlementAcknowledgement?>(
            new(settlement.Exposure, RuntimeHookStatus.Unknown));

        var first = await AprFixtures.WithTimeout(startup.Scenario.RunConfiguredAttemptAsync(request,
            new ProviderAttempt(request.ExecutionId, Guid.NewGuid(), Guid.NewGuid())));

        Assert.Equal(RuntimeStop.SettlementUnknown, first.SettlementStop);
        Assert.Equal(3, first.Observation.Usage.InputTokens);

        var second = await AprFixtures.WithTimeout(startup.Scenario.RunConfiguredAttemptAsync(request,
            new ProviderAttempt(request.ExecutionId, Guid.NewGuid(), Guid.NewGuid())));

        Assert.Equal(RuntimeStop.SettlementUnknown, second.AdmissionStop);
        Assert.Null(second.Provider);
    }

    private static ScriptedAprFeedback AlwaysAccept() => new((submission, _, _) =>
        ValueTask.FromResult<CandidateFeedback?>(AprDecisions.Accept(submission.ExecutionId, submission.SubmissionId)));
}

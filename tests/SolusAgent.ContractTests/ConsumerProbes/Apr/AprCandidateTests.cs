using CustomTools;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using Xunit;

namespace SolusAgent.ContractTests.ConsumerProbes.Apr;

/// <summary>Candidate correction, acceptance, bounded stops and uncertain feedback over the composed APR scenario.</summary>
public sealed class AprCandidateTests
{
    [Fact]
    public async Task RejectedCandidateIsCorrectedThroughActualToolOutputAndRepairsSubmissionId()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetProductions = 3 });
        var execution = AprFixtures.Request();
        var host = startup.Host;
        var feedback = new ScriptedAprFeedback((submission, index, _) => ValueTask.FromResult<CandidateFeedback?>(
            index == 0
                ? AprDecisions.RejectWithCorrection(submission.ExecutionId, submission.SubmissionId, 7)
                : AprDecisions.Accept(submission.ExecutionId, submission.SubmissionId)));

        var result = await AprFixtures.WithTimeout(
            host.ExecuteCandidatesAsync(AprFixtures.Candidates(execution), feedback));

        Assert.Equal(CandidateStopReason.Completed, result.StopReason);
        Assert.Equal(AgentTerminationReason.Completed, result.Outcome.Reason);
        Assert.Equal(3, result.Receipts.Count);
        Assert.Equal(CandidateDecision.Reject, result.Receipts[0].Decision);
        Assert.True(result.Receipts[1].IsAccepted);
        Assert.True(result.Receipts[2].IsAccepted);
        Assert.Equal(1, result.RepairsAdmitted);
        Assert.Equal(1, result.ContinuationsAdmitted);
        Assert.Equal(2, result.AcceptedCount);

        // Repair association uses the actual submission member, never a stand-in local field.
        Assert.Null(feedback.Submissions[0].RepairsSubmissionId);
        Assert.Equal(feedback.Submissions[0].SubmissionId, feedback.Submissions[1].RepairsSubmissionId);
        Assert.Null(feedback.Submissions[2].RepairsSubmissionId);

        // Provider/tool output causally drives each candidate value, including the bounded correction.
        Assert.Equal(2, AprFixtures.ItemAt(feedback, 0)!.Value);
        Assert.Equal(7, AprFixtures.ItemAt(feedback, 1)!.Value);
        Assert.Equal(9, AprFixtures.ItemAt(feedback, 2)!.Value);
        Assert.Equal(3, startup.ToolEffects);
        Assert.Equal(6, startup.ProviderEffects);

        // Host acceptance is recorded independently of the agent's safe receipts.
        Assert.Equal(2, host.Acceptance.AcceptedCount);
        Assert.Equal(7, host.Acceptance.Accepted[0].Item.Value);
        Assert.Equal(feedback.Submissions[0].SubmissionId, host.Acceptance.Accepted[0].RepairsSubmissionId);
    }

    [Fact]
    public async Task IntermediateAcceptanceWithContinuationAwaitsTheExplicitGoalWithAllReceiptsRetained()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetProductions = 3 });
        var execution = AprFixtures.Request();
        var host = startup.Host;
        var acceptedCounts = new List<int>();
        var feedback = new ScriptedAprFeedback((submission, index, _) =>
        {
            acceptedCounts.Add(host.Acceptance.AcceptedCount);
            return ValueTask.FromResult<CandidateFeedback?>(index == 0
                ? AprDecisions.RejectWithCorrection(submission.ExecutionId, submission.SubmissionId, 7)
                : AprDecisions.Accept(submission.ExecutionId, submission.SubmissionId));
        });

        var result = await AprFixtures.WithTimeout(
            host.ExecuteCandidatesAsync(AprFixtures.Candidates(execution), feedback));

        // The third decision observes one accepted candidate while the run is still incomplete.
        Assert.Equal(new[] { 0, 0, 1 }, acceptedCounts);
        Assert.Equal(2, result.AcceptedCount);
        Assert.Equal(feedback.Submissions.Select(submission => submission.SubmissionId),
            result.Receipts.Select(receipt => receipt.SubmissionId));
    }

    [Fact]
    public async Task HostEndAfterAcceptedProgressReturnsPartialAndRetainsHostAcceptance()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetProductions = 3 });
        var execution = AprFixtures.Request();
        var feedback = new ScriptedAprFeedback((submission, index, _) => ValueTask.FromResult<CandidateFeedback?>(
            index == 0
                ? AprDecisions.Accept(submission.ExecutionId, submission.SubmissionId)
                : AprDecisions.Accept(submission.ExecutionId, submission.SubmissionId, CandidateContinuation.End)));

        var result = await AprFixtures.WithTimeout(
            startup.Host.ExecuteCandidatesAsync(AprFixtures.Candidates(execution), feedback));

        Assert.Equal(CandidateStopReason.HostEnded, result.StopReason);
        Assert.Equal(AgentTerminationReason.Partial, result.Outcome.Reason);
        Assert.Equal(2, result.Receipts.Count);
        Assert.All(result.Receipts, receipt => Assert.True(receipt.IsAccepted));
        Assert.Equal(1, result.ContinuationsAdmitted);
        Assert.Equal(2, startup.Host.Acceptance.AcceptedCount);
        Assert.Equal(2, startup.ToolEffects);
    }

    [Fact]
    public async Task SubmissionBoundStopsResourceLimitBeforeTheNextEffectAndRetainsAcceptance()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetProductions = 5 });
        var execution = AprFixtures.Request();
        var feedback = AlwaysAccept();

        var result = await AprFixtures.WithTimeout(
            startup.Host.ExecuteCandidatesAsync(AprFixtures.Candidates(execution, maximumSubmissions: 2), feedback));

        Assert.Equal(CandidateStopReason.SubmissionLimit, result.StopReason);
        Assert.Equal(AgentTerminationReason.ResourceLimit, result.Outcome.Reason);
        Assert.Equal(2, feedback.Submissions.Count);
        Assert.Equal(2, startup.ToolEffects);
        Assert.Equal(4, startup.ProviderEffects);
        Assert.Equal(2, startup.Host.Acceptance.AcceptedCount);
    }

    [Fact]
    public async Task WorkUnitBoundStopsResourceLimitBeforeTheNextEffect()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetProductions = 3 });
        var execution = AprFixtures.Request(maximumWorkUnits: 1);
        var feedback = AlwaysAccept();

        var result = await AprFixtures.WithTimeout(
            startup.Host.ExecuteCandidatesAsync(AprFixtures.Candidates(execution), feedback));

        Assert.Equal(CandidateStopReason.WorkUnitLimit, result.StopReason);
        Assert.Equal(AgentTerminationReason.ResourceLimit, result.Outcome.Reason);
        Assert.Single(feedback.Submissions);
        Assert.Equal(1, startup.ToolEffects);
    }

    [Fact]
    public async Task RepairLimitDeniesTheFollowOnProductionAndRetainsExistingFacts()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetProductions = 3 });
        var execution = AprFixtures.Request();
        var feedback = new ScriptedAprFeedback((submission, _, _) => ValueTask.FromResult<CandidateFeedback?>(
            AprDecisions.RejectWithCorrection(submission.ExecutionId, submission.SubmissionId, 1_000_000)));

        var result = await AprFixtures.WithTimeout(
            startup.Host.ExecuteCandidatesAsync(AprFixtures.Candidates(execution, maximumSubmissions: 8, maximumRepairs: 1), feedback));

        Assert.Equal(CandidateStopReason.RepairLimit, result.StopReason);
        Assert.Equal(AgentTerminationReason.ResourceLimit, result.Outcome.Reason);
        Assert.Equal(1, result.RepairsAdmitted);
        Assert.Equal(2, result.Receipts.Count);
        Assert.All(result.Receipts, receipt => Assert.Equal(CandidateDecision.Reject, receipt.Decision));
        Assert.Equal(0, startup.Host.Acceptance.AcceptedCount);
        Assert.Equal(2, startup.ToolEffects);
    }

    [Fact]
    public async Task ContinuationLimitDeniesTheFollowOnProductionAndRetainsAcceptedProgress()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetProductions = 4 });
        var execution = AprFixtures.Request();
        var feedback = AlwaysAccept();

        var result = await AprFixtures.WithTimeout(
            startup.Host.ExecuteCandidatesAsync(AprFixtures.Candidates(execution, maximumContinuations: 1), feedback));

        Assert.Equal(CandidateStopReason.ContinuationLimit, result.StopReason);
        Assert.Equal(AgentTerminationReason.ResourceLimit, result.Outcome.Reason);
        Assert.Equal(1, result.ContinuationsAdmitted);
        Assert.Equal(2, result.AcceptedCount);
        Assert.Equal(2, startup.Host.Acceptance.AcceptedCount);
    }

    [Fact]
    public async Task MissingAcknowledgementAfterAcceptedProgressStopsWithoutFurtherWorkOrReplay()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetProductions = 3 });
        var execution = AprFixtures.Request();
        var feedback = new ScriptedAprFeedback((submission, index, _) => ValueTask.FromResult<CandidateFeedback?>(
            index == 0 ? AprDecisions.Accept(submission.ExecutionId, submission.SubmissionId) : null));

        var result = await AprFixtures.WithTimeout(
            startup.Host.ExecuteCandidatesAsync(AprFixtures.Candidates(execution), feedback));

        Assert.Equal(CandidateStopReason.MissingAcknowledgement, result.StopReason);
        Assert.Equal(AgentTerminationReason.Partial, result.Outcome.Reason);
        Assert.Equal(CandidateAcknowledgement.Missing, result.Receipts[1].Acknowledgement);
        Assert.Equal(2, feedback.Submissions.Count);
        Assert.Equal(2, startup.ToolEffects);
        Assert.Equal(1, startup.Host.Acceptance.AcceptedCount);
    }

    [Fact]
    public async Task UnknownAcknowledgementAfterAcceptedProgressStopsWithoutAManufacturedDecision()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetProductions = 3 });
        var execution = AprFixtures.Request();
        var feedback = new ScriptedAprFeedback((submission, index, _) => ValueTask.FromResult<CandidateFeedback?>(
            index == 0 ? AprDecisions.Accept(submission.ExecutionId, submission.SubmissionId)
                       : AprDecisions.Unknown(submission.ExecutionId, submission.SubmissionId)));

        var result = await AprFixtures.WithTimeout(
            startup.Host.ExecuteCandidatesAsync(AprFixtures.Candidates(execution), feedback));

        Assert.Equal(CandidateStopReason.UnknownAcknowledgement, result.StopReason);
        Assert.Equal(AgentTerminationReason.Partial, result.Outcome.Reason);
        Assert.Equal(CandidateAcknowledgement.Unknown, result.Receipts[1].Acknowledgement);
        Assert.Null(result.Receipts[1].Decision);
        Assert.Null(result.Receipts[1].Continuation);
        Assert.Equal(2, feedback.Submissions.Count);
        Assert.Equal(1, startup.Host.Acceptance.AcceptedCount);
    }

    [Fact]
    public async Task FailedFeedbackExchangeRetainsExistingFacts()
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
        Assert.Equal(AgentTerminationReason.Failed, result.Outcome.Reason);
        Assert.Equal(AgentFailureCode.ExecutionFailed, result.Outcome.FailureCode);
        Assert.Equal(CandidateAcknowledgement.Failed, result.Receipts[1].Acknowledgement);
        Assert.Equal(1, startup.Host.Acceptance.AcceptedCount);
    }

    [Fact]
    public async Task MismatchedFeedbackDoesNotAuthorizeContinuation()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetProductions = 3 });
        var execution = AprFixtures.Request();
        var feedback = new ScriptedAprFeedback((submission, index, _) => ValueTask.FromResult<CandidateFeedback?>(
            index == 0 ? AprDecisions.Accept(submission.ExecutionId, submission.SubmissionId)
                       : AprDecisions.WrongExecution(Guid.NewGuid(), submission.SubmissionId)));

        var result = await AprFixtures.WithTimeout(
            startup.Host.ExecuteCandidatesAsync(AprFixtures.Candidates(execution), feedback));

        Assert.Equal(CandidateStopReason.MismatchedFeedback, result.StopReason);
        Assert.Equal(AgentTerminationReason.Failed, result.Outcome.Reason);
        // The receipt keeps the submitted identity instead of the wrong feedback identity.
        Assert.Equal(feedback.Submissions[1].SubmissionId, result.Receipts[1].SubmissionId);
        Assert.Equal(CandidateAcknowledgement.Mismatched, result.Receipts[1].Acknowledgement);
        Assert.Equal(2, feedback.Submissions.Count);
    }

    [Fact]
    public async Task DuplicateFeedbackDoesNotApplyThePriorDecisionToThePendingCandidate()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetProductions = 3 });
        var execution = AprFixtures.Request();
        var firstSubmission = Guid.Empty;
        var feedback = new ScriptedAprFeedback((submission, index, _) =>
        {
            if (index == 0)
            {
                firstSubmission = submission.SubmissionId;
                return ValueTask.FromResult<CandidateFeedback?>(AprDecisions.Accept(submission.ExecutionId, submission.SubmissionId));
            }

            return ValueTask.FromResult<CandidateFeedback?>(
                AprDecisions.WrongSubmission(submission.ExecutionId, firstSubmission));
        });

        var result = await AprFixtures.WithTimeout(
            startup.Host.ExecuteCandidatesAsync(AprFixtures.Candidates(execution), feedback));

        Assert.Equal(CandidateStopReason.DuplicateFeedback, result.StopReason);
        Assert.Equal(CandidateAcknowledgement.Duplicate, result.Receipts[1].Acknowledgement);
        Assert.Equal(feedback.Submissions[1].SubmissionId, result.Receipts[1].SubmissionId);
        Assert.Equal(1, startup.Host.Acceptance.AcceptedCount);
    }

    [Fact]
    public async Task CancellationAfterAcceptedProgressRetainsReceiptsAndHostAcceptance()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetProductions = 3 });
        var execution = AprFixtures.Request();
        using var cancellation = new CancellationTokenSource();
        var feedback = new ScriptedAprFeedback((submission, _, _) =>
        {
            // A delivered acknowledgement survives cancellation signalled before the await resumes.
            cancellation.Cancel();
            return ValueTask.FromResult<CandidateFeedback?>(AprDecisions.Accept(submission.ExecutionId, submission.SubmissionId));
        });

        var result = await AprFixtures.WithTimeout(
            startup.Host.ExecuteCandidatesAsync(AprFixtures.Candidates(execution), feedback, cancellationToken: cancellation.Token));

        Assert.Equal(CandidateStopReason.Cancelled, result.StopReason);
        Assert.Equal(AgentTerminationReason.Cancelled, result.Outcome.Reason);
        Assert.True(Assert.Single(result.Receipts).IsAccepted);
        Assert.Equal(1, startup.Host.Acceptance.AcceptedCount);
        Assert.Single(feedback.Submissions);
    }

    [Fact]
    public async Task AcceptedAndCompletedAloneLeaveEffectsZeroUntilTheExplicitHostOperation()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetProductions = 3 });
        var execution = AprFixtures.Request();
        var feedback = AlwaysAccept();

        var result = await AprFixtures.WithTimeout(
            startup.Host.ExecuteCandidatesAsync(AprFixtures.Candidates(execution), feedback));

        Assert.Equal(CandidateStopReason.Completed, result.StopReason);
        Assert.Equal(3, startup.Host.Acceptance.AcceptedCount);
        Assert.Equal(0, startup.Host.Acceptance.EffectCount);
        Assert.Equal(3, startup.Host.Acceptance.ApplyEffects());
        Assert.Equal(3, startup.Host.Acceptance.EffectCount);
        Assert.Equal(0, startup.Host.Acceptance.ApplyEffects());
    }

    [Fact]
    public async Task ALaterInvalidBatchArgumentAdmitsNoCandidateAndNoToolEffect()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetProductions = 3, SecondCallAmount = 500 });
        var execution = AprFixtures.Request();
        var feedback = AlwaysAccept();

        var result = await AprFixtures.WithTimeout(
            startup.Host.ExecuteCandidatesAsync(AprFixtures.Candidates(execution), feedback));

        Assert.Equal(CandidateStopReason.ProductionFailed, result.StopReason);
        Assert.Equal(AgentTerminationReason.Failed, result.Outcome.Reason);
        Assert.Empty(feedback.Submissions);
        Assert.Empty(result.Receipts);
        // Whole-batch admission precedes invocation: even the valid member stays uninvoked.
        Assert.Equal(0, startup.ToolEffects);
        Assert.False(startup.Tool.Started.Task.IsCompleted);
        Assert.Equal(1, startup.ProviderEffects);
    }

    [Fact]
    public async Task AMismatchedConcreteCapabilityAdmitsNoCandidateAndNoInvocation()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetProductions = 3, UseForeignCapability = true });
        var execution = AprFixtures.Request();
        var feedback = AlwaysAccept();

        var result = await AprFixtures.WithTimeout(
            startup.Host.ExecuteCandidatesAsync(AprFixtures.Candidates(execution), feedback));

        Assert.Equal(CandidateStopReason.ProductionFailed, result.StopReason);
        Assert.Empty(feedback.Submissions);
        Assert.Equal(0, startup.ToolEffects);
        Assert.False(startup.Tool.Started.Task.IsCompleted);
    }

    [Fact]
    public async Task AWrongAssociatedToolResultAdmitsNoSuccessfulCandidate()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetProductions = 3, ToolMode = CounterMode.WrongAssociation });
        var execution = AprFixtures.Request();
        var feedback = AlwaysAccept();

        var result = await AprFixtures.WithTimeout(
            startup.Host.ExecuteCandidatesAsync(AprFixtures.Candidates(execution), feedback));

        Assert.Equal(CandidateStopReason.ProductionFailed, result.StopReason);
        Assert.Empty(feedback.Submissions);
        Assert.Empty(result.Receipts);
        // The guarded tool honestly reports its started effect while admitting no candidate.
        Assert.Equal(1, startup.ToolEffects);
        Assert.True(startup.Tool.Started.Task.IsCompleted);
    }

    [Fact]
    public async Task RequiredWorkUnitLimitIsAdmittedAndEnforcedAtTheOuterBound()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetProductions = 5 });
        var execution = AprFixtures.Request(maximumWorkUnits: 2, required: AgentCapability.WorkUnitLimit | AgentCapability.Cancellation);
        var feedback = AlwaysAccept();

        var result = await AprFixtures.WithTimeout(
            startup.Host.ExecuteCandidatesAsync(AprFixtures.Candidates(execution), feedback));

        // The advertised work-unit guarantee is honored end to end instead of breaking inside the
        // composed provider-dispatch consumer as a generic production failure.
        Assert.Equal(CandidateStopReason.WorkUnitLimit, result.StopReason);
        Assert.Equal(AgentTerminationReason.ResourceLimit, result.Outcome.Reason);
        Assert.Equal(2, feedback.Submissions.Count);
        Assert.Equal(2, startup.ToolEffects);
    }

    [Fact]
    public async Task UnsupportedRequiredGuaranteeIsRejectedBeforeWork()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetProductions = 3 });
        var execution = AprFixtures.Request(required: AgentCapability.UsageThresholds);
        var feedback = AlwaysAccept();

        var result = await AprFixtures.WithTimeout(
            startup.Host.ExecuteCandidatesAsync(AprFixtures.Candidates(execution), feedback));

        Assert.Equal(CandidateStopReason.UnsupportedCapability, result.StopReason);
        Assert.Equal(AgentTerminationReason.UnsupportedCapability, result.Outcome.Reason);
        Assert.Equal(AgentCapability.UsageThresholds, result.Outcome.UnsupportedCapabilities);
        Assert.Equal(0, startup.ProviderEffects);
        Assert.Equal(0, startup.ToolEffects);
        Assert.Empty(startup.Scenario.RecordedAttempts);
        Assert.Empty(feedback.Submissions);
    }

    [Fact]
    public async Task RequiredDispatchLimitsIsRejectedBeforeWork()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetProductions = 3 });
        var execution = AprFixtures.Request(required: AgentCapability.DispatchLimits);
        var feedback = AlwaysAccept();

        var result = await AprFixtures.WithTimeout(
            startup.Host.ExecuteCandidatesAsync(AprFixtures.Candidates(execution), feedback));

        // Configured dispatch limits stay fixture-internal policy, so the unadvertised guarantee
        // rejects before work instead of promising semantics the scenario cannot report truthfully.
        Assert.Equal(CandidateStopReason.UnsupportedCapability, result.StopReason);
        Assert.Equal(AgentCapability.DispatchLimits, result.Outcome.UnsupportedCapabilities);
        Assert.Equal(0, startup.ProviderEffects);
    }

    [Fact]
    public async Task MismatchedExecutionAssociationIsRejectedBeforeAnyWork()
    {
        var startup = AprStartup.Create();
        var contextExecution = AprFixtures.Request();
        var candidateExecution = AprFixtures.Request();
        var sink = new AprFixtures.CollectingSink();
        var feedback = AlwaysAccept();

        await Assert.ThrowsAsync<ArgumentException>(() => AprFixtures.WithTimeout(startup.Host.RunAsync(
            AprFixtures.Fresh(contextExecution), sink, AprFixtures.Candidates(candidateExecution), feedback)));

        Assert.Equal(0, startup.ProviderEffects);
        Assert.Equal(0, startup.ToolEffects);
        Assert.Equal(0, sink.CaptureCount);
        Assert.Empty(feedback.Submissions);
        Assert.Empty(startup.Scenario.RecordedAttempts);
    }

    private static ScriptedAprFeedback AlwaysAccept() => new((submission, _, _) =>
        ValueTask.FromResult<CandidateFeedback?>(AprDecisions.Accept(submission.ExecutionId, submission.SubmissionId)));
}

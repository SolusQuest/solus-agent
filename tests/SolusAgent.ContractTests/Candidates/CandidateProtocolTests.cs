using System.Text.Json;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ContractTests.Architecture;
using Xunit;

namespace SolusAgent.ContractTests.Candidates;

public sealed class CandidateProtocolTests
{
    private static readonly Guid ExecutionId = Guid.Parse("13ac8a91-6e30-442c-9721-3b12d0c852a1");

    [Fact]
    public async Task ActualAgentAndCallingHostConsumerUseOnlyApi()
    {
        var project = Path.Combine(RepositoryLayout.Root, "tests", "SolusAgent.ApiOnlyConsumer", "SolusAgent.ApiOnlyConsumer.csproj");
        var evaluation = MsbuildProjectEvaluation.Evaluate(project);
        ProjectBoundaryAssertions.AssertExactProjectReferences(evaluation, [RepositoryLayout.ProductionProjectPath("SolusAgent.Api")]);
        ProjectBoundaryAssertions.AssertNoPackages(evaluation);
        ProjectBoundaryAssertions.AssertCompileSourcesWithinRoot(evaluation, RepositoryLayout.Root);
        Assert.Contains(evaluation.CompileItemPaths, path => path.EndsWith("ScriptedCandidateAgent.cs", StringComparison.Ordinal));
        Assert.Contains(evaluation.CompileItemPaths, path => path.EndsWith("CandidateConsumer.cs", StringComparison.Ordinal));
        var references = typeof(ScriptedCandidateAgent).Assembly.GetReferencedAssemblies();
        Assert.Equal(["SolusAgent.Api"], references.Where(reference => reference.Name!.StartsWith("SolusAgent.", StringComparison.Ordinal)).Select(reference => reference.Name));
        ICandidateAgent agent = Agent("one");
        var observed = await CandidateConsumer.RunAsync(agent, Request(), AcceptingHost());
        Assert.Equal(CandidateStopReason.Completed, observed.Result.StopReason);
        Assert.IsAssignableFrom<IAgent>(agent);
    }

    [Fact]
    public async Task RejectionCorrectionUsesHostDataAndFreshCorrelatedIdentity()
    {
        var agent = new ScriptedCandidateAgent(
            (_, _) => ValueTask.FromResult("incorrect"),
            (feedback, _) => ValueTask.FromResult("corrected:" + feedback!.CorrectionText));
        var calls = 0;
        var host = new ScriptedCandidateHost((submission, _) => ValueTask.FromResult<CandidateFeedback?>(
            Feedback(submission, ++calls == 1 ? CandidateDecision.Reject : CandidateDecision.Accept,
                CandidateContinuation.Continue, "synthetic fix")));
        var observed = await CandidateConsumer.RunAsync(agent, Request(repairs: 1), host);
        Assert.Equal(CandidateStopReason.Completed, observed.Result.StopReason);
        Assert.Equal(1, observed.Result.RepairsAdmitted);
        Assert.Equal(0, observed.Result.ContinuationsAdmitted);
        Assert.Equal(1, observed.Result.AcceptedCount);
        Assert.Equal([CandidateDecision.Reject, CandidateDecision.Accept], observed.Result.Receipts.Select(receipt => receipt.Decision!.Value));
        Assert.Equal("corrected:synthetic fix", host.Submissions[1].Payload);
        Assert.Equal(host.Submissions[0].SubmissionId, host.Submissions[1].RepairsSubmissionId);
        Assert.NotEqual(host.Submissions[0].SubmissionId, host.Submissions[1].SubmissionId);
        Assert.Equal([1, 2], observed.Progress.Select(progress => progress.CompletedWorkUnits));
        AssertCorrelated(observed, host);
        Assert.Equal(0, host.Effects);
    }

    [Fact]
    public async Task TwoIndependentAcceptancesRemainSeparateFromRuntimeAndHostEffects()
    {
        var agent = Agent("one", "two");
        var host = AcceptingHost();
        var observed = await CandidateConsumer.RunAsync(agent, Request(continuations: 1), host);
        Assert.Equal(2, observed.Result.AcceptedCount);
        Assert.Equal(1, observed.Result.ContinuationsAdmitted);
        Assert.Equal(2, observed.Result.Outcome.CompletedWorkUnits);
        Assert.True(observed.Result.Outcome.IsCompleted);
        Assert.Equal(0, host.Effects);
        Assert.All(host.Submissions, submission => Assert.Null(submission.RepairsSubmissionId));
        AssertCorrelated(observed, host);
        host.ApplyEffect();
        Assert.Equal(1, host.Effects);
        Assert.Equal(2, host.Submissions.Count);
        Assert.Equal(2, observed.Result.AcceptedCount);
    }

    [Theory]
    [InlineData(CandidateDecision.Accept)]
    [InlineData(CandidateDecision.Reject)]
    public async Task HostEndStopsWithIndependentDecisionAndEarlierAcceptanceRetained(CandidateDecision finalDecision)
    {
        var calls = 0;
        var host = new ScriptedCandidateHost((submission, _) => ValueTask.FromResult<CandidateFeedback?>(
            ++calls == 1 ? Feedback(submission) : Feedback(submission, finalDecision, CandidateContinuation.End)));
        var observed = await CandidateConsumer.RunAsync(Agent("one", "two", "unused"), Request(), host);
        Assert.Equal(CandidateStopReason.HostEnded, observed.Result.StopReason);
        Assert.Equal(AgentTerminationReason.Partial, observed.Result.Outcome.Reason);
        Assert.Equal(finalDecision == CandidateDecision.Accept ? 2 : 1, observed.Result.AcceptedCount);
        Assert.Equal(finalDecision, observed.Result.Receipts[1].Decision);
        Assert.Equal(2, host.Submissions.Count);
        Assert.True(observed.Result.Outcome.HasPartialProgress);
        Assert.Equal(0, host.Effects);
    }

    [Theory]
    [InlineData(CandidateContinuation.Continue)]
    [InlineData(CandidateContinuation.End)]
    public async Task FinalAcceptanceCompletesWithoutAdmittingAnUnneededContinuation(CandidateContinuation instruction)
    {
        var host = new ScriptedCandidateHost((submission, _) => ValueTask.FromResult<CandidateFeedback?>(Feedback(submission, continuation: instruction)));
        var observed = await CandidateConsumer.RunAsync(Agent("one"), Request(submissions: 1, work: 1, continuations: 0), host);
        Assert.Equal(CandidateStopReason.Completed, observed.Result.StopReason);
        Assert.Equal(0, observed.Result.ContinuationsAdmitted);
        Assert.Single(observed.Result.Receipts);
    }

    [Fact]
    public async Task FinalRejectionNeedingCorrectionIsIncompleteRatherThanAcceptedOrCompleted()
    {
        var host = new ScriptedCandidateHost((submission, _) => ValueTask.FromResult<CandidateFeedback?>(Feedback(submission, CandidateDecision.Reject)));
        var observed = await CandidateConsumer.RunAsync(Agent("one"), Request(), host);
        Assert.Equal(CandidateStopReason.ProductionExhausted, observed.Result.StopReason);
        Assert.False(observed.Result.Outcome.IsCompleted);
        Assert.Equal(0, observed.Result.AcceptedCount);
        Assert.Equal(0, observed.Result.RepairsAdmitted);
    }

    [Fact]
    public async Task AcceptedFirstRejectedSecondPreservesAcceptanceWhenRepairAllowanceIsZero()
    {
        var calls = 0;
        var host = new ScriptedCandidateHost((submission, _) => ValueTask.FromResult<CandidateFeedback?>(
            Feedback(submission, ++calls == 1 ? CandidateDecision.Accept : CandidateDecision.Reject)));
        var observed = await CandidateConsumer.RunAsync(Agent("accepted", "rejected", "unused"), Request(repairs: 0), host);
        Assert.Equal(CandidateStopReason.RepairLimit, observed.Result.StopReason);
        Assert.True(observed.Result.Receipts[0].IsAccepted);
        Assert.Equal(CandidateDecision.Reject, observed.Result.Receipts[1].Decision);
        Assert.Equal(1, observed.Result.AcceptedCount);
        Assert.Equal(2, host.Submissions.Count);
        Assert.Equal(2, observed.Result.Outcome.CompletedWorkUnits);
        Assert.Equal(0, observed.Result.RepairsAdmitted);
        Assert.Equal(0, host.Effects);
    }

    [Theory]
    [InlineData("submissions", CandidateStopReason.SubmissionLimit)]
    [InlineData("work", CandidateStopReason.WorkUnitLimit)]
    [InlineData("repairs", CandidateStopReason.RepairLimit)]
    [InlineData("continuations", CandidateStopReason.ContinuationLimit)]
    public async Task ExhaustedBoundsPreventNextProductionAndSubmission(string bound, CandidateStopReason expected)
    {
        var agent = Agent("one", "must not produce");
        var host = new ScriptedCandidateHost((submission, _) => ValueTask.FromResult<CandidateFeedback?>(
            Feedback(submission, bound == "repairs" ? CandidateDecision.Reject : CandidateDecision.Accept)));
        var request = Request(submissions: bound == "submissions" ? 1 : 3, work: bound == "work" ? 1 : 3,
            repairs: bound == "repairs" ? 0 : 2, continuations: bound == "continuations" ? 0 : 2);
        var observed = await CandidateConsumer.RunAsync(agent, request, host);
        Assert.Equal(expected, observed.Result.StopReason);
        Assert.Equal(AgentTerminationReason.ResourceLimit, observed.Result.Outcome.Reason);
        Assert.Equal(1, agent.TotalProductionStarted);
        Assert.Single(host.Submissions);
        Assert.Single(observed.Result.Receipts);
        Assert.Equal(0, observed.Result.RepairsAdmitted);
        Assert.Equal(0, observed.Result.ContinuationsAdmitted);
    }

    [Theory]
    [InlineData(CandidateDecision.Accept)]
    [InlineData(CandidateDecision.Reject)]
    public async Task ExactlyOneFollowOnAllowancePermitsOneFollowOnThenStops(CandidateDecision decision)
    {
        var host = new ScriptedCandidateHost((submission, _) => ValueTask.FromResult<CandidateFeedback?>(Feedback(submission, decision)));
        var observed = await CandidateConsumer.RunAsync(Agent("one", "two", "three"), Request(repairs: 1, continuations: 1), host);
        Assert.Equal(decision == CandidateDecision.Accept ? CandidateStopReason.ContinuationLimit : CandidateStopReason.RepairLimit, observed.Result.StopReason);
        Assert.Equal(2, host.Submissions.Count);
        Assert.Equal(decision == CandidateDecision.Accept ? 1 : 0, observed.Result.ContinuationsAdmitted);
        Assert.Equal(decision == CandidateDecision.Reject ? 1 : 0, observed.Result.RepairsAdmitted);
    }

    [Theory]
    [InlineData(CandidateAcknowledgement.Missing)]
    [InlineData(CandidateAcknowledgement.Failed)]
    [InlineData(CandidateAcknowledgement.Unknown)]
    public async Task UnacknowledgedSecondSubmissionNeverErasesFirstAcceptanceOrRetries(CandidateAcknowledgement state)
    {
        var calls = 0;
        var host = new ScriptedCandidateHost((submission, _) => ValueTask.FromResult<CandidateFeedback?>(++calls == 1
            ? Feedback(submission)
            : state == CandidateAcknowledgement.Missing ? null : new CandidateFeedback(submission.ExecutionId, submission.SubmissionId, state)));
        var observed = await CandidateConsumer.RunAsync(Agent("one", "two", "unused"), Request(), host);
        Assert.Equal(1, observed.Result.AcceptedCount);
        Assert.True(observed.Result.Receipts[0].IsAccepted);
        Assert.Equal(state, observed.Result.Receipts[1].Acknowledgement);
        Assert.Null(observed.Result.Receipts[1].Decision);
        Assert.Null(observed.Result.Receipts[1].Continuation);
        Assert.False(observed.Result.Outcome.IsCompleted);
        Assert.Equal(state == CandidateAcknowledgement.Failed ? AgentTerminationReason.Failed : AgentTerminationReason.Partial, observed.Result.Outcome.Reason);
        Assert.Equal(2, host.Submissions.Count);
        Assert.Equal(0, host.Effects);
        AssertCorrelated(observed, host);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostFailureAfterAnEffectRemainsUnknownToAcceptanceAndIsNeverReplayed(bool unrelatedCancellation)
    {
        var effects = 0;
        var host = new ScriptedCandidateHost((_, _) =>
        {
            effects++;
            return ValueTask.FromException<CandidateFeedback?>(unrelatedCancellation
                ? new OperationCanceledException("exception-canary") : new InvalidOperationException("exception-canary"));
        });
        var observed = await CandidateConsumer.RunAsync(Agent("one", "unused"), Request(), host);
        Assert.Equal(CandidateStopReason.FailedAcknowledgement, observed.Result.StopReason);
        Assert.Equal(CandidateAcknowledgement.Failed, Assert.Single(observed.Result.Receipts).Acknowledgement);
        Assert.Equal(0, observed.Result.AcceptedCount);
        Assert.Equal(1, effects);
        Assert.Single(host.Submissions);
        Assert.DoesNotContain("exception-canary", JsonSerializer.Serialize(observed));
    }

    [Theory]
    [InlineData("execution")]
    [InlineData("submission")]
    [InlineData("old-execution")]
    public async Task WrongCorrelationCannotUseItsAcceptOrEndInstruction(string mismatch)
    {
        CandidateFeedback? first = null;
        var host = new ScriptedCandidateHost((submission, _) =>
        {
            if (first is null)
            {
                first = Feedback(submission);
                return ValueTask.FromResult<CandidateFeedback?>(first);
            }
            return ValueTask.FromResult<CandidateFeedback?>(new CandidateFeedback(
                mismatch == "submission" ? submission.ExecutionId : Guid.NewGuid(),
                mismatch == "old-execution" ? first.SubmissionId : mismatch == "execution" ? submission.SubmissionId : Guid.NewGuid(),
                CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept, CandidateContinuation.End));
        });
        var observed = await CandidateConsumer.RunAsync(Agent("one", "two", "unused"), Request(), host);
        Assert.Equal(CandidateStopReason.MismatchedFeedback, observed.Result.StopReason);
        Assert.Equal(CandidateAcknowledgement.Mismatched, observed.Result.Receipts[1].Acknowledgement);
        Assert.Null(observed.Result.Receipts[1].Decision);
        Assert.Equal(1, observed.Result.AcceptedCount);
        AssertCorrelated(observed, host);
    }

    [Theory]
    [InlineData(CandidateDecision.Accept)]
    [InlineData(CandidateDecision.Reject)]
    public async Task ReplayedEarlierFeedbackIsDuplicateAndCannotDecidePendingCandidate(CandidateDecision originalDecision)
    {
        CandidateFeedback? first = null;
        var host = new ScriptedCandidateHost((submission, _) => ValueTask.FromResult<CandidateFeedback?>(first ??= Feedback(submission, originalDecision)));
        var observed = await CandidateConsumer.RunAsync(Agent("one", "two", "unused"), Request(), host);
        Assert.Equal(CandidateStopReason.DuplicateFeedback, observed.Result.StopReason);
        Assert.Equal(CandidateAcknowledgement.Duplicate, observed.Result.Receipts[1].Acknowledgement);
        Assert.Null(observed.Result.Receipts[1].Decision);
        Assert.Equal(originalDecision == CandidateDecision.Accept ? 1 : 0, observed.Result.AcceptedCount);
        AssertCorrelated(observed, host);
        Assert.Equal(2, host.Submissions.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationWhileNonCooperativeHostIsPendingRetainsPriorAcksAndIgnoresLateAcceptance(bool acceptFirst)
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<CandidateFeedback?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = new TaskCompletionSource<CandidateSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var effects = 0;
        var host = new ScriptedCandidateHost((submission, _) =>
        {
            if (acceptFirst && ++calls == 1)
            {
                return ValueTask.FromResult<CandidateFeedback?>(Feedback(submission));
            }
            effects++; // The Host may have acted even though the acknowledgement is still pending.
            invoked.SetResult(submission);
            return new ValueTask<CandidateFeedback?>(pending.Task); // Deliberately ignores cancellation.
        });
        var run = CandidateConsumer.RunAsync(Agent("one", "two", "unused"), Request(), host, cancellation.Token).AsTask();
        var awaiting = await invoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var observed = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CandidateStopReason.Cancelled, observed.Result.StopReason);
        Assert.Equal(acceptFirst ? 1 : 0, observed.Result.AcceptedCount);
        Assert.Equal(CandidateAcknowledgement.Unknown, observed.Result.Receipts[^1].Acknowledgement);
        Assert.Null(observed.Result.Receipts[^1].Decision);
        var before = JsonSerializer.Serialize(observed);
        pending.SetResult(Feedback(awaiting));
        await pending.Task;
        Assert.Equal(before, JsonSerializer.Serialize(observed));
        Assert.Equal(acceptFirst ? 2 : 1, host.Submissions.Count);
        Assert.Equal(1, effects);
        Assert.Equal(0, host.Effects);
        AssertCorrelated(observed, host);
    }

    [Theory]
    [InlineData(1, CandidateStopReason.Completed)]
    [InlineData(2, CandidateStopReason.Cancelled)]
    public async Task DeliveredAcknowledgementSurvivesCancellationAndPreventsOnlyFutureWork(int productions, CandidateStopReason expected)
    {
        using var cancellation = new CancellationTokenSource();
        var host = new ScriptedCandidateHost((submission, _) =>
        {
            var acknowledgement = Feedback(submission);
            cancellation.Cancel();
            return ValueTask.FromResult<CandidateFeedback?>(acknowledgement);
        });
        var observed = await CandidateConsumer.RunAsync(Agent(Enumerable.Repeat("candidate", productions).ToArray()), Request(), host, cancellation.Token);
        Assert.Equal(expected, observed.Result.StopReason);
        Assert.True(Assert.Single(observed.Result.Receipts).IsAccepted);
        Assert.Equal(1, observed.Result.AcceptedCount);
        Assert.Single(host.Submissions);
        Assert.Equal(0, observed.Result.ContinuationsAdmitted);
    }

    [Fact]
    public async Task UnsupportedRequiredCapabilityRejectsBeforeCancellationWorkProgressAndHostEffects()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var agent = Agent("must not produce");
        var host = new ScriptedCandidateHost((_, _) => throw new InvalidOperationException("Host must not be called"));
        var observed = await CandidateConsumer.RunAsync(agent, Request(required: AgentCapability.WorkUnitLimit | AgentCapability.DurationLimit), host, cancellation.Token);
        Assert.Equal(CandidateStopReason.UnsupportedCapability, observed.Result.StopReason);
        Assert.Equal(AgentTerminationReason.UnsupportedCapability, observed.Result.Outcome.Reason);
        Assert.Equal(AgentCapability.DurationLimit, observed.Result.Outcome.UnsupportedCapabilities);
        Assert.False(agent.SupportedCapabilities.HasFlag(AgentCapability.DurationLimit));
        Assert.Equal(0, observed.Result.Outcome.CompletedWorkUnits);
        Assert.Equal(0, agent.TotalProductionStarted);
        Assert.Empty(observed.Progress);
        Assert.Empty(host.Submissions);
        Assert.Empty(observed.Result.Receipts);
        Assert.Equal(0, observed.Result.AcceptedCount);
        Assert.Equal(0, host.Effects);
    }

    [Fact]
    public async Task ObserverFailurePreservesProductionButNeverInvokesHost()
    {
        var agent = Agent("candidate-canary");
        var host = new ScriptedCandidateHost((_, _) => throw new InvalidOperationException("Host must not be called"));
        var result = await agent.ExecuteCandidatesAsync(Request(), host, new InlineProgress(_ => throw new InvalidOperationException("observer-canary")));
        Assert.Equal(CandidateStopReason.ProgressObserverFailed, result.StopReason);
        Assert.Equal(AgentTerminationReason.Failed, result.Outcome.Reason);
        Assert.Equal(AgentFailureCode.ProgressObserverFailed, result.Outcome.FailureCode);
        Assert.Equal(1, result.Outcome.CompletedWorkUnits);
        Assert.Equal(1, agent.TotalProductionStarted);
        Assert.Empty(host.Submissions);
        Assert.Empty(result.Receipts);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(0, host.Effects);
        Assert.DoesNotContain("candidate-canary", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("observer-canary", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task CancellationBeforeSubmissionHasNoUnknownReceiptForAnUnsentCandidate()
    {
        using var cancellation = new CancellationTokenSource();
        var host = AcceptingHost();
        var result = await Agent("one").ExecuteCandidatesAsync(Request(), host, new InlineProgress(_ => cancellation.Cancel()), cancellation.Token);
        Assert.Equal(CandidateStopReason.Cancelled, result.StopReason);
        Assert.Equal(1, result.Outcome.CompletedWorkUnits);
        Assert.Empty(result.Receipts);
        Assert.Empty(host.Submissions);
    }

    [Fact]
    public async Task PreCancelledSupportedRequestStartsNoProductionOrSubmission()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var agent = Agent("one");
        var host = AcceptingHost();
        var observed = await CandidateConsumer.RunAsync(agent, Request(), host, cancellation.Token);
        Assert.Equal(CandidateStopReason.Cancelled, observed.Result.StopReason);
        Assert.Equal(0, agent.TotalProductionStarted);
        Assert.Empty(host.Submissions);
        Assert.Empty(observed.Progress);
        Assert.Empty(observed.Result.Receipts);
    }

    [Fact]
    public async Task ProductionFailureRetainsEarlierAcceptanceAndReturnsOnlyFixedDiagnostics()
    {
        var agent = new ScriptedCandidateAgent((_, _) => ValueTask.FromResult("candidate-canary"),
            (_, _) => ValueTask.FromException<string>(new InvalidOperationException("production-canary")));
        var host = AcceptingHost();
        var observed = await CandidateConsumer.RunAsync(agent, Request(), host);
        Assert.Equal(CandidateStopReason.ProductionFailed, observed.Result.StopReason);
        Assert.Equal(1, observed.Result.AcceptedCount);
        Assert.Equal(1, observed.Result.Outcome.CompletedWorkUnits);
        Assert.Single(host.Submissions);
        Assert.Equal(1, observed.Result.ContinuationsAdmitted);
        Assert.DoesNotContain("production-canary", JsonSerializer.Serialize(observed));
        Assert.DoesNotContain("candidate-canary", JsonSerializer.Serialize(observed));
    }

    [Fact]
    public async Task PayloadAndCorrectionCanariesStayInHostChannelAndOutOfAllOrdinaryObservations()
    {
        const string canary = "synthetic-candidate-credential-context-canary";
        var agent = Agent(canary, canary);
        var host = new ScriptedCandidateHost((submission, _) => ValueTask.FromResult<CandidateFeedback?>(Feedback(submission, correction: canary)));
        var request = new CandidateExecutionRequest(new AgentRequest(ExecutionId, canary, [new AgentInput(AgentInputSource.Model, canary)],
            new AgentExecutionBounds(3, TimeSpan.FromSeconds(1)), AgentCapability.WorkUnitLimit), new CandidateExecutionBounds(3, 1, 1));
        var observed = await CandidateConsumer.RunAsync(agent, request, host);
        Assert.All(host.Submissions, submission => Assert.Equal(canary, submission.Payload));
        var ordinary = JsonSerializer.Serialize(observed) + request + request.Execution + string.Join(";", host.Submissions)
            + Feedback(host.Submissions[0], correction: canary);
        Assert.DoesNotContain(canary, ordinary);
        Assert.DoesNotContain("Payload", JsonSerializer.Serialize(observed));
        Assert.DoesNotContain("CorrectionText", JsonSerializer.Serialize(observed));
        AssertCorrelated(observed, host);
    }

    private static CandidateExecutionRequest Request(int submissions = 4, int repairs = 2, int continuations = 2, int work = 4,
        AgentCapability required = AgentCapability.WorkUnitLimit | AgentCapability.Cancellation) =>
        new(new AgentRequest(ExecutionId, "Trusted synthetic Host task", [], new AgentExecutionBounds(work, TimeSpan.FromSeconds(1)), required),
            new CandidateExecutionBounds(submissions, repairs, continuations));

    private static ScriptedCandidateAgent Agent(params string[] payloads) =>
        new(payloads.Select<string, Func<CandidateFeedback?, CancellationToken, ValueTask<string>>>(payload => (_, _) => ValueTask.FromResult(payload)).ToArray());

    private static CandidateFeedback Feedback(CandidateSubmission submission, CandidateDecision decision = CandidateDecision.Accept,
        CandidateContinuation continuation = CandidateContinuation.Continue, string? correction = null) =>
        new(submission.ExecutionId, submission.SubmissionId, CandidateAcknowledgement.Acknowledged, decision, continuation, correction);

    private static ScriptedCandidateHost AcceptingHost() => new((submission, _) => ValueTask.FromResult<CandidateFeedback?>(Feedback(submission)));

    private static void AssertCorrelated(ObservedCandidateExecution observed, ScriptedCandidateHost host)
    {
        Assert.Equal(ExecutionId, observed.Result.Outcome.ExecutionId);
        Assert.All(observed.Progress, progress => Assert.Equal(ExecutionId, progress.ExecutionId));
        Assert.Equal(host.Submissions.Select(submission => submission.SubmissionId), observed.Result.Receipts.Select(receipt => receipt.SubmissionId));
        Assert.All(observed.Result.Receipts, receipt => Assert.Equal(ExecutionId, receipt.ExecutionId));
        Assert.Equal(host.Submissions.Count, host.Submissions.Select(submission => submission.SubmissionId).Distinct().Count());
    }

    private sealed class InlineProgress(Action<AgentProgress> report) : IProgress<AgentProgress>
    {
        public void Report(AgentProgress value) => report(value);
    }
}

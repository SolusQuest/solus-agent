using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Candidates;
using Xunit;

namespace SolusAgent.ContractTests.Candidates;

public sealed class CandidateContractTests
{
    private static readonly Guid ExecutionId = Guid.Parse("2ec330cc-f8c1-465a-b099-28bc7bcd98a1");
    private static readonly Guid SubmissionId = Guid.Parse("e4d6fd30-e011-4874-9bbe-9c68cb7d9d72");

    [Fact]
    public void HostTextBoundsUseActualUtf8BytesWithoutDomainValidationOrDiagnosticEcho()
    {
        var payload = string.Concat(Enumerable.Repeat("😀", CandidateSubmission.MaximumPayloadBytes / 4));
        Assert.Equal(payload, new CandidateSubmission(ExecutionId, SubmissionId, payload).Payload);
        Assert.Equal(string.Empty, new CandidateSubmission(ExecutionId, SubmissionId, string.Empty).Payload);
        var correction = new string('x', CandidateFeedback.MaximumCorrectionBytes);
        Assert.Equal(correction, Feedback(correction).CorrectionText);
        var errors = new Exception[]
        {
            Assert.Throws<ArgumentException>(() => new CandidateSubmission(ExecutionId, SubmissionId, payload + "x")),
            Assert.Throws<ArgumentException>(() => Feedback(correction + "x")),
            Assert.Throws<ArgumentException>(() => new CandidateSubmission(ExecutionId, SubmissionId, "utf8-canary\ud800")),
            Assert.Throws<ArgumentException>(() => Feedback("utf8-canary\ud800")),
            Assert.Throws<ArgumentNullException>(() => new CandidateSubmission(ExecutionId, SubmissionId, null!)),
        };
        Assert.All(errors, error =>
        {
            Assert.DoesNotContain("utf8-canary", error.ToString());
            Assert.DoesNotContain(payload, error.ToString());
            Assert.DoesNotContain(correction, error.ToString());
        });
    }

    [Fact]
    public void SubmissionAndFeedbackIdentityValidationNeverIncludesPayloadOrCorrection()
    {
        var errors = new Exception[]
        {
            Assert.Throws<ArgumentException>(() => new CandidateSubmission(Guid.Empty, SubmissionId, "candidate-canary")),
            Assert.Throws<ArgumentException>(() => new CandidateSubmission(ExecutionId, Guid.Empty, "candidate-canary")),
            Assert.Throws<ArgumentException>(() => new CandidateSubmission(ExecutionId, SubmissionId, "candidate-canary", Guid.Empty)),
            Assert.Throws<ArgumentException>(() => new CandidateSubmission(ExecutionId, SubmissionId, "candidate-canary", SubmissionId)),
            Assert.Throws<ArgumentException>(() => new CandidateFeedback(Guid.Empty, SubmissionId, CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept, CandidateContinuation.End, "feedback-canary")),
            Assert.Throws<ArgumentException>(() => new CandidateFeedback(ExecutionId, Guid.Empty, CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept, CandidateContinuation.End, "feedback-canary")),
            Assert.Throws<ArgumentException>(() => new CandidateReceipt(Guid.Empty, SubmissionId, CandidateAcknowledgement.Missing)),
            Assert.Throws<ArgumentException>(() => new CandidateReceipt(ExecutionId, Guid.Empty, CandidateAcknowledgement.Missing)),
        };
        Assert.All(errors, error => Assert.DoesNotContain("canary", error.ToString()));
    }

    [Theory]
    [InlineData(CandidateAcknowledgement.Missing)]
    [InlineData(CandidateAcknowledgement.Failed)]
    [InlineData(CandidateAcknowledgement.Unknown)]
    [InlineData(CandidateAcknowledgement.Mismatched)]
    [InlineData(CandidateAcknowledgement.Duplicate)]
    public void UnacknowledgedStatesCannotContainAnAcceptanceOrContinuation(CandidateAcknowledgement state)
    {
        var receipt = new CandidateReceipt(ExecutionId, SubmissionId, state);
        Assert.False(receipt.IsAccepted);
        Assert.Null(receipt.Decision);
        Assert.Null(receipt.Continuation);
        Assert.Throws<ArgumentException>(() => new CandidateReceipt(ExecutionId, SubmissionId, state, CandidateDecision.Accept));
        Assert.Throws<ArgumentException>(() => new CandidateReceipt(ExecutionId, SubmissionId, state, continuation: CandidateContinuation.Continue));
        if (state is CandidateAcknowledgement.Failed or CandidateAcknowledgement.Unknown)
        {
            Assert.Throws<ArgumentException>(() => new CandidateFeedback(ExecutionId, SubmissionId, state, CandidateDecision.Accept, CandidateContinuation.Continue));
            Assert.Throws<ArgumentException>(() => new CandidateFeedback(ExecutionId, SubmissionId, state, correctionText: "feedback-canary"));
        }
        else
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CandidateFeedback(ExecutionId, SubmissionId, state));
        }
    }

    [Fact]
    public void AcknowledgedMetadataRequiresDefinedIndependentDecisionAndInstruction()
    {
        Assert.Throws<ArgumentException>(() => new CandidateReceipt(ExecutionId, SubmissionId, CandidateAcknowledgement.Acknowledged));
        Assert.Throws<ArgumentException>(() => new CandidateFeedback(ExecutionId, SubmissionId, CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept));
        Assert.Throws<ArgumentException>(() => new CandidateFeedback(ExecutionId, SubmissionId, CandidateAcknowledgement.Acknowledged, continuation: CandidateContinuation.End));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CandidateReceipt(ExecutionId, SubmissionId, (CandidateAcknowledgement)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CandidateFeedback(ExecutionId, SubmissionId, (CandidateAcknowledgement)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CandidateReceipt(ExecutionId, SubmissionId, CandidateAcknowledgement.Acknowledged, (CandidateDecision)99, CandidateContinuation.End));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CandidateFeedback(ExecutionId, SubmissionId, CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept, (CandidateContinuation)99));
    }

    [Fact]
    public void TerminalReceiptSnapshotCannotBeMutatedOrEraseEarlierAcceptance()
    {
        var first = new CandidateReceipt(ExecutionId, SubmissionId, CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept, CandidateContinuation.Continue);
        var list = new List<CandidateReceipt> { first, new(ExecutionId, Guid.NewGuid(), CandidateAcknowledgement.Unknown) };
        var result = new CandidateExecutionResult(new AgentOutcome(ExecutionId, AgentTerminationReason.Cancelled, 2), CandidateStopReason.Cancelled, list, continuationsAdmitted: 1);
        list.Clear();
        Assert.Equal(2, result.Receipts.Count);
        Assert.Same(first, result.Receipts[0]);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Throws<NotSupportedException>(() => ((IList<CandidateReceipt>)result.Receipts).Clear());
        Assert.All(typeof(CandidateReceipt).GetProperties(), property => Assert.Null(property.SetMethod));
    }

    [Fact]
    public void ResultRejectsWrongExecutionDuplicateSubmissionAndIncoherentCountersOrStop()
    {
        var outcome = new AgentOutcome(ExecutionId, AgentTerminationReason.Completed, 1);
        var receipt = new CandidateReceipt(ExecutionId, SubmissionId, CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept, CandidateContinuation.End);
        Assert.Throws<ArgumentException>(() => new CandidateExecutionResult(outcome, CandidateStopReason.Completed, [new(Guid.NewGuid(), SubmissionId, CandidateAcknowledgement.Unknown)]));
        Assert.Throws<ArgumentException>(() => new CandidateExecutionResult(new AgentOutcome(ExecutionId, AgentTerminationReason.Completed, 2), CandidateStopReason.Completed, [receipt, receipt]));
        Assert.Throws<ArgumentException>(() => new CandidateExecutionResult(outcome, CandidateStopReason.Completed, [null!]));
        Assert.Throws<ArgumentException>(() => new CandidateExecutionResult(new AgentOutcome(ExecutionId, AgentTerminationReason.UnsupportedCapability, 0, AgentCapability.DurationLimit), CandidateStopReason.UnsupportedCapability, [receipt]));
        Assert.Throws<ArgumentException>(() => new CandidateExecutionResult(outcome, CandidateStopReason.Completed, [receipt], repairsAdmitted: 1));
        Assert.Throws<ArgumentException>(() => new CandidateExecutionResult(outcome, CandidateStopReason.Completed, [receipt], continuationsAdmitted: 2));
        Assert.Throws<ArgumentException>(() => new CandidateExecutionResult(outcome, CandidateStopReason.UnknownAcknowledgement, [receipt]));
        Assert.Throws<ArgumentException>(() => new CandidateExecutionResult(new AgentOutcome(ExecutionId, AgentTerminationReason.Failed, 0, failureCode: AgentFailureCode.ExecutionFailed), CandidateStopReason.ProgressObserverFailed, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CandidateExecutionResult(outcome, (CandidateStopReason)99, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CandidateExecutionResult(outcome, CandidateStopReason.Completed, [], repairsAdmitted: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CandidateExecutionResult(outcome, CandidateStopReason.Completed, [], continuationsAdmitted: -1));
        Assert.Throws<ArgumentNullException>(() => new CandidateExecutionResult(null!, CandidateStopReason.Completed, []));
        Assert.Throws<ArgumentNullException>(() => new CandidateExecutionResult(outcome, CandidateStopReason.Completed, null!));
    }

    [Fact]
    public void ReceiptAndAcknowledgementCountsRemainIndependentOfImplementationDefinedWorkUnits()
    {
        var first = new CandidateReceipt(ExecutionId, SubmissionId, CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept, CandidateContinuation.Continue);
        var second = new CandidateReceipt(ExecutionId, Guid.NewGuid(), CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept, CandidateContinuation.End);
        var result = new CandidateExecutionResult(new AgentOutcome(ExecutionId, AgentTerminationReason.Completed, 1), CandidateStopReason.Completed, [first, second], continuationsAdmitted: 1);
        Assert.Equal(1, result.Outcome.CompletedWorkUnits);
        Assert.Equal(2, result.Receipts.Count);
        Assert.Equal(2, result.AcceptedCount);
    }

    [Theory]
    [InlineData(CandidateDecision.Accept)]
    [InlineData(CandidateDecision.Reject)]
    public void HostEndCannotJustifyAFollowOnAdmissionCount(CandidateDecision decision)
    {
        var receipt = new CandidateReceipt(ExecutionId, SubmissionId, CandidateAcknowledgement.Acknowledged, decision, CandidateContinuation.End);
        Assert.Throws<ArgumentException>(() => new CandidateExecutionResult(new AgentOutcome(ExecutionId, AgentTerminationReason.Partial, 1),
            CandidateStopReason.HostEnded, [receipt], repairsAdmitted: decision == CandidateDecision.Reject ? 1 : 0,
            continuationsAdmitted: decision == CandidateDecision.Accept ? 1 : 0));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(-1, 0, 0)]
    [InlineData(1, -1, 0)]
    [InlineData(1, 0, -1)]
    public void InvalidBoundsAreRejectedBeforeExecution(int submissions, int repairs, int continuations)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CandidateExecutionBounds(submissions, repairs, continuations));
    }

    [Fact]
    public async Task NullInputsAreRejectedBeforeProduction()
    {
        var execution = new AgentRequest(ExecutionId, "Host task", [], new AgentExecutionBounds(2, TimeSpan.FromSeconds(1)), AgentCapability.WorkUnitLimit);
        Assert.Throws<ArgumentNullException>(() => new CandidateExecutionRequest(null!, new CandidateExecutionBounds(1, 0, 0)));
        Assert.Throws<ArgumentNullException>(() => new CandidateExecutionRequest(execution, null!));
        var agent = new ScriptedCandidateAgent((_, _) => ValueTask.FromResult("candidate"));
        var host = new ScriptedCandidateHost((_, _) => ValueTask.FromResult<CandidateFeedback?>(null));
        await Assert.ThrowsAsync<ArgumentNullException>(() => agent.ExecuteCandidatesAsync(null!, host).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => agent.ExecuteCandidatesAsync(new(execution, new(1, 0, 0)), null!).AsTask());
        Assert.Equal(0, agent.TotalProductionStarted);
        Assert.Empty(host.Submissions);
    }

    [Fact]
    public void OrdinaryResultTypeGraphHasNoFreeTextOrHostPayloadReferences()
    {
        var allowed = new HashSet<Type> { typeof(Guid), typeof(int), typeof(bool), typeof(long), typeof(decimal) };
        var visited = new HashSet<Type>();
        Inspect(typeof(CandidateExecutionResult));
        return;

        void Inspect(Type type)
        {
            if (allowed.Contains(type) || type.IsEnum || !visited.Add(type)) { return; }
            if (Nullable.GetUnderlyingType(type) is Type underlying) { Inspect(underlying); return; }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)) { Inspect(type.GenericTypeArguments[0]); return; }
            Assert.Contains(type, new[] { typeof(CandidateExecutionResult), typeof(CandidateReceipt), typeof(AgentOutcome),
                typeof(AgentRunUsage), typeof(ToolInvocationUsage), typeof(RunTokenObservation), typeof(UsageAttemptObservation), typeof(UsageObservation), typeof(ProviderTokenCounter),
                typeof(UsageAccounting), typeof(UsageTokenAmounts), typeof(UsageCostEstimate) });
            Assert.All(type.GetProperties(), property =>
            {
                Assert.Null(property.SetMethod);
                if (type == typeof(UsageCostEstimate) && property.Name == nameof(UsageCostEstimate.Currency))
                {
                    Assert.Equal(typeof(string), property.PropertyType); // Accepted bounded three-uppercase-letter label only.
                    return;
                }
                Inspect(property.PropertyType);
            });
        }
    }

    private static CandidateFeedback Feedback(string correction) =>
        new(ExecutionId, SubmissionId, CandidateAcknowledgement.Acknowledged, CandidateDecision.Reject, CandidateContinuation.Continue, correction);
}

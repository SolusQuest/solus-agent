using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using Xunit;

namespace SolusAgent.ContractTests.Candidates;

public sealed class CandidateTerminalTests
{
    private static readonly Guid ExecutionId = Guid.Parse("0835a98a-6891-438e-8c36-506dba39705f");

    public static TheoryData<CandidateStopReason, CandidateAcknowledgement> AcknowledgementStops => new()
    {
        { CandidateStopReason.MissingAcknowledgement, CandidateAcknowledgement.Missing },
        { CandidateStopReason.FailedAcknowledgement, CandidateAcknowledgement.Failed },
        { CandidateStopReason.UnknownAcknowledgement, CandidateAcknowledgement.Unknown },
        { CandidateStopReason.MismatchedFeedback, CandidateAcknowledgement.Mismatched },
        { CandidateStopReason.DuplicateFeedback, CandidateAcknowledgement.Duplicate },
    };

    public static IEnumerable<object[]> WrongTerminalAcknowledgements => AcknowledgementStops
        .SelectMany(pair => Enum.GetValues<CandidateAcknowledgement>()
            .Where(state => state != (CandidateAcknowledgement)pair[1])
            .Select(state => new object[] { pair[0], state }));

    public static IEnumerable<object[]> AcknowledgementStopReasons => AcknowledgementStops
        .Select(pair => new object[] { pair[0] });

    [Theory]
    [MemberData(nameof(AcknowledgementStops))]
    public void AcknowledgementStopRequiresMatchingLastReceiptAndPreservesPriorAcceptance(CandidateStopReason stop, CandidateAcknowledgement state)
    {
        var first = Receipt(CandidateAcknowledgement.Acknowledged);
        var last = Receipt(state);
        var result = new CandidateExecutionResult(Outcome(stop), stop, [first, last]);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Same(first, result.Receipts[0]);
        Assert.Same(last, result.Receipts[^1]);
        Assert.Equal(state, last.Acknowledgement);
        Assert.Null(last.Decision);
        Assert.Equal(0, result.Outcome.CompletedWorkUnits); // No universal work-unit/receipt coupling.
        Assert.Equal(stop, new CandidateExecutionResult(Outcome(stop), stop, [last]).StopReason);
    }

    [Theory]
    [MemberData(nameof(WrongTerminalAcknowledgements))]
    public void WrongTerminalAcknowledgementCannotBeHiddenByBroadOutcome(CandidateStopReason stop, CandidateAcknowledgement wrong)
    {
        var first = Receipt(CandidateAcknowledgement.Acknowledged);
        CandidateReceipt[] receipts = [first, Receipt(wrong)];
        Assert.Throws<ArgumentException>(() => new CandidateExecutionResult(Outcome(stop), stop, receipts));
        Assert.True(first.IsAccepted);
        Assert.Equal(2, receipts.Length);
    }

    [Theory]
    [MemberData(nameof(AcknowledgementStops))]
    public void EarlierMatchingReceiptCannotJustifyStopWhenLastIsAcknowledged(CandidateStopReason stop, CandidateAcknowledgement earlier)
    {
        Assert.Throws<ArgumentException>(() => new CandidateExecutionResult(Outcome(stop), stop,
            [Receipt(earlier), Receipt(CandidateAcknowledgement.Acknowledged)]));
    }

    [Theory]
    [MemberData(nameof(AcknowledgementStopReasons))]
    public void AcknowledgementSpecificStopCannotInventAnUnobservedSubmission(CandidateStopReason stop)
    {
        Assert.Throws<ArgumentException>(() => new CandidateExecutionResult(Outcome(stop), stop, []));
    }

    [Theory]
    [InlineData(CandidateDecision.Accept)]
    [InlineData(CandidateDecision.Reject)]
    public void HostEndedRequiresLastAcknowledgedEndRegardlessOfDecision(CandidateDecision decision)
    {
        var first = Receipt(CandidateAcknowledgement.Acknowledged);
        var last = Receipt(CandidateAcknowledgement.Acknowledged, decision, CandidateContinuation.End);
        var result = new CandidateExecutionResult(Outcome(CandidateStopReason.HostEnded), CandidateStopReason.HostEnded, [first, last]);
        Assert.Equal(decision == CandidateDecision.Accept ? 2 : 1, result.AcceptedCount);
        Assert.Equal(CandidateContinuation.End, result.Receipts[^1].Continuation);
        Assert.Throws<ArgumentException>(() => new CandidateExecutionResult(Outcome(CandidateStopReason.HostEnded), CandidateStopReason.HostEnded,
            [last, Receipt(CandidateAcknowledgement.Acknowledged, decision, CandidateContinuation.Continue)]));
    }

    [Theory]
    [InlineData(CandidateAcknowledgement.Missing)]
    [InlineData(CandidateAcknowledgement.Failed)]
    [InlineData(CandidateAcknowledgement.Unknown)]
    [InlineData(CandidateAcknowledgement.Mismatched)]
    [InlineData(CandidateAcknowledgement.Duplicate)]
    public void HostEndedCannotUseUnacknowledgedReceiptAsHostInstruction(CandidateAcknowledgement state)
    {
        Assert.Throws<ArgumentException>(() => new CandidateExecutionResult(Outcome(CandidateStopReason.HostEnded), CandidateStopReason.HostEnded, [Receipt(state)]));
    }

    [Fact]
    public void HostEndedCannotExistWithoutFeedback()
    {
        Assert.Throws<ArgumentException>(() => new CandidateExecutionResult(Outcome(CandidateStopReason.HostEnded), CandidateStopReason.HostEnded, []));
    }

    [Theory]
    [InlineData(CandidateStopReason.RepairLimit, CandidateDecision.Reject)]
    [InlineData(CandidateStopReason.ProductionExhausted, CandidateDecision.Reject)]
    [InlineData(CandidateStopReason.ContinuationLimit, CandidateDecision.Accept)]
    public void SpecificFollowOnStopRequiresMatchingTerminalDecisionAndContinue(CandidateStopReason stop, CandidateDecision decision)
    {
        var first = Receipt(CandidateAcknowledgement.Acknowledged);
        var last = Receipt(CandidateAcknowledgement.Acknowledged, decision);
        var result = new CandidateExecutionResult(Outcome(stop), stop, [first, last]);
        Assert.Same(last, result.Receipts[^1]);
        Assert.Equal(decision == CandidateDecision.Accept ? 2 : 1, result.AcceptedCount);
        Assert.Throws<ArgumentException>(() => new CandidateExecutionResult(Outcome(stop), stop, []));
        Assert.Throws<ArgumentException>(() => new CandidateExecutionResult(Outcome(stop), stop, [Receipt(CandidateAcknowledgement.Unknown)]));
        Assert.Throws<ArgumentException>(() => new CandidateExecutionResult(Outcome(stop), stop,
            [Receipt(CandidateAcknowledgement.Acknowledged, decision, CandidateContinuation.End)]));
        Assert.Throws<ArgumentException>(() => new CandidateExecutionResult(Outcome(stop), stop,
            [last, Receipt(CandidateAcknowledgement.Acknowledged, decision == CandidateDecision.Accept ? CandidateDecision.Reject : CandidateDecision.Accept)]));
    }

    [Theory]
    [InlineData(CandidateStopReason.RepairLimit, CandidateDecision.Reject, 1)]
    [InlineData(CandidateStopReason.RepairLimit, CandidateDecision.Reject, 2)]
    [InlineData(CandidateStopReason.ContinuationLimit, CandidateDecision.Accept, 1)]
    [InlineData(CandidateStopReason.ContinuationLimit, CandidateDecision.Accept, 2)]
    public void LimitStopCannotCountItsDeniedTerminalFollowOnAsAdmitted(CandidateStopReason stop, CandidateDecision decision, int receiptCount)
    {
        var receipts = Enumerable.Range(0, receiptCount).Select(_ => Receipt(CandidateAcknowledgement.Acknowledged, decision)).ToArray();
        Assert.Throws<ArgumentException>(() => Result(receiptCount));
        var valid = Result(receiptCount - 1);
        Assert.Equal(receiptCount - 1, stop == CandidateStopReason.RepairLimit ? valid.RepairsAdmitted : valid.ContinuationsAdmitted);
        Assert.Equal(decision == CandidateDecision.Accept ? receiptCount : 0, valid.AcceptedCount);
        Assert.Equal(0, valid.Outcome.CompletedWorkUnits);

        var otherDecision = decision == CandidateDecision.Accept ? CandidateDecision.Reject : CandidateDecision.Accept;
        var mixed = new CandidateExecutionResult(Outcome(stop), stop,
            [Receipt(CandidateAcknowledgement.Acknowledged, otherDecision), receipts[^1]],
            repairsAdmitted: stop == CandidateStopReason.ContinuationLimit ? 1 : 0,
            continuationsAdmitted: stop == CandidateStopReason.RepairLimit ? 1 : 0);
        Assert.Equal(1, mixed.RepairsAdmitted + mixed.ContinuationsAdmitted);
        Assert.Equal(1, mixed.AcceptedCount);

        CandidateExecutionResult Result(int admitted) => new(Outcome(stop), stop, receipts,
            repairsAdmitted: stop == CandidateStopReason.RepairLimit ? admitted : 0,
            continuationsAdmitted: stop == CandidateStopReason.ContinuationLimit ? admitted : 0);
    }

    [Theory]
    [InlineData(CandidateStopReason.Cancelled)]
    [InlineData(CandidateStopReason.WorkUnitLimit)]
    [InlineData(CandidateStopReason.ProductionFailed)]
    [InlineData(CandidateStopReason.ProgressObserverFailed)]
    public void NeighboringStopsNeedNoInventedNewAcknowledgementAndRetainPriorAcceptance(CandidateStopReason stop)
    {
        var empty = new CandidateExecutionResult(Outcome(stop), stop, []);
        Assert.Empty(empty.Receipts);
        var receipt = Receipt(CandidateAcknowledgement.Acknowledged);
        var result = new CandidateExecutionResult(Outcome(stop), stop, [receipt]);
        Assert.Same(receipt, Assert.Single(result.Receipts));
        Assert.Equal(1, result.AcceptedCount);
    }

    [Theory]
    [InlineData(CandidateStopReason.Cancelled)]
    [InlineData(CandidateStopReason.WorkUnitLimit)]
    public void CancellationOrResourceStopCanRetainUnknownPendingReceiptWithoutClaimingHostRollback(CandidateStopReason stop)
    {
        var result = new CandidateExecutionResult(Outcome(stop), stop,
            [Receipt(CandidateAcknowledgement.Acknowledged), Receipt(CandidateAcknowledgement.Unknown)]);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(CandidateAcknowledgement.Unknown, result.Receipts[^1].Acknowledgement);
    }

    [Fact]
    public void GenericCompletionAndCancellationPoliciesRemainIndependentOfHostDecision()
    {
        var rejected = Receipt(CandidateAcknowledgement.Acknowledged, CandidateDecision.Reject, CandidateContinuation.End);
        var complete = new CandidateExecutionResult(Outcome(CandidateStopReason.Completed), CandidateStopReason.Completed, [rejected]);
        Assert.True(complete.Outcome.IsCompleted);
        Assert.Equal(0, complete.AcceptedCount);
        Assert.Equal(0, complete.Outcome.CompletedWorkUnits);
        var cancelled = new CandidateExecutionResult(Outcome(CandidateStopReason.Cancelled), CandidateStopReason.Cancelled, [rejected]);
        Assert.Equal(CandidateDecision.Reject, cancelled.Receipts[0].Decision);
        Assert.Equal(0, new CandidateExecutionResult(Outcome(CandidateStopReason.Completed), CandidateStopReason.Completed, []).AcceptedCount);
        var submissionBound = new CandidateExecutionResult(Outcome(CandidateStopReason.SubmissionLimit), CandidateStopReason.SubmissionLimit,
            [Receipt(CandidateAcknowledgement.Acknowledged)]);
        Assert.Equal(1, submissionBound.AcceptedCount);
    }

    private static CandidateReceipt Receipt(CandidateAcknowledgement state, CandidateDecision decision = CandidateDecision.Accept, CandidateContinuation continuation = CandidateContinuation.Continue) =>
        new(ExecutionId, Guid.NewGuid(), state, state == CandidateAcknowledgement.Acknowledged ? decision : null,
            state == CandidateAcknowledgement.Acknowledged ? continuation : null);

    private static AgentOutcome Outcome(CandidateStopReason stop) => stop switch
    {
        CandidateStopReason.MissingAcknowledgement or CandidateStopReason.UnknownAcknowledgement or CandidateStopReason.HostEnded or CandidateStopReason.ProductionExhausted => new(ExecutionId, AgentTerminationReason.Partial, 0),
        CandidateStopReason.RepairLimit or CandidateStopReason.ContinuationLimit or CandidateStopReason.SubmissionLimit or CandidateStopReason.WorkUnitLimit => new(ExecutionId, AgentTerminationReason.ResourceLimit, 0),
        CandidateStopReason.Completed => new(ExecutionId, AgentTerminationReason.Completed, 0),
        CandidateStopReason.Cancelled => new(ExecutionId, AgentTerminationReason.Cancelled, 0),
        CandidateStopReason.ProgressObserverFailed => new(ExecutionId, AgentTerminationReason.Failed, 0, failureCode: AgentFailureCode.ProgressObserverFailed),
        _ => new(ExecutionId, AgentTerminationReason.Failed, 0, failureCode: AgentFailureCode.ExecutionFailed),
    };
}

using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using Xunit;

namespace SolusAgent.ContractTests.Candidates;

public sealed class HistoricalCandidateResultTests
{
    [Fact]
    public void HistoricalReceiptsPreserveOriginAndAreDefensivelyCopied()
    {
        var old = Guid.NewGuid(); var current = Guid.NewGuid(); var id = Guid.NewGuid();
        var receipt = new CandidateReceipt(old, id, CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept, CandidateContinuation.Continue);
        var history = new List<CandidateReceipt> { receipt };
        var outcome = new AgentOutcome(current, AgentTerminationReason.Completed, 0);
        var candidates = new CandidateExecutionResult(outcome, CandidateStopReason.Completed, [], continuationsAdmitted: 1, historicalReceipts: history);
        history.Clear(); Assert.Same(receipt, Assert.Single(candidates.HistoricalReceipts)); Assert.Equal(old, candidates.HistoricalReceipts[0].ExecutionId);
        Assert.Equal(1, candidates.AcceptedCount); Assert.Empty(candidates.Receipts);
        var context = new ContextExecutionResult(current, ContextExecutionIntent.ContinueRun, ContextAdmission.Supplied, outcome,
            history: [new(Guid.NewGuid(), Guid.NewGuid(), new(old, UsageInventoryCoverage.Complete, []), AgentTerminationReason.Partial, 1)]);
        Assert.Same(candidates, new ContextCandidateExecutionResult(context, candidates).Candidates);
        Assert.Throws<ArgumentException>(() => new ContextCandidateExecutionResult(
            new(current, ContextExecutionIntent.ContinueRun, ContextAdmission.Supplied, outcome), candidates));
    }
    [Fact]
    public void SameCurrentOriginAndDuplicateSubmissionCannotBecomeHistorical()
    {
        var old = Guid.NewGuid(); var current = Guid.NewGuid(); var id = Guid.NewGuid();
        var outcome = new AgentOutcome(current, AgentTerminationReason.Completed, 0);
        var now = new CandidateReceipt(current, id, CandidateAcknowledgement.Unknown);
        var prior = new CandidateReceipt(old, id, CandidateAcknowledgement.Unknown);
        Assert.Throws<ArgumentException>(() => new CandidateExecutionResult(outcome, CandidateStopReason.Completed, [], historicalReceipts: [now]));
        Assert.Throws<ArgumentException>(() => new CandidateExecutionResult(outcome, CandidateStopReason.Completed, [now], historicalReceipts: [prior]));
        Assert.Throws<ArgumentException>(() => new CandidateExecutionResult(outcome, CandidateStopReason.Completed, [], historicalReceipts: [null!]));
        var rejected = new ContextExecutionResult(current, ContextExecutionIntent.ContinueRun, ContextAdmission.Rejected,
            rejectionCode: ContextRejectionCode.InvalidContext);
        Assert.Null(new ContextCandidateExecutionResult(rejected).Candidates);
        Assert.Throws<ArgumentException>(() => new ContextCandidateExecutionResult(rejected, new(outcome, CandidateStopReason.Completed, [])));
    }
}

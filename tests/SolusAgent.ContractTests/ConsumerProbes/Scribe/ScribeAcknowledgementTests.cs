using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.ConsumerProbes.ScribeHost;
using Xunit;

namespace SolusAgent.ContractTests.ConsumerProbes.Scribe;

/// <summary>AC2: independently correlated candidates with accept/reject feedback, bounded repair and uncertain delivery.</summary>
public sealed class ScribeAcknowledgementTests
{
    [Fact]
    public async Task AcceptedFirstRejectedSecondWithRemainingWorkStopsAtHostEndWithPartialProgress()
    {
        var host = ScribeFixtures.CreateHost(
        [
            new ScribeExchangePlan(true, ScribeDelivery.Delivered, CandidateContinuation.Continue),
            new ScribeExchangePlan(false, ScribeDelivery.Delivered, CandidateContinuation.End),
        ]);
        var round = new ScribeRound(host,
        [
            new ScribeProductionStep("intro", "INTRO_FACT_CANARY accepted first"),
            new ScribeProductionStep("usage", "USAGE_FACT_CANARY rejected second"),
            new ScribeProductionStep("limits", "LIMITS_FACT_CANARY never produced"),
        ]);
        var observed = await round.RunAsync();

        Assert.Equal(CandidateStopReason.HostEnded, observed.Result.StopReason);
        Assert.Equal(AgentTerminationReason.Partial, observed.Result.Outcome.Reason);
        Assert.Equal(2, round.Agent.TotalProductionStarted);
        Assert.Equal(new[] { "intro", "usage" }, round.ProducedMembers);
        Assert.Equal(2, observed.Result.Receipts.Count);
        Assert.Equal(2, observed.Result.Receipts.Select(receipt => receipt.SubmissionId).Distinct().Count());
        Assert.All(observed.Result.Receipts, receipt => Assert.Equal(observed.Result.Outcome.ExecutionId, receipt.ExecutionId));
        Assert.Equal(1, observed.Result.AcceptedCount);

        // Host progress retains only the accepted first candidate while scripted work remains.
        Assert.Equal(new[] { "intro" }, host.Progress.AcceptedFacts.Select(fact => fact.Member).ToArray());
        Assert.Equal(new[] { "usage", "limits" }, host.Progress.UnresolvedMembers);
        Assert.False(host.Progress.IsComplete);
        Assert.Equal(2, host.Exchanges.Count);
        Assert.Equal(0, host.ExternalEffects);
    }

    [Fact]
    public async Task RepairExhaustedPartialProgressRetainsAcceptedWorkAndDeniesTheNextRepair()
    {
        var host = ScribeFixtures.CreateHost(
        [
            new ScribeExchangePlan(true, ScribeDelivery.Delivered, CandidateContinuation.Continue),
            new ScribeExchangePlan(false, ScribeDelivery.Delivered, CandidateContinuation.Continue, "USAGE_FACT_CANARY repair requested by Host"),
            new ScribeExchangePlan(false, ScribeDelivery.Delivered, CandidateContinuation.Continue, "USAGE_FACT_CANARY second repair request"),
        ], maximumSubmissions: 4);
        var round = new ScribeRound(host,
        [
            new ScribeProductionStep("intro", "INTRO_FACT_CANARY accepted first"),
            new ScribeProductionStep("usage", "USAGE_FACT_CANARY rejected second"),
            new ScribeProductionStep("usage", "USAGE_FACT_CANARY planned repair text never used"),
            new ScribeProductionStep("limits", "LIMITS_FACT_CANARY denied repair never produced"),
        ]);
        var observed = await round.RunAsync();

        Assert.Equal(CandidateStopReason.RepairLimit, observed.Result.StopReason);
        Assert.Equal(AgentTerminationReason.ResourceLimit, observed.Result.Outcome.Reason);
        Assert.Equal(3, round.Agent.TotalProductionStarted);
        Assert.Equal(1, observed.Result.RepairsAdmitted);
        Assert.Equal(3, observed.Result.Receipts.Count);
        Assert.Equal(3, observed.Result.Receipts.Select(receipt => receipt.SubmissionId).Distinct().Count());

        // The admitted fresh repair points at the rejected second submission, with its own provider/tool work.
        var exchanges = host.Exchanges;
        Assert.Null(exchanges[0].RepairsSubmissionId);
        Assert.Null(exchanges[1].RepairsSubmissionId);
        Assert.Equal(exchanges[1].SubmissionId, exchanges[2].RepairsSubmissionId);
        Assert.NotEqual(exchanges[0].SubmissionId, exchanges[2].RepairsSubmissionId);
        Assert.Equal(3, round.Runtime.Productions.Count);
        Assert.Equal(3, round.Runtime.Productions.Select(record => record.Exchange.Attempt.PhysicalAttemptId).Distinct().Count());
        Assert.Equal(3, round.Runtime.Capability.Effects);

        // The repair content came from the Host correction consumed as data, not from the pre-scripted step text.
        var repairArguments = round.Runtime.Productions[2].ToolResults[0].Call.ArgumentsJson;
        Assert.Contains("USAGE_FACT_CANARY repair requested by Host", repairArguments, StringComparison.Ordinal);
        Assert.DoesNotContain("USAGE_FACT_CANARY planned repair text never used", repairArguments, StringComparison.Ordinal);

        // Progress is partial independently of the outer terminal enum; accepted work survives the exhausted repair.
        Assert.Equal(new[] { "intro" }, host.Progress.AcceptedFacts.Select(fact => fact.Member).ToArray());
        Assert.Equal(new[] { "usage", "limits" }, host.Progress.UnresolvedMembers);
        Assert.False(host.Progress.IsComplete);
        Assert.Equal(0, host.ExternalEffects);
    }

    [Fact]
    public async Task RepairedCandidateAcceptedEndVerifiesCorrectionWithoutAdditionalProduction()
    {
        var host = ScribeFixtures.CreateHost(
        [
            new ScribeExchangePlan(true, ScribeDelivery.Delivered, CandidateContinuation.Continue),
            new ScribeExchangePlan(false, ScribeDelivery.Delivered, CandidateContinuation.Continue, "USAGE_FACT_CANARY concrete corrected fact"),
            new ScribeExchangePlan(true, ScribeDelivery.Delivered, CandidateContinuation.End),
        ], members: ["intro", "usage"]);
        var round = new ScribeRound(host,
        [
            new ScribeProductionStep("intro", "INTRO_FACT_CANARY accepted first"),
            new ScribeProductionStep("usage", "USAGE_FACT_CANARY rejected second"),
            new ScribeProductionStep("usage", "USAGE_FACT_CANARY planned repair text never used"),
        ]);
        var observed = await round.RunAsync();

        Assert.Equal(CandidateStopReason.Completed, observed.Result.StopReason);
        Assert.Equal(1, observed.Result.RepairsAdmitted);
        Assert.Equal(3, round.Agent.TotalProductionStarted);
        // The single continuation admission produced the rejected second candidate; the accepted repair's End
        // admitted no additional production.
        Assert.Equal(1, observed.Result.ContinuationsAdmitted);
        var exchanges = host.Exchanges;
        Assert.Equal(3, exchanges.Count);
        Assert.Equal(exchanges[1].SubmissionId, exchanges[2].RepairsSubmissionId);

        // The concrete correction succeeded causally: the accepted usage fact is the correction-derived text,
        // not the pre-scripted repair step text.
        var usageFact = Assert.Single(host.Progress.AcceptedFacts, fact => fact.Member == "usage");
        Assert.Equal("USAGE_FACT_CANARY concrete corrected fact", usageFact.Text);
        Assert.NotEqual("USAGE_FACT_CANARY planned repair text never used", usageFact.Text);
        Assert.True(host.Progress.IsComplete);
        Assert.Equal(0, host.ExternalEffects);
    }

    [Theory]
    [InlineData("USAGE_FACT_CANARY repair alpha requested by Host")]
    [InlineData("USAGE_FACT_CANARY repair beta requested by Host")]
    public async Task ChangedCorrectionDataChangesRepairContent(string requestedFact)
    {
        var host = ScribeFixtures.CreateHost(
        [
            new ScribeExchangePlan(true, ScribeDelivery.Delivered, CandidateContinuation.Continue),
            new ScribeExchangePlan(false, ScribeDelivery.Delivered, CandidateContinuation.Continue, requestedFact),
            new ScribeExchangePlan(true, ScribeDelivery.Delivered, CandidateContinuation.End),
        ], members: ["intro", "usage"]);
        var round = new ScribeRound(host,
        [
            new ScribeProductionStep("intro", "INTRO_FACT_CANARY accepted first"),
            new ScribeProductionStep("usage", "USAGE_FACT_CANARY rejected second"),
            new ScribeProductionStep("usage", "USAGE_FACT_CANARY planned repair text never used"),
        ]);
        var observed = await round.RunAsync();

        Assert.Equal(CandidateStopReason.Completed, observed.Result.StopReason);

        // Metamorphic pair: the repair content follows the changed correction, never the planned step text.
        var usageFact = Assert.Single(host.Progress.AcceptedFacts, fact => fact.Member == "usage");
        Assert.Equal(requestedFact, usageFact.Text);
        Assert.NotEqual("USAGE_FACT_CANARY planned repair text never used", usageFact.Text);
        var repairArguments = round.Runtime.Productions[2].ToolResults[0].Call.ArgumentsJson;
        Assert.Contains(requestedFact, repairArguments, StringComparison.Ordinal);
        Assert.DoesNotContain("USAGE_FACT_CANARY planned repair text never used", repairArguments, StringComparison.Ordinal);

        // Correction-derived text stays untrusted data: current Host control and installed bindings are unchanged.
        Assert.Equal(ScribeFixtures.TrustedInstructions, host.Control.Instructions);
        Assert.Same(round.Runtime.Binding, Assert.Single(round.Runtime.Configuration.Tools));
    }

    [Fact]
    public async Task MissingOrBrokenCorrectionDataCannotFabricateRepair()
    {
        // Parser boundary: only the exact grammar with genuine marker and nonempty requested fact is usable.
        Assert.False(ScribeCandidateCorrection.TryParseRequestedFact(null, out _));
        Assert.False(ScribeCandidateCorrection.TryParseRequestedFact("synthetic rejection (domain-policy): text", out _));
        Assert.False(ScribeCandidateCorrection.TryParseRequestedFact(ScribeCandidateCorrection.Format(null), out _));
        Assert.True(ScribeCandidateCorrection.TryParseRequestedFact(ScribeCandidateCorrection.Format("fact text"), out var parsed));
        Assert.Equal("fact text", parsed);

        // Round boundary: a rejection whose correction carries no repair data cannot produce a repair candidate.
        var host = ScribeFixtures.CreateHost(
        [
            new ScribeExchangePlan(true, ScribeDelivery.Delivered, CandidateContinuation.Continue),
            new ScribeExchangePlan(false, ScribeDelivery.Delivered, CandidateContinuation.Continue),
            new ScribeExchangePlan(false, ScribeDelivery.Delivered, CandidateContinuation.Continue),
        ], maximumSubmissions: 4);
        var round = new ScribeRound(host,
        [
            new ScribeProductionStep("intro", "INTRO_FACT_CANARY accepted first"),
            new ScribeProductionStep("usage", "USAGE_FACT_CANARY rejected second"),
            new ScribeProductionStep("usage", "USAGE_FACT_CANARY planned repair text never used"),
            new ScribeProductionStep("limits", "LIMITS_FACT_CANARY never produced"),
        ]);
        var observed = await round.RunAsync();

        Assert.Equal(CandidateStopReason.ProductionFailed, observed.Result.StopReason);
        Assert.Equal(AgentTerminationReason.Failed, observed.Result.Outcome.Reason);
        Assert.Equal(2, observed.Result.Receipts.Count);
        Assert.Equal(1, observed.Result.AcceptedCount);
        Assert.Equal(2, host.Exchanges.Count);
        Assert.Equal(2, round.Runtime.Productions.Count);
        Assert.Equal(2, round.Runtime.Capability.Effects);
        Assert.Equal(new[] { "intro" }, host.Progress.AcceptedFacts.Select(fact => fact.Member).ToArray());
        Assert.Equal(0, host.ExternalEffects);
    }

    [Fact]
    public async Task MissingFeedbackAfterCommitKeepsAcceptedProgressWithoutReplay()
    {
        var host = ScribeFixtures.CreateHost(
        [
            new ScribeExchangePlan(true, ScribeDelivery.Delivered, CandidateContinuation.Continue),
            new ScribeExchangePlan(true, ScribeDelivery.Missing),
        ]);
        var round = new ScribeRound(host,
        [
            new ScribeProductionStep("intro", "INTRO_FACT_CANARY accepted first"),
            new ScribeProductionStep("usage", "USAGE_FACT_CANARY committed before missing delivery"),
        ]);
        var observed = await round.RunAsync();

        Assert.Equal(CandidateStopReason.MissingAcknowledgement, observed.Result.StopReason);
        Assert.Equal(AgentTerminationReason.Partial, observed.Result.Outcome.Reason);
        Assert.Equal(2, observed.Result.Receipts.Count);
        Assert.Equal(CandidateAcknowledgement.Missing, observed.Result.Receipts[1].Acknowledgement);
        Assert.Equal(1, observed.Result.AcceptedCount);

        // Host validation and commit preceded delivery: progress contains the second fact although receipts do not.
        Assert.Equal(new[] { "intro", "usage" }, host.Progress.AcceptedFacts.Select(fact => fact.Member).ToArray());
        Assert.Equal(2, round.Agent.TotalProductionStarted);
        Assert.Equal(2, host.Exchanges.Count);
        Assert.Equal(0, host.ExternalEffects);
    }

    [Fact]
    public async Task UnknownFeedbackAfterCommitKeepsAcceptedProgressWithoutReplay()
    {
        var host = ScribeFixtures.CreateHost(
        [
            new ScribeExchangePlan(true, ScribeDelivery.Delivered, CandidateContinuation.Continue),
            new ScribeExchangePlan(true, ScribeDelivery.Unknown),
        ]);
        var round = new ScribeRound(host,
        [
            new ScribeProductionStep("intro", "INTRO_FACT_CANARY accepted first"),
            new ScribeProductionStep("usage", "USAGE_FACT_CANARY committed before unknown delivery"),
        ]);
        var observed = await round.RunAsync();

        Assert.Equal(CandidateStopReason.UnknownAcknowledgement, observed.Result.StopReason);
        Assert.Equal(AgentTerminationReason.Partial, observed.Result.Outcome.Reason);
        Assert.Equal(CandidateAcknowledgement.Unknown, observed.Result.Receipts[1].Acknowledgement);
        Assert.Equal(1, observed.Result.AcceptedCount);

        // Unknown delivery may follow Host effects and cannot cause automatic replay or rollback inference.
        Assert.Equal(new[] { "intro", "usage" }, host.Progress.AcceptedFacts.Select(fact => fact.Member).ToArray());
        Assert.Equal(2, round.Agent.TotalProductionStarted);
        Assert.Equal(2, host.Exchanges.Count);
        Assert.Equal(0, host.ExternalEffects);
    }

    [Fact]
    public async Task CancelledObservationOfHeldAcknowledgementRetainsProgressAndRejectsLateDelivery()
    {
        var host = ScribeFixtures.CreateHost(
        [
            new ScribeExchangePlan(true, ScribeDelivery.Delivered, CandidateContinuation.Continue),
            new ScribeExchangePlan(true, ScribeDelivery.Held),
        ]);
        var round = new ScribeRound(host,
        [
            new ScribeProductionStep("intro", "INTRO_FACT_CANARY accepted first"),
            new ScribeProductionStep("usage", "USAGE_FACT_CANARY committed before held delivery"),
        ]);

        using var cancellation = new CancellationTokenSource();
        var running = round.RunAsync(cancellation.Token).AsTask();
        await host.DeliveryHeld;
        cancellation.Cancel();
        var observed = await running;

        Assert.Equal(CandidateStopReason.Cancelled, observed.Result.StopReason);
        Assert.Equal(AgentTerminationReason.Cancelled, observed.Result.Outcome.Reason);
        Assert.Equal(CandidateAcknowledgement.Unknown, observed.Result.Receipts[1].Acknowledgement);
        Assert.Equal(new[] { "intro", "usage" }, host.Progress.AcceptedFacts.Select(fact => fact.Member).ToArray());

        // Completing the held callback afterwards cannot rewrite the immutable result or admit late work.
        var before = observed.Result;
        host.ReleaseHeldDelivery();
        Assert.Same(before, observed.Result);
        Assert.Equal(2, observed.Result.Receipts.Count);
        Assert.Equal(2, observed.Result.Receipts.Select(receipt => receipt.SubmissionId).Distinct().Count());
        Assert.Equal(2, round.Agent.TotalProductionStarted);
        Assert.Equal(2, host.Exchanges.Count);
        Assert.Equal(ScribeDelivery.Held, host.Exchanges[1].Delivery);
        Assert.Equal(0, host.ExternalEffects);
    }
}

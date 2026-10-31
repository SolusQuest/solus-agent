using Solus.Telemetry.Contracts;
using SolusAgent.Api.Usage;
using SolusAgent.Telemetry;
using Xunit;
using static SolusAgent.ContractTests.Telemetry.TelemetryFixtures;

namespace SolusAgent.ContractTests.Telemetry;

public sealed class TelemetryAccountingTests
{
    [Theory]
    [InlineData(UsageSettlement.Settled, null, UsageInventoryCoverage.Complete, TelemetrySettlement.Unknown)]
    [InlineData(UsageSettlement.Settled, UsageSettlement.Unknown, UsageInventoryCoverage.Complete, TelemetrySettlement.Mixed)]
    [InlineData(UsageSettlement.Settled, UsageSettlement.Unsettled, UsageInventoryCoverage.Complete, TelemetrySettlement.Mixed)]
    [InlineData(UsageSettlement.Settled, UsageSettlement.Settled, UsageInventoryCoverage.Complete, TelemetrySettlement.Settled)]
    [InlineData(UsageSettlement.Unsettled, UsageSettlement.Unsettled, UsageInventoryCoverage.Complete, TelemetrySettlement.Unsettled)]
    [InlineData(UsageSettlement.Unknown, null, UsageInventoryCoverage.Complete, TelemetrySettlement.Unknown)]
    [InlineData(null, null, UsageInventoryCoverage.Complete, null)]
    [InlineData(UsageSettlement.Settled, UsageSettlement.Settled, UsageInventoryCoverage.Partial, TelemetrySettlement.Unknown)]
    [InlineData(UsageSettlement.Settled, UsageSettlement.Unknown, UsageInventoryCoverage.Partial, TelemetrySettlement.Mixed)]
    public void SettlementDistinguishesMissingClaimsFromExplicitUnknown(UsageSettlement? first, UsageSettlement? second,
        UsageInventoryCoverage coverage, TelemetrySettlement? expected)
    {
        UsageAccounting? Claim(UsageSettlement? settlement) => settlement.HasValue ? new(settlement.Value) : null;
        var run = RoundTrip(Claims(coverage, Claim(first), Claim(second))).Usage.Runs[0];
        Assert.Equal(expected, run.ProviderSettlement);
    }

    [Fact]
    public void SparseClaimsAreUnknownSubtotalsAndAbsentClaimsDoNotBecomeZero()
    {
        var claim = new UsageAccounting(UsageSettlement.Unsettled, new(5, 0), new(9, 2));
        var run = RoundTrip(Claims(UsageInventoryCoverage.Complete, claim, null)).Usage.Runs[0];
        Assert.Equal(TelemetryMeasurementCoverage.Unavailable, run.InputTokens.Coverage);
        var reservation = Assert.Single(run.Accounting, a => a.Kind == TelemetryAccountingKind.Reservation);
        Assert.Equal(5L, reservation.Input.Value); Assert.Equal(TelemetryAccountingKnowledge.Unknown, reservation.Input.Knowledge);
        Assert.Equal(0L, reservation.Output.Value); Assert.Equal(TelemetryAccountingKnowledge.Unknown, reservation.Output.Knowledge);
        var charge = Assert.Single(run.Accounting, a => a.Kind == TelemetryAccountingKind.ConservativeCharge);
        Assert.Equal(9L, charge.Input.Value); Assert.Equal(TelemetryAccountingSource.ProviderClaim, charge.Source);
        Assert.Empty(RoundTrip(Claims(UsageInventoryCoverage.Complete, null, new UsageAccounting())).Usage.Runs[0].Accounting);
        run = RoundTrip(Claims(UsageInventoryCoverage.Partial, claim)).Usage.Runs[0];
        Assert.All(run.Accounting, a => Assert.Equal(TelemetryAccountingKnowledge.Unknown, a.Input.Knowledge));
        run = RoundTrip(Claims(UsageInventoryCoverage.Complete, claim)).Usage.Runs[0];
        Assert.All(run.Accounting, a => Assert.Equal(TelemetryAccountingKnowledge.Provisional, a.Input.Knowledge));
    }

    [Fact]
    public void ClaimOverflowIsIndependentPerAxisAndDoesNotInventMeasurements()
    {
        var run = RoundTrip(Claims(UsageInventoryCoverage.Complete,
            new UsageAccounting(inFlightReservation: new(long.MaxValue, 0)),
            new UsageAccounting(inFlightReservation: new(1, 0)))).Usage.Runs[0];
        var amount = Assert.Single(run.Accounting);
        Assert.Null(amount.Input.Value); Assert.Equal(TelemetryAccountingKnowledge.Overflow, amount.Input.Knowledge);
        Assert.Equal(0L, amount.Output.Value); Assert.Equal(TelemetryAccountingKnowledge.Provisional, amount.Output.Knowledge);
        Assert.Null(run.InputTokens.Value); Assert.Equal(TelemetryMeasurementCoverage.Unavailable, run.InputTokens.Coverage);
    }

    [Fact]
    public void ProviderClaimsAndFinalizedRunLedgerKeepDistinctAuthoritiesAndSignedRemainders()
    {
        var id = Guid.NewGuid(); var policy = new AgentAccountingPolicy(new(5, 7), 3, 5, UnknownUsagePolicy.ConservativeCharge);
        var observed = Attempt(id, new(8), new(UsageSettlement.Unsettled, new(999, 999), new(999, 999)));
        var released = Attempt(id, new(), exposure: DispatchExposure.NotDispatched);
        var ledger = new RunAccountingSnapshot(id, policy,
            [Entry(observed, policy, new(AccountingDisposition.Measured, 8), new(AccountingDisposition.ConservativeCharge, 7)),
             Entry(released, policy, new(AccountingDisposition.Released, 0), new(AccountingDisposition.Released, 0))]);
        var run = RoundTrip(new(id, UsageInventoryCoverage.Complete, [observed, released], accounting: ledger)).Usage.Runs[0];
        Assert.Equal(8L, run.InputTokens.Value); Assert.Equal(TelemetryMeasurementCoverage.Complete, run.InputTokens.Coverage);
        Assert.Equal(0L, run.OutputTokens.Value); Assert.Equal(TelemetryMeasurementCoverage.Partial, run.OutputTokens.Coverage); // no-dispatch contributes a known zero subtotal
        var measured = Find(run, TelemetryAccountingKind.Measured); Assert.Equal(8L, measured.Input.Value);
        var conservative = Find(run, TelemetryAccountingKind.ConservativeCharge); Assert.Equal(7L, conservative.Output.Value);
        Assert.Equal(TelemetryAccountingKnowledge.Provisional, conservative.Output.Knowledge);
        var remaining = Find(run, TelemetryAccountingKind.RemainingAllowance);
        Assert.Equal(-5L, remaining.Input.Value); Assert.Equal(-2L, remaining.Output.Value);
        Assert.Equal(TelemetryAccountingKnowledge.Known, remaining.Input.Knowledge);
        Assert.Equal(TelemetryAccountingKnowledge.Provisional, remaining.Output.Knowledge);
        Assert.Equal(999L, Assert.Single(run.Accounting, a => a.Source == TelemetryAccountingSource.ProviderClaim && a.Kind == TelemetryAccountingKind.ConservativeCharge).Input.Value);
        Assert.Equal(0L, Find(run, TelemetryAccountingKind.Reservation).Input.Value);
        Assert.Equal(TelemetryAccountingKnowledge.Known, Find(run, TelemetryAccountingKind.Released).Input.Knowledge);
    }

    [Fact]
    public void ZeroUnresolvedEstimateRemainsUnknownAndMissingAllowanceRemainsUnavailable()
    {
        var id = Guid.NewGuid(); var policy = new AgentAccountingPolicy(new(0, 0), inputAllowance: 1, unknownUsage: UnknownUsagePolicy.ContinueUnknown);
        var attempt = Attempt(id, new());
        var ledger = new RunAccountingSnapshot(id, policy, [Entry(attempt, policy,
            new(AccountingDisposition.Unresolved, 0), new(AccountingDisposition.Unresolved, 0))]);
        var run = RoundTrip(new(id, UsageInventoryCoverage.Complete, [attempt], accounting: ledger)).Usage.Runs[0];
        var unresolved = Find(run, TelemetryAccountingKind.Unresolved);
        Assert.Equal(0L, unresolved.Input.Value); Assert.Equal(TelemetryAccountingKnowledge.Unknown, unresolved.Input.Knowledge);
        var remaining = Find(run, TelemetryAccountingKind.RemainingAllowance);
        Assert.Equal(1L, remaining.Input.Value); Assert.Equal(TelemetryAccountingKnowledge.Unknown, remaining.Input.Knowledge);
        Assert.Null(remaining.Output.Value); Assert.Equal(TelemetryAccountingKnowledge.Unavailable, remaining.Output.Knowledge);
        Assert.Null(run.InputTokens.Value);
    }

    [Fact]
    public void LedgerOverflowDoesNotSaturateOrCreateAFalseRemainingAllowance()
    {
        var id = Guid.NewGuid(); var policy = new AgentAccountingPolicy(new(0, 0), 1);
        var a = Attempt(id, new(long.MaxValue, 0)); var b = Attempt(id, new(1, 0));
        var ledger = new RunAccountingSnapshot(id, policy,
            [Entry(a, policy, new(AccountingDisposition.Measured, long.MaxValue), new(AccountingDisposition.Measured, 0)),
             Entry(b, policy, new(AccountingDisposition.Measured, 1), new(AccountingDisposition.Measured, 0))]);
        var run = RoundTrip(new(id, UsageInventoryCoverage.Complete, [a, b], accounting: ledger)).Usage.Runs[0];
        Assert.Equal(TelemetryMeasurementCoverage.Overflow, run.InputTokens.Coverage);
        Assert.Null(Find(run, TelemetryAccountingKind.Measured).Input.Value);
        Assert.Equal(TelemetryAccountingKnowledge.Overflow, Find(run, TelemetryAccountingKind.RemainingAllowance).Input.Knowledge);
        Assert.Equal(1L, Find(run, TelemetryAccountingKind.Allowance).Input.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CostAggregationRejectsBothNumericOverflowAndSilentPrecisionLoss(bool fractional)
    {
        var costs = RoundTrip(Claims(UsageInventoryCoverage.Complete,
            Cost(decimal.MaxValue), Cost(fractional ? 0.1m : 1m))).Usage.Runs[0].EstimatedCosts;
        var cost = Assert.Single(costs); Assert.Null(cost.Amount); Assert.Equal(TelemetryAccountingKnowledge.Overflow, cost.Knowledge);
    }

    [Fact]
    public void ExactCurrencyGroupsRetainScalePrecisionAndSparseKnowledge()
    {
        var run = RoundTrip(Claims(UsageInventoryCoverage.Complete, Cost(0.1m), Cost(0.02m))).Usage.Runs[0];
        var cost = Assert.Single(run.EstimatedCosts); Assert.Equal(0.12m, cost.Amount); Assert.Equal(TelemetryAccountingKnowledge.Provisional, cost.Knowledge);
        run = RoundTrip(Claims(UsageInventoryCoverage.Complete, Cost(9007199254740993.123456789m), Cost(0m), Cost(2m, "EUR"), null)).Usage.Runs[0];
        Assert.Equal(2, run.EstimatedCosts.Count);
        Assert.Equal(9007199254740993.123456789m, Assert.Single(run.EstimatedCosts, c => c.Currency == "USD").Amount);
        Assert.All(run.EstimatedCosts, c => Assert.Equal(TelemetryAccountingKnowledge.Unknown, c.Knowledge));
        Assert.Equal(0m, Assert.Single(RoundTrip(Claims(UsageInventoryCoverage.Complete, Cost(0m))).Usage.Runs[0].EstimatedCosts).Amount);
    }

    [Fact]
    public void ExcessCurrenciesRejectTheWholeSummaryInsteadOfDroppingEstimates()
    {
        var claims = Enumerable.Range(0, 17).Select(i => Cost(1, "AA" + (char)('A' + i))).ToArray();
        Assert.Equal(16, AgentTelemetryMapper.MapRun(Claims(UsageInventoryCoverage.Complete, claims[..16])).EstimatedCosts.Count);
        Assert.Throws<ArgumentException>(() => AgentTelemetryMapper.MapRun(Claims(UsageInventoryCoverage.Complete, claims)));
    }

    private static UsageAccounting Cost(decimal value, string currency = "USD") => new(estimatedCost: new(value, currency));
    private static AgentRunUsage Claims(UsageInventoryCoverage coverage, params UsageAccounting?[] claims)
    {
        var id = Guid.NewGuid(); return new(id, coverage, claims.Select(c => Attempt(id, new(), c)).ToArray());
    }
    private static AttemptAccounting Entry(UsageAttemptObservation a, AgentAccountingPolicy policy, AccountingDimension input, AccountingDimension output) =>
        new(a.ExecutionId, a.LogicalCallId, a.PhysicalAttemptId, a.AttemptNumber, policy.Reservation, input, output);
    private static TelemetryAccountingObservation Find(TelemetryRunSummary run, TelemetryAccountingKind kind) =>
        Assert.Single(run.Accounting, a => a.Source == TelemetryAccountingSource.RunLedger && a.Kind == kind);
}

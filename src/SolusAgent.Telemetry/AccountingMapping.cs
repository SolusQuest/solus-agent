using System.Numerics;
using Solus.Telemetry.Contracts;
using SolusAgent.Api.Usage;

namespace SolusAgent.Telemetry;

internal static class AccountingMapping
{
    internal static long? Sum(IEnumerable<long> values)
    {
        long sum = 0;
        foreach (var value in values)
        {
            try { sum = checked(sum + value); }
            catch (OverflowException) { return null; }
        }
        return sum;
    }

    internal static IReadOnlyList<TelemetryAccountingObservation> Map(AgentRunUsage usage)
    {
        var result = new List<TelemetryAccountingObservation>();
        AddClaim(TelemetryAccountingKind.Reservation, c => c?.InFlightReservation);
        AddClaim(TelemetryAccountingKind.ConservativeCharge, c => c?.ConservativeUnobservedCharge);
        if (usage.Accounting is { } ledger)
        {
            AddBucket(TelemetryAccountingKind.Measured, AccountingDisposition.Measured, d => d.MeasuredTokens);
            AddBucket(TelemetryAccountingKind.Reservation, AccountingDisposition.Reserved, d => d.ReservedTokens);
            AddBucket(TelemetryAccountingKind.ConservativeCharge, AccountingDisposition.ConservativeCharge, d => d.ConservativeChargeTokens);
            AddBucket(TelemetryAccountingKind.Unresolved, AccountingDisposition.Unresolved, d => d.UnresolvedTokens);
            result.Add(new(TelemetryAccountingSource.RunLedger, TelemetryAccountingKind.Released,
                new(0, TelemetryAccountingKnowledge.Known), new(0, TelemetryAccountingKnowledge.Known)));
            result.Add(new(TelemetryAccountingSource.RunLedger, TelemetryAccountingKind.Allowance,
                Allowance(ledger.Input.Allowance), Allowance(ledger.Output.Allowance)));
            result.Add(new(TelemetryAccountingSource.RunLedger, TelemetryAccountingKind.RemainingAllowance,
                Remaining(ledger.Input), Remaining(ledger.Output)));

            void AddBucket(TelemetryAccountingKind kind, AccountingDisposition disposition, Func<RunAccountingDimension, long?> read)
            {
                result.Add(new(TelemetryAccountingSource.RunLedger, kind,
                    Bucket(read(ledger.Input), ledger.Attempts.Any(a => a.Input.Disposition == disposition), disposition),
                    Bucket(read(ledger.Output), ledger.Attempts.Any(a => a.Output.Disposition == disposition), disposition)));
            }
        }
        return result;

        void AddClaim(TelemetryAccountingKind kind, Func<UsageAccounting?, UsageTokenAmounts?> read)
        {
            var values = usage.Attempts.Select(a => read(a.Accounting)).ToArray();
            if (values.All(v => v is null)) return;
            var complete = usage.Coverage == UsageInventoryCoverage.Complete && values.All(v => v is not null);
            var supplied = values.Where(v => v is not null).ToArray();
            result.Add(new(TelemetryAccountingSource.ProviderClaim, kind,
                ClaimAmount(Sum(supplied.Select(v => v!.InputTokens)), complete),
                ClaimAmount(Sum(supplied.Select(v => v!.OutputTokens)), complete)));
        }
    }

    internal static TelemetrySettlement? Settlement(AgentRunUsage usage)
    {
        var explicitStates = usage.Attempts.Where(a => a.Accounting is not null)
            .Select(a => a.Accounting!.Settlement).Distinct().ToArray();
        if (explicitStates.Length == 0) return null;
        if (explicitStates.Length > 1) return TelemetrySettlement.Mixed;
        if (usage.Coverage != UsageInventoryCoverage.Complete || usage.Attempts.Any(a => a.Accounting is null))
            return TelemetrySettlement.Unknown;
        return explicitStates[0] switch
        {
            UsageSettlement.Settled => TelemetrySettlement.Settled,
            UsageSettlement.Unsettled => TelemetrySettlement.Unsettled,
            UsageSettlement.Unknown => TelemetrySettlement.Unknown,
            _ => throw new ArgumentException("Unsupported settlement classification."),
        };
    }

    internal static IReadOnlyList<TelemetryCostEstimate> Costs(AgentRunUsage usage) => usage.Attempts
        .Select(a => a.Accounting?.EstimatedCost).OfType<UsageCostEstimate>()
        .GroupBy(c => c.Currency).OrderBy(g => g.Key, StringComparer.Ordinal).Select(group =>
        {
            var values = group.ToArray();
            var sum = ExactCost(values);
            var knowledge = sum is null ? TelemetryAccountingKnowledge.Overflow
                : usage.Coverage == UsageInventoryCoverage.Complete && values.Length == usage.Attempts.Count
                    ? TelemetryAccountingKnowledge.Provisional : TelemetryAccountingKnowledge.Unknown;
            return new TelemetryCostEstimate(TelemetryAccountingSource.ProviderClaim, group.Key, sum, knowledge);
        }).ToArray();

    private static decimal? ExactCost(IEnumerable<UsageCostEstimate> values)
    {
        // All source values are nonnegative decimals. Scale to 28 without rounding, then fit exactly.
        BigInteger coefficient = 0;
        foreach (var value in values)
        {
            var bits = decimal.GetBits(value.Amount);
            var unscaled = (BigInteger)(uint)bits[0] + ((BigInteger)(uint)bits[1] << 32) + ((BigInteger)(uint)bits[2] << 64);
            coefficient += unscaled * BigInteger.Pow(10, 28 - ((bits[3] >> 16) & 0xff));
        }
        byte scale = 28;
        while (scale > 0 && coefficient % 10 == 0) { coefficient /= 10; scale--; }
        if (coefficient > (BigInteger.One << 96) - 1) return null;
        return new decimal(unchecked((int)(uint)(coefficient & uint.MaxValue)),
            unchecked((int)(uint)((coefficient >> 32) & uint.MaxValue)),
            unchecked((int)(uint)(coefficient >> 64)), false, scale);
    }

    private static TelemetryAccountingAmount ClaimAmount(long? value, bool complete) => new(value,
        value is null ? TelemetryAccountingKnowledge.Overflow
        : complete ? TelemetryAccountingKnowledge.Provisional : TelemetryAccountingKnowledge.Unknown);

    private static TelemetryAccountingAmount Bucket(long? value, bool present, AccountingDisposition disposition) => new(value,
        value is null ? TelemetryAccountingKnowledge.Overflow
        : !present ? TelemetryAccountingKnowledge.Known
        : disposition == AccountingDisposition.Unresolved ? TelemetryAccountingKnowledge.Unknown
        : disposition is AccountingDisposition.Reserved or AccountingDisposition.ConservativeCharge
            ? TelemetryAccountingKnowledge.Provisional : TelemetryAccountingKnowledge.Known);

    private static TelemetryAccountingAmount Allowance(long? value) => new(value,
        value.HasValue ? TelemetryAccountingKnowledge.Known : TelemetryAccountingKnowledge.Unavailable);

    private static TelemetryAccountingAmount Remaining(RunAccountingDimension dimension) => new(dimension.RemainingAllowance,
        !dimension.Allowance.HasValue ? TelemetryAccountingKnowledge.Unavailable : dimension.Coverage switch
        {
            AccountingBalanceCoverage.Known => TelemetryAccountingKnowledge.Known,
            AccountingBalanceCoverage.Provisional => TelemetryAccountingKnowledge.Provisional,
            AccountingBalanceCoverage.Unknown => TelemetryAccountingKnowledge.Unknown,
            AccountingBalanceCoverage.Overflow => TelemetryAccountingKnowledge.Overflow,
            _ => throw new ArgumentException("Unsupported accounting classification."),
        });
}

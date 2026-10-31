using Solus.Telemetry.Contracts;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;

namespace SolusAgent.Telemetry;

/// <summary>Converts public current-run observations without inferring product membership, acceptance or billing.</summary>
public static class AgentTelemetryMapper
{
    /// <summary>Maps a terminal outcome's associated usage; absent usage remains unavailable for that known execution.</summary>
    /// <remarks>The caller selects the separate product outcome. Agent completion is not product acceptance.</remarks>
    public static TelemetryRunSummary MapRun(AgentOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome.Usage is { } usage ? MapRun(usage) : new(outcome.ExecutionId,
            TelemetryInventoryCoverage.Unavailable, new(null, TelemetryMeasurementCoverage.Unavailable),
            new(null, TelemetryMeasurementCoverage.Unavailable));
    }

    /// <summary>Maps one immutable current-execution snapshot; predecessor seeds never contribute new attempts or measurements.</summary>
    /// <exception cref="ArgumentException">The mapped facts exceed the neutral summary's admission bounds.</exception>
    public static TelemetryRunSummary MapRun(AgentRunUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var attempts = usage.Attempts;
        var tools = usage.ToolInvocations;
        var admissions = usage.ToolCallAdmissions;
        var provider = attempts.SelectMany(a => a.Usage.ProviderCounters)
            .GroupBy(c => (c.Kind, c.Relationship)).OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.Relationship)
            .Select(group => MapCounter(group, usage.Coverage, attempts.Count)).ToArray();
        return new(usage.ExecutionId, Map(usage.Coverage),
            new(usage.InputTokens.ObservedTokens, Map(usage.InputTokens.Coverage)),
            new(usage.OutputTokens.ObservedTokens, Map(usage.OutputTokens.Coverage)),
            usage.Coverage == UsageInventoryCoverage.Unavailable ? null : attempts.Select(a => a.LogicalCallId).Distinct().LongCount(),
            usage.Coverage == UsageInventoryCoverage.Unavailable ? null : attempts.Count,
            tools is null && admissions is null ? null : new(admissions?.Admitted, tools?.Invoked, tools?.ReservedUnstarted, tools?.ReleasedUnstarted),
            provider, AccountingMapping.Map(usage), AccountingMapping.Settlement(usage), AccountingMapping.Costs(usage));
    }

    /// <summary>Maps exactly the caller-supplied current outcomes with independent membership coverage; no history or totals are added.</summary>
    /// <exception cref="ArgumentException">Membership is inconsistent, duplicated, contains null entries or exceeds the run bound.</exception>
    public static TelemetryUsage MapUsage(TelemetryInventoryCoverage membershipCoverage, IReadOnlyList<AgentOutcome> currentOutcomes)
    {
        ArgumentNullException.ThrowIfNull(currentOutcomes);
        if (currentOutcomes.Count > TelemetryLimits.MaximumRuns)
            throw new ArgumentException("Current membership exceeds its bound.");
        var copy = currentOutcomes.Take(TelemetryLimits.MaximumRuns + 1).ToArray();
        if (copy.Length > TelemetryLimits.MaximumRuns || copy.Any(o => o is null)
            || copy.Select(o => o.ExecutionId).Distinct().Count() != copy.Length)
            throw new ArgumentException("Current membership is invalid.");
        return new(membershipCoverage, copy.Select(MapRun).ToArray());
    }

    private static TelemetryProviderCounter MapCounter(
        IGrouping<(ProviderTokenCounterKind Kind, TokenCounterRelationship Relationship), ProviderTokenCounter> group,
        UsageInventoryCoverage inventory, int physicalAttempts)
    {
        var values = group.ToArray();
        var measured = values.Where(c => c.Value.HasValue).ToArray();
        var sum = AccountingMapping.Sum(measured.Select(c => c.Value!.Value));
        var coverage = sum is null ? TelemetryMeasurementCoverage.Overflow
            : measured.Length == 0 ? TelemetryMeasurementCoverage.Unavailable
            : inventory == UsageInventoryCoverage.Complete && measured.Length == physicalAttempts
                ? TelemetryMeasurementCoverage.Complete : TelemetryMeasurementCoverage.Partial;
        return new(Map(group.Key.Kind), Map(group.Key.Relationship),
            new(measured.Length == 0 ? null : sum, coverage), values.Length, measured.Length);
    }

    private static TelemetryInventoryCoverage Map(UsageInventoryCoverage value) => value switch
    {
        UsageInventoryCoverage.Complete => TelemetryInventoryCoverage.Complete,
        UsageInventoryCoverage.Partial => TelemetryInventoryCoverage.Partial,
        UsageInventoryCoverage.Unavailable => TelemetryInventoryCoverage.Unavailable,
        _ => throw new ArgumentException("Unsupported inventory classification."),
    };

    private static TelemetryMeasurementCoverage Map(TokenObservationCoverage value) => value switch
    {
        TokenObservationCoverage.Complete => TelemetryMeasurementCoverage.Complete,
        TokenObservationCoverage.Partial => TelemetryMeasurementCoverage.Partial,
        TokenObservationCoverage.Unavailable => TelemetryMeasurementCoverage.Unavailable,
        TokenObservationCoverage.Overflow => TelemetryMeasurementCoverage.Overflow,
        _ => throw new ArgumentException("Unsupported measurement classification."),
    };

    private static TelemetryCounterKind Map(ProviderTokenCounterKind value) => value switch
    {
        ProviderTokenCounterKind.CacheRead => TelemetryCounterKind.CacheRead,
        ProviderTokenCounterKind.CacheWrite => TelemetryCounterKind.CacheWrite,
        ProviderTokenCounterKind.UncachedInput => TelemetryCounterKind.UncachedInput,
        ProviderTokenCounterKind.Reasoning => TelemetryCounterKind.Reasoning,
        _ => throw new ArgumentException("Unsupported counter classification."),
    };

    private static TelemetryCounterRelationship Map(TokenCounterRelationship value) => value switch
    {
        TokenCounterRelationship.IncludedInInput => TelemetryCounterRelationship.IncludedInInput,
        TokenCounterRelationship.IncludedInOutput => TelemetryCounterRelationship.IncludedInOutput,
        TokenCounterRelationship.Independent => TelemetryCounterRelationship.Independent,
        _ => throw new ArgumentException("Unsupported relationship classification."),
    };
}

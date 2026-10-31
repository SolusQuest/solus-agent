namespace Solus.Telemetry.Contracts;

/// <summary>An independent measured count; partial zero remains distinct from complete zero.</summary>
public sealed class TelemetryTokenObservation
{
    /// <summary>Validates nullable nonnegative measurement and its explicit coverage.</summary>
    public TelemetryTokenObservation(long? value, TelemetryMeasurementCoverage coverage)
    {
        Validation.Count(value); Validation.Defined(coverage);
        if ((coverage is TelemetryMeasurementCoverage.Complete or TelemetryMeasurementCoverage.Partial) != value.HasValue)
            throw new ArgumentException("Measurement value and coverage disagree.");
        Value = value; Coverage = coverage;
    }
    /// <summary>Gets the observation or subtotal, never an estimate; null is not zero.</summary>
    public long? Value { get; }
    /// <summary>Gets independent measurement coverage.</summary>
    public TelemetryMeasurementCoverage Coverage { get; }
}

/// <summary>Independent optional tool counts; entries are not proof of successful business effects.</summary>
public sealed class TelemetryToolCounts
{
    /// <summary>Validates each supplied count independently; null means unreported.</summary>
    public TelemetryToolCounts(long? admitted, long? invoked, long? reservedUnstarted, long? releasedUnstarted)
    {
        Admitted = Validation.Count(admitted); Invoked = Validation.Count(invoked);
        ReservedUnstarted = Validation.Count(reservedUnstarted); ReleasedUnstarted = Validation.Count(releasedUnstarted);
    }
    /// <summary>Gets complete-batch member admissions.</summary>
    public long? Admitted { get; }
    /// <summary>Gets invocation-interface entries, including failures and unknown effects.</summary>
    public long? Invoked { get; }
    /// <summary>Gets currently reserved unstarted invocations.</summary>
    public long? ReservedUnstarted { get; }
    /// <summary>Gets cumulative released unstarted invocations.</summary>
    public long? ReleasedUnstarted { get; }
}

/// <summary>A checked summary of one provider kind and relationship across distinct current attempts.</summary>
public sealed class TelemetryProviderCounter
{
    /// <summary>Preserves scope counts and nullable coverage without combining different relationships.</summary>
    public TelemetryProviderCounter(TelemetryCounterKind kind, TelemetryCounterRelationship relationship,
        TelemetryTokenObservation observation, long reportedAttempts, long measuredAttempts)
    {
        Validation.Defined(kind); Validation.Defined(relationship); ArgumentNullException.ThrowIfNull(observation);
        if (reportedAttempts <= 0 || measuredAttempts < 0 || measuredAttempts > reportedAttempts
            || kind == TelemetryCounterKind.UncachedInput && relationship == TelemetryCounterRelationship.IncludedInOutput
            || observation.Coverage == TelemetryMeasurementCoverage.Unavailable && measuredAttempts != 0
            || observation.Coverage is TelemetryMeasurementCoverage.Complete or TelemetryMeasurementCoverage.Partial && measuredAttempts == 0
            || observation.Coverage == TelemetryMeasurementCoverage.Complete && measuredAttempts != reportedAttempts
            || observation.Coverage == TelemetryMeasurementCoverage.Overflow && measuredAttempts < 2)
            throw new ArgumentException("Provider counter scope is inconsistent.");
        Kind = kind; Relationship = relationship; Observation = observation;
        ReportedAttempts = reportedAttempts; MeasuredAttempts = measuredAttempts;
    }
    /// <summary>Gets the provider counter kind.</summary>
    public TelemetryCounterKind Kind { get; }
    /// <summary>Gets the producer-declared relationship.</summary>
    public TelemetryCounterRelationship Relationship { get; }
    /// <summary>Gets the nullable checked sum within this group only.</summary>
    public TelemetryTokenObservation Observation { get; }
    /// <summary>Gets attempts reporting this kind and relationship, including unknown values.</summary>
    public long ReportedAttempts { get; }
    /// <summary>Gets attempts with a known value in this group.</summary>
    public long MeasuredAttempts { get; }
}

/// <summary>An accounting amount and its knowledge; it never supplies measured token coverage.</summary>
public sealed class TelemetryAccountingAmount
{
    /// <summary>Validates knowledge; enclosing classification controls whether a negative amount is legal.</summary>
    public TelemetryAccountingAmount(long? value, TelemetryAccountingKnowledge knowledge)
    { Validation.Knowledge(value, knowledge); Value = value; Knowledge = knowledge; }
    /// <summary>Gets the supplied amount or estimate; null remains unreported or overflowed.</summary>
    public long? Value { get; }
    /// <summary>Gets balance/estimate knowledge independently of observations.</summary>
    public TelemetryAccountingKnowledge Knowledge { get; }
}

/// <summary>A separately classified accounting group; provider claims never become ledger authority.</summary>
public sealed class TelemetryAccountingObservation
{
    /// <summary>Validates source, classification and independent axes without calculating policy.</summary>
    public TelemetryAccountingObservation(TelemetryAccountingSource source, TelemetryAccountingKind kind,
        TelemetryAccountingAmount input, TelemetryAccountingAmount output)
    {
        Validation.Defined(source); Validation.Defined(kind); ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(output);
        if (kind != TelemetryAccountingKind.RemainingAllowance && (input.Value is < 0 || output.Value is < 0)
            || kind == TelemetryAccountingKind.Released && (input.Value is > 0 || output.Value is > 0))
            throw new ArgumentException("Accounting amount contradicts its classification.");
        Source = source; Kind = kind; Input = input; Output = output;
    }
    /// <summary>Gets provider-claim versus authoritative-run-ledger origin.</summary>
    public TelemetryAccountingSource Source { get; }
    /// <summary>Gets the independent accounting classification.</summary>
    public TelemetryAccountingKind Kind { get; }
    /// <summary>Gets input accounting, separate from measured usage.</summary>
    public TelemetryAccountingAmount Input { get; }
    /// <summary>Gets output accounting, separate from measured usage.</summary>
    public TelemetryAccountingAmount Output { get; }
}

/// <summary>A disclosed currency estimate, not a rate calculation or invoice.</summary>
public sealed class TelemetryCostEstimate
{
    /// <summary>Validates exact nullable decimal amount, source, knowledge and currency label.</summary>
    public TelemetryCostEstimate(TelemetryAccountingSource source, string currency, decimal? amount, TelemetryAccountingKnowledge knowledge)
    {
        Validation.Defined(source); Validation.Knowledge(amount, knowledge);
        if (amount is < 0 || currency is null || currency.Length != 3 || currency.Any(c => c is < 'A' or > 'Z'))
            throw new ArgumentException("A nonnegative estimate and three-uppercase-letter currency are required.");
        Source = source; Currency = currency; Amount = amount; Knowledge = knowledge;
    }
    /// <summary>Gets estimate origin.</summary>
    public TelemetryAccountingSource Source { get; }
    /// <summary>Gets currency label, without promising currency support.</summary>
    public string Currency { get; }
    /// <summary>Gets exact decimal estimate, encoded as a decimal string on the wire.</summary>
    public decimal? Amount { get; }
    /// <summary>Gets estimate knowledge.</summary>
    public TelemetryAccountingKnowledge Knowledge { get; }
}

/// <summary>A bounded current-run summary without attempt logs, prompts, outcomes or saved context.</summary>
public sealed class TelemetryRunSummary
{
    /// <summary>Copies and validates current run facts without synthesizing totals or accounting policy.</summary>
    public TelemetryRunSummary(Guid executionId, TelemetryInventoryCoverage inventoryCoverage,
        TelemetryTokenObservation inputTokens, TelemetryTokenObservation outputTokens,
        long? logicalCalls = null, long? physicalAttempts = null, TelemetryToolCounts? tools = null,
        IReadOnlyList<TelemetryProviderCounter>? providerCounters = null,
        IReadOnlyList<TelemetryAccountingObservation>? accounting = null, TelemetrySettlement? providerSettlement = null,
        IReadOnlyList<TelemetryCostEstimate>? estimatedCosts = null)
    {
        ExecutionId = Validation.Identity(executionId); InventoryCoverage = Validation.Defined(inventoryCoverage);
        ArgumentNullException.ThrowIfNull(inputTokens); ArgumentNullException.ThrowIfNull(outputTokens);
        LogicalCalls = Validation.Count(logicalCalls); PhysicalAttempts = Validation.Count(physicalAttempts);
        if (logicalCalls > physicalAttempts || logicalCalls == 0 && physicalAttempts > 0)
            throw new ArgumentException("Logical and physical counts disagree.");
        if (inventoryCoverage != TelemetryInventoryCoverage.Complete
            && (inputTokens.Coverage == TelemetryMeasurementCoverage.Complete || outputTokens.Coverage == TelemetryMeasurementCoverage.Complete))
            throw new ArgumentException("Incomplete inventory cannot prove complete measurements.");
        if (inventoryCoverage == TelemetryInventoryCoverage.Unavailable
            && (logicalCalls is not null || physicalAttempts is not null
                || inputTokens.Coverage != TelemetryMeasurementCoverage.Unavailable || outputTokens.Coverage != TelemetryMeasurementCoverage.Unavailable))
            throw new ArgumentException("Unavailable inventory cannot supply attempt measurements.");
        if (inventoryCoverage == TelemetryInventoryCoverage.Complete && physicalAttempts == 0
            && (logicalCalls != 0 || inputTokens.Coverage != TelemetryMeasurementCoverage.Complete || inputTokens.Value != 0
                || outputTokens.Coverage != TelemetryMeasurementCoverage.Complete || outputTokens.Value != 0))
            throw new ArgumentException("Complete empty inventory requires complete zero observations and counts.");
        InputTokens = inputTokens; OutputTokens = outputTokens; Tools = tools;
        ProviderCounters = Validation.Copy(providerCounters, TelemetryLimits.MaximumProviderCounters);
        Accounting = Validation.Copy(accounting, TelemetryLimits.MaximumAccountingObservations);
        EstimatedCosts = Validation.Copy(estimatedCosts, TelemetryLimits.MaximumCostEstimates);
        if (ProviderCounters.Select(c => (c.Kind, c.Relationship)).Distinct().Count() != ProviderCounters.Count
            || Accounting.Select(c => (c.Source, c.Kind)).Distinct().Count() != Accounting.Count
            || EstimatedCosts.Select(c => (c.Source, c.Currency)).Distinct().Count() != EstimatedCosts.Count)
            throw new ArgumentException("Summary groups must be unique.");
        if (inventoryCoverage == TelemetryInventoryCoverage.Unavailable && ProviderCounters.Count != 0)
            throw new ArgumentException("Unavailable inventory cannot supply provider groups.");
        if (physicalAttempts is long physicalCount)
            foreach (var group in ProviderCounters.GroupBy(counter => counter.Kind))
            {
                long reported = 0;
                foreach (var counter in group)
                {
                    // One kind can occur at most once per physical attempt, across all relationships.
                    if (counter.ReportedAttempts > physicalCount - reported)
                        throw new ArgumentException("Provider kind groups exceed physical inventory.");
                    reported = checked(reported + counter.ReportedAttempts);
                }
            }
        foreach (var counter in ProviderCounters)
        {
            if (counter.ReportedAttempts > physicalAttempts
                || counter.Observation.Coverage == TelemetryMeasurementCoverage.Complete
                    && (inventoryCoverage != TelemetryInventoryCoverage.Complete || physicalAttempts != counter.ReportedAttempts))
                throw new ArgumentException("Provider group overstates inventory coverage.");
            var parent = counter.Relationship switch
            {
                TelemetryCounterRelationship.IncludedInInput => inputTokens,
                TelemetryCounterRelationship.IncludedInOutput => outputTokens,
                _ => null,
            };
            if (parent?.Coverage == TelemetryMeasurementCoverage.Complete
                && (counter.Observation.Coverage == TelemetryMeasurementCoverage.Overflow || counter.Observation.Value > parent.Value))
                throw new ArgumentException("Provider subset exceeds its complete parent.");
        }
        if (providerSettlement.HasValue) Validation.Defined(providerSettlement.Value);
        ProviderSettlement = providerSettlement;
    }
    /// <summary>Gets current execution identity, unique within the invocation.</summary>
    public Guid ExecutionId { get; }
    /// <summary>Gets attempt-inventory coverage independently of each measured dimension.</summary>
    public TelemetryInventoryCoverage InventoryCoverage { get; }
    /// <summary>Gets actual input measurement or subtotal.</summary>
    public TelemetryTokenObservation InputTokens { get; }
    /// <summary>Gets actual output measurement or subtotal independently of input.</summary>
    public TelemetryTokenObservation OutputTokens { get; }
    /// <summary>Gets known logical calls in the supplied inventory, or null.</summary>
    public long? LogicalCalls { get; }
    /// <summary>Gets known physical attempts in the supplied inventory, or null.</summary>
    public long? PhysicalAttempts { get; }
    /// <summary>Gets independent optional tool knowledge.</summary>
    public TelemetryToolCounts? Tools { get; }
    /// <summary>Gets immutable nonoverlapping kind/relationship groups; different kinds may overlap.</summary>
    public IReadOnlyList<TelemetryProviderCounter> ProviderCounters { get; }
    /// <summary>Gets independently classified accounting observations.</summary>
    public IReadOnlyList<TelemetryAccountingObservation> Accounting { get; }
    /// <summary>Gets optional provider settlement, independent of measurements.</summary>
    public TelemetrySettlement? ProviderSettlement { get; }
    /// <summary>Gets independently classified exact currency estimates.</summary>
    public IReadOnlyList<TelemetryCostEstimate> EstimatedCosts { get; }
}

/// <summary>Product-supplied current-run membership; history belongs in an explicitly named product extension.</summary>
public sealed class TelemetryUsage
{
    /// <summary>Copies up to 256 distinct runs; complete empty membership proves no current runs.</summary>
    public TelemetryUsage(TelemetryInventoryCoverage inventoryCoverage, IReadOnlyList<TelemetryRunSummary> runs)
    {
        InventoryCoverage = Validation.Defined(inventoryCoverage); ArgumentNullException.ThrowIfNull(runs);
        Runs = Validation.Copy(runs, TelemetryLimits.MaximumRuns);
        if (Runs.Select(r => r.ExecutionId).Distinct().Count() != Runs.Count
            || inventoryCoverage == TelemetryInventoryCoverage.Unavailable && Runs.Count != 0)
            throw new ArgumentException("Current-run membership is inconsistent.");
    }
    /// <summary>Gets current-run membership coverage, not token completeness.</summary>
    public TelemetryInventoryCoverage InventoryCoverage { get; }
    /// <summary>Gets the immutable current collection; no invocation totals are synthesized.</summary>
    public IReadOnlyList<TelemetryRunSummary> Runs { get; }
}

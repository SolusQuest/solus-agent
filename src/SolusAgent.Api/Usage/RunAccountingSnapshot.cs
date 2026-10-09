namespace SolusAgent.Api.Usage;

/// <summary>Exclusive disposition of one attempt's token dimension in the admission ledger.</summary>
public enum AccountingDisposition
{
    /// <summary>Provisional pre-dispatch reservation; inventory placeholders do not release it.</summary>
    Reserved,
    /// <summary>Authoritative no-dispatch released the reservation.</summary>
    Released,
    /// <summary>Exact actual consumption replaced the estimate.</summary>
    Measured,
    /// <summary>The Host policy charged the estimate for unobserved consumption.</summary>
    ConservativeCharge,
    /// <summary>Known or possible dispatch with missing consumption retains estimated exposure.</summary>
    Unresolved,
}

/// <summary>Knowledge of an accounting balance, independent of measurement coverage.</summary>
public enum AccountingBalanceCoverage
{
    /// <summary>Only measured consumption or proven no-dispatch contributes.</summary>
    Known,
    /// <summary>Active reservations or policy charges contribute estimates.</summary>
    Provisional,
    /// <summary>Unresolved exposure remains, even when its estimate is zero.</summary>
    Unknown,
    /// <summary>Checked numeric aggregation overflowed; individual entries remain available.</summary>
    Overflow,
}

/// <summary>One exclusive numeric ledger bucket, never both a measurement and a charge.</summary>
public sealed class AccountingDimension
{
    /// <summary>Validates the disposition and nonnegative amount; Released must be zero.</summary>
    public AccountingDimension(AccountingDisposition disposition, long amount)
    {
        if (!Enum.IsDefined(disposition)) throw new ArgumentOutOfRangeException(nameof(disposition));
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (disposition == AccountingDisposition.Released && amount != 0) throw new ArgumentException("Released amounts must be zero.");
        Disposition = disposition; Amount = amount;
    }
    /// <summary>Gets the exclusive disposition.</summary>
    public AccountingDisposition Disposition { get; }
    /// <summary>Gets the exact measurement or explicit estimate appropriate to the disposition.</summary>
    public long Amount { get; }
    /// <summary>Compares every value.</summary>
    public bool Matches(AccountingDimension other) => other is not null && Disposition == other.Disposition && Amount == other.Amount;
}

/// <summary>Immutable full-attempt accounting, separate from provider-reported accounting claims.</summary>
public sealed class AttemptAccounting
{
    /// <summary>Validates full identity, the original reservation and coherent per-dimension lifecycle.</summary>
    public AttemptAccounting(Guid executionId, Guid logicalCallId, Guid physicalAttemptId, int attemptNumber,
        UsageTokenAmounts reservation, AccountingDimension input, AccountingDimension output)
    {
        if (executionId == Guid.Empty || logicalCallId == Guid.Empty || physicalAttemptId == Guid.Empty) throw new ArgumentException("Full attempt identity is required.");
        if (attemptNumber <= 0) throw new ArgumentOutOfRangeException(nameof(attemptNumber));
        ArgumentNullException.ThrowIfNull(reservation); ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(output);
        Validate(input, reservation.InputTokens); Validate(output, reservation.OutputTokens);
        if ((input.Disposition == AccountingDisposition.Reserved) != (output.Disposition == AccountingDisposition.Reserved)
            || (input.Disposition == AccountingDisposition.Released) != (output.Disposition == AccountingDisposition.Released))
            throw new ArgumentException("Reservation and no-dispatch transitions cover the whole attempt.");
        ExecutionId = executionId; LogicalCallId = logicalCallId; PhysicalAttemptId = physicalAttemptId; AttemptNumber = attemptNumber;
        Reservation = reservation; Input = input; Output = output;
    }
    private static void Validate(AccountingDimension value, long reservation)
    {
        if (value.Disposition is AccountingDisposition.Reserved or AccountingDisposition.ConservativeCharge or AccountingDisposition.Unresolved
            && value.Amount != reservation) throw new ArgumentException("Estimated amounts must equal the Host reservation.");
    }
    /// <summary>Gets the run correlation, not a shared ledger key.</summary>
    public Guid ExecutionId { get; }
    /// <summary>Gets logical call correlation.</summary>
    public Guid LogicalCallId { get; }
    /// <summary>Gets physical attempt correlation.</summary>
    public Guid PhysicalAttemptId { get; }
    /// <summary>Gets the positive physical-attempt ordinal.</summary>
    public int AttemptNumber { get; }
    /// <summary>Gets the original estimate; actual usage may exceed it.</summary>
    public UsageTokenAmounts Reservation { get; }
    /// <summary>Gets the input disposition.</summary>
    public AccountingDimension Input { get; }
    /// <summary>Gets the output disposition.</summary>
    public AccountingDimension Output { get; }
    /// <summary>Gets whether the final authoritative observation has replaced the provisional reservation.</summary>
    public bool IsFinalized => Input.Disposition != AccountingDisposition.Reserved;
    /// <summary>Compares full identity and numeric contents.</summary>
    public bool Matches(AttemptAccounting other) => other is not null && ExecutionId == other.ExecutionId && LogicalCallId == other.LogicalCallId
        && PhysicalAttemptId == other.PhysicalAttemptId && AttemptNumber == other.AttemptNumber
        && Reservation.InputTokens == other.Reservation.InputTokens && Reservation.OutputTokens == other.Reservation.OutputTokens
        && Input.Matches(other.Input) && Output.Matches(other.Output);
    /// <summary>Checks final accounting against actual core facts; provider accounting claims never participate.</summary>
    public bool MatchesObservation(UsageAttemptObservation observation, UnknownUsagePolicy policy) => observation is not null
        && ExecutionId == observation.ExecutionId && LogicalCallId == observation.LogicalCallId
        && PhysicalAttemptId == observation.PhysicalAttemptId && AttemptNumber == observation.AttemptNumber && IsFinalized
        && MatchesDimension(Input, observation.Exposure, observation.Usage.InputTokens, policy)
        && MatchesDimension(Output, observation.Exposure, observation.Usage.OutputTokens, policy);
    private static bool MatchesDimension(AccountingDimension value, DispatchExposure exposure, long? measured, UnknownUsagePolicy policy) =>
        exposure == DispatchExposure.NotDispatched ? value.Disposition == AccountingDisposition.Released
        : measured is long known ? value.Disposition == AccountingDisposition.Measured && value.Amount == known
        : value.Disposition == (policy == UnknownUsagePolicy.ConservativeCharge ? AccountingDisposition.ConservativeCharge : AccountingDisposition.Unresolved);
}

/// <summary>Checked exclusive-bucket sums for one token dimension; estimates never become measured usage.</summary>
public sealed class RunAccountingDimension
{
    internal RunAccountingDimension(IReadOnlyList<AccountingDimension> entries, long? allowance)
    {
        MeasuredTokens = Sum(entries.Where(e => e.Disposition == AccountingDisposition.Measured));
        ConservativeChargeTokens = Sum(entries.Where(e => e.Disposition == AccountingDisposition.ConservativeCharge));
        ReservedTokens = Sum(entries.Where(e => e.Disposition == AccountingDisposition.Reserved));
        UnresolvedTokens = Sum(entries.Where(e => e.Disposition == AccountingDisposition.Unresolved));
        AccountedTokens = Sum(entries);
        Allowance = allowance;
        RemainingAllowance = allowance.HasValue && AccountedTokens.HasValue ? allowance.Value - AccountedTokens.Value : null;
        Coverage = AccountedTokens is null ? AccountingBalanceCoverage.Overflow
            : entries.Any(e => e.Disposition == AccountingDisposition.Unresolved) ? AccountingBalanceCoverage.Unknown
            : entries.Any(e => e.Disposition is AccountingDisposition.Reserved or AccountingDisposition.ConservativeCharge)
                ? AccountingBalanceCoverage.Provisional : AccountingBalanceCoverage.Known;
    }
    private static long? Sum(IEnumerable<AccountingDimension> entries)
    {
        long sum = 0;
        foreach (var entry in entries)
        {
            try { sum = checked(sum + entry.Amount); }
            catch (OverflowException) { return null; }
        }
        return sum;
    }
    /// <summary>Gets the checked sum of known measurements only; zero may be an empty known subtotal.</summary>
    public long? MeasuredTokens { get; }
    /// <summary>Gets policy charges, excluding measured dimensions.</summary>
    public long? ConservativeChargeTokens { get; }
    /// <summary>Gets still-provisional reservations.</summary>
    public long? ReservedTokens { get; }
    /// <summary>Gets estimates retained for unresolved dispatched exposure, counted only once.</summary>
    public long? UnresolvedTokens { get; }
    /// <summary>Gets total accounting debit; null on overflow, never saturated.</summary>
    public long? AccountedTokens { get; }
    /// <summary>Gets the optional Host accounting allowance.</summary>
    public long? Allowance { get; }
    /// <summary>Gets accounting remainder including estimates; negative preserves observed overrun, null means no allowance or overflow.</summary>
    public long? RemainingAllowance { get; }
    /// <summary>Gets balance knowledge; inspect actual observations separately for measurement coverage.</summary>
    public AccountingBalanceCoverage Coverage { get; }
}

/// <summary>Immutable complete run-local admission ledger snapshot, with derived rather than caller-supplied balances.</summary>
public sealed class RunAccountingSnapshot
{
    /// <summary>Copies and validates identity, uniqueness, policy basis and complete ordinal inventory.</summary>
    public RunAccountingSnapshot(Guid executionId, AgentAccountingPolicy policy, IReadOnlyList<AttemptAccounting> attempts)
    {
        if (executionId == Guid.Empty) throw new ArgumentException("Run identity is required.", nameof(executionId));
        ArgumentNullException.ThrowIfNull(policy); ArgumentNullException.ThrowIfNull(attempts);
        var copy = attempts.ToArray(); var ids = new HashSet<Guid>(); var ordinals = new HashSet<(Guid, int)>();
        foreach (var entry in copy)
        {
            if (entry is null || entry.ExecutionId != executionId || !ids.Add(entry.PhysicalAttemptId)
                || !ordinals.Add((entry.LogicalCallId, entry.AttemptNumber))
                || entry.Reservation.InputTokens != policy.Reservation.InputTokens || entry.Reservation.OutputTokens != policy.Reservation.OutputTokens)
                throw new ArgumentException("Accounting inventory or reservation basis is inconsistent.", nameof(attempts));
            foreach (var axis in new[] { entry.Input, entry.Output })
                if (axis.Disposition == AccountingDisposition.ConservativeCharge && policy.UnknownUsage != UnknownUsagePolicy.ConservativeCharge
                    || axis.Disposition == AccountingDisposition.Unresolved && policy.UnknownUsage == UnknownUsagePolicy.ConservativeCharge)
                    throw new ArgumentException("Accounting disposition conflicts with Host policy.", nameof(attempts));
        }
        if (copy.GroupBy(e => e.LogicalCallId).Any(g => g.Min(e => e.AttemptNumber) != 1 || g.Max(e => e.AttemptNumber) != g.Count()))
            throw new ArgumentException("Accounting inventory requires contiguous attempt ordinals.", nameof(attempts));
        ExecutionId = executionId; Policy = policy; Attempts = Array.AsReadOnly(copy);
        Input = new(copy.Select(e => e.Input).ToArray(), policy.InputAllowance);
        Output = new(copy.Select(e => e.Output).ToArray(), policy.OutputAllowance);
    }
    /// <summary>Gets run correlation, not a global accounting identity.</summary>
    public Guid ExecutionId { get; }
    /// <summary>Gets the current request's immutable Host policy.</summary>
    public AgentAccountingPolicy Policy { get; }
    /// <summary>Gets complete copied accounting entries, independent of provider claims.</summary>
    public IReadOnlyList<AttemptAccounting> Attempts { get; }
    /// <summary>Gets derived input balances.</summary>
    public RunAccountingDimension Input { get; }
    /// <summary>Gets derived output balances.</summary>
    public RunAccountingDimension Output { get; }
    /// <summary>Compares the policy and every correlated numeric entry; derived totals necessarily agree.</summary>
    public bool Matches(RunAccountingSnapshot other) => other is not null && ExecutionId == other.ExecutionId && Policy.Matches(other.Policy)
        && Attempts.Count == other.Attempts.Count && Attempts.Zip(other.Attempts).All(pair => pair.First.Matches(pair.Second));
}

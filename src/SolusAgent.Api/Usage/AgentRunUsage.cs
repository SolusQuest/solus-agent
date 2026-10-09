namespace SolusAgent.Api.Usage;

/// <summary>Knowledge of whether a particular physical attempt was dispatched, independent of settlement.</summary>
public enum DispatchExposure
{
    /// <summary>The producer knows no dispatch occurred.</summary>
    NotDispatched,
    /// <summary>The producer knows this physical attempt was dispatched.</summary>
    Dispatched,
    /// <summary>Dispatch occurrence is unknown; this does not mean no consumption.</summary>
    Unknown,
}

/// <summary>Coverage of an attempt inventory, independent of individual measurement completeness.</summary>
public enum UsageInventoryCoverage
{
    /// <summary>All attempts up to this snapshot are included, with contiguous per-call ordinals.</summary>
    Complete,
    /// <summary>Some inventory may be omitted; no whole-run total is asserted.</summary>
    Partial,
    /// <summary>No attempt inventory is available; the list must be empty.</summary>
    Unavailable,
}

/// <summary>An immutable physical-attempt observation retaining logical retry correlation.</summary>
public sealed class UsageAttemptObservation
{
    /// <summary>Validates correlations, exposure and actual consumption before creating an observation.</summary>
    /// <exception cref="ArgumentException">An identity is empty or exposure contradicts positive measured usage or a no-dispatch charge.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The ordinal is not positive or exposure is undefined.</exception>
    /// <exception cref="ArgumentNullException">The usage observation is null.</exception>
    public UsageAttemptObservation(Guid executionId, Guid logicalCallId, Guid physicalAttemptId, int attemptNumber, DispatchExposure exposure, UsageObservation usage, UsageAccounting? accounting = null)
    {
        if (executionId == Guid.Empty) throw new ArgumentException("An execution identity is required.", nameof(executionId));
        if (logicalCallId == Guid.Empty) throw new ArgumentException("A logical call identity is required.", nameof(logicalCallId));
        if (physicalAttemptId == Guid.Empty) throw new ArgumentException("A physical attempt identity is required.", nameof(physicalAttemptId));
        if (attemptNumber <= 0) throw new ArgumentOutOfRangeException(nameof(attemptNumber));
        if (!Enum.IsDefined(exposure)) throw new ArgumentOutOfRangeException(nameof(exposure));
        ArgumentNullException.ThrowIfNull(usage);
        if (exposure != DispatchExposure.Dispatched && usage.HasPositiveConsumption)
            throw new ArgumentException("Positive actual consumption requires a dispatched attempt.", nameof(exposure));
        if (exposure == DispatchExposure.NotDispatched && accounting?.ConservativeUnobservedCharge?.IsPositive == true)
            throw new ArgumentException("A known undispatched attempt cannot carry an unobserved consumption charge.", nameof(accounting));
        ExecutionId = executionId;
        LogicalCallId = logicalCallId;
        PhysicalAttemptId = physicalAttemptId;
        AttemptNumber = attemptNumber;
        Exposure = exposure;
        Usage = usage;
        Accounting = accounting;
    }
    /// <summary>Gets the Host execution correlation.</summary>
    public Guid ExecutionId { get; }
    /// <summary>Gets the call identity shared by retries.</summary>
    public Guid LogicalCallId { get; }
    /// <summary>Gets the distinct physical attempt identity.</summary>
    public Guid PhysicalAttemptId { get; }
    /// <summary>Gets the positive ordinal within the logical call.</summary>
    public int AttemptNumber { get; }
    /// <summary>Gets dispatch knowledge, not a remote-stop guarantee.</summary>
    public DispatchExposure Exposure { get; }
    /// <summary>Gets actual nullable measurements retained even when subsequent response validation fails.</summary>
    public UsageObservation Usage { get; }
    /// <summary>Gets optional independent accounting claims.</summary>
    public UsageAccounting? Accounting { get; }
}

/// <summary>Immutable known tool-interface invocation counts, independent of provider attempts, tokens and confirmed business effects.</summary>
public sealed class ToolInvocationUsage
{
    /// <summary>Creates nonnegative counts whose sum fits the supported count range.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative or the total exceeds the supported range.</exception>
    public ToolInvocationUsage(int invoked = 0, int reservedUnstarted = 0, int releasedUnstarted = 0)
    {
        if (invoked < 0) throw new ArgumentOutOfRangeException(nameof(invoked));
        if (reservedUnstarted < 0) throw new ArgumentOutOfRangeException(nameof(reservedUnstarted));
        if (releasedUnstarted < 0) throw new ArgumentOutOfRangeException(nameof(releasedUnstarted));
        if ((long)invoked + reservedUnstarted + releasedUnstarted > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(invoked));
        Invoked = invoked; ReservedUnstarted = reservedUnstarted; ReleasedUnstarted = releasedUnstarted;
    }
    /// <summary>Gets entries into the tool invocation interface, including rejected, failed and unknown effects; not a count of successful effects.</summary>
    public int Invoked { get; }
    /// <summary>Gets admitted members not yet entered whose invocation allowances are still reserved.</summary>
    public int ReservedUnstarted { get; }
    /// <summary>Gets cumulative admitted members proved never entered whose reservations were released; these no longer consume allowance.</summary>
    public int ReleasedUnstarted { get; }
}

/// <summary>An immutable validated provider inventory with checked core token observations and independent optional tool counts.</summary>
public sealed class AgentRunUsage
{
    /// <summary>Copies attempts and validates unique identities, same-run association and complete ordinal coverage.</summary>
    /// <exception cref="ArgumentException">The identity, inventory associations or coverage are inconsistent.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Coverage is undefined.</exception>
    /// <exception cref="ArgumentNullException">The attempt list is null.</exception>
    public AgentRunUsage(Guid executionId, UsageInventoryCoverage coverage, IReadOnlyList<UsageAttemptObservation> attempts, ToolInvocationUsage? toolInvocations = null)
    {
        if (executionId == Guid.Empty) throw new ArgumentException("An execution identity is required.", nameof(executionId));
        if (!Enum.IsDefined(coverage)) throw new ArgumentOutOfRangeException(nameof(coverage));
        ArgumentNullException.ThrowIfNull(attempts);
        var snapshot = attempts.ToArray();
        var physicalIds = new HashSet<Guid>();
        var ordinals = new HashSet<(Guid, int)>();
        foreach (var attempt in snapshot)
        {
            if (attempt is null || attempt.ExecutionId != executionId || !physicalIds.Add(attempt.PhysicalAttemptId) || !ordinals.Add((attempt.LogicalCallId, attempt.AttemptNumber)))
                throw new ArgumentException("Attempts must be non-null, same-run and uniquely associated.", nameof(attempts));
        }
        if (coverage == UsageInventoryCoverage.Unavailable && snapshot.Length != 0)
            throw new ArgumentException("Unavailable inventory cannot assert attempt entries.", nameof(attempts));
        if (coverage == UsageInventoryCoverage.Complete && snapshot.GroupBy(attempt => attempt.LogicalCallId).Any(call => call.Min(attempt => attempt.AttemptNumber) != 1 || call.Max(attempt => attempt.AttemptNumber) != call.Count()))
            throw new ArgumentException("Complete inventory requires every ordinal from one through the last attempt.", nameof(attempts));
        ExecutionId = executionId;
        Coverage = coverage;
        Attempts = Array.AsReadOnly(snapshot);
        ToolInvocations = toolInvocations;
        InputTokens = RunTokenObservation.Aggregate(coverage, Attempts, usage => usage.InputTokens);
        OutputTokens = RunTokenObservation.Aggregate(coverage, Attempts, usage => usage.OutputTokens);
    }
    /// <summary>Gets the Host execution correlation.</summary>
    public Guid ExecutionId { get; }
    /// <summary>Gets inventory coverage, not measurement availability or a claim of whole-run totals.</summary>
    public UsageInventoryCoverage Coverage { get; }
    /// <summary>Gets the copied read-only observations. Complete empty inventory proves no attempt up to this snapshot; unavailable empty inventory does not.</summary>
    public IReadOnlyList<UsageAttemptObservation> Attempts { get; }
    /// <summary>Gets independent immutable tool counts, or null when unavailable. Provider inventory coverage does not establish tool knowledge.</summary>
    public ToolInvocationUsage? ToolInvocations { get; }
    /// <summary>Gets checked input observations; incomplete subtotals do not claim whole-run consumption.</summary>
    public RunTokenObservation InputTokens { get; }
    /// <summary>Gets checked output observations, independently of input availability.</summary>
    public RunTokenObservation OutputTokens { get; }
}

namespace AprHost;

/// <summary>
/// One Host-owned accepted candidate record. It is derived from the Host's own channel exchange
/// and minimized synthetic item grammar, independently of the agent's safe receipts, runtime
/// completion or any external effect.
/// </summary>
public sealed class AprAcceptanceRecord
{
    internal AprAcceptanceRecord(Guid executionId, Guid submissionId, Guid? repairsSubmissionId, AprItem item)
    {
        ExecutionId = executionId;
        SubmissionId = submissionId;
        RepairsSubmissionId = repairsSubmissionId;
        Item = item;
    }

    /// <summary>Gets the execution association of the accepted submission.</summary>
    public Guid ExecutionId { get; }

    /// <summary>Gets the accepted submission identity.</summary>
    public Guid SubmissionId { get; }

    /// <summary>Gets the rejected submission this candidate repaired, when applicable.</summary>
    public Guid? RepairsSubmissionId { get; }

    /// <summary>Gets the accepted minimized synthetic item.</summary>
    public AprItem Item { get; }

    /// <summary>Returns the type name without synthetic data.</summary>
    public override string ToString() => nameof(AprAcceptanceRecord);
}

/// <summary>
/// Host-owned synthetic acceptance and product-effect state. Acceptance is recorded only from
/// correlated acknowledged Host feedback on this Host's own channel. Effects are never implied by
/// acceptance or completion: they happen only through the explicit <see cref="ApplyEffects"/> Host
/// operation, which is a synthetic in-memory demonstration rather than a product side effect.
/// </summary>
public sealed class AprHostAcceptance
{
    private readonly object gate = new();
    private readonly List<AprAcceptanceRecord> accepted = [];
    private readonly HashSet<Guid> applied = [];
    private int effectCount;

    /// <summary>Gets the defensive ordered snapshot of Host-owned accepted candidates.</summary>
    public IReadOnlyList<AprAcceptanceRecord> Accepted
    {
        get
        {
            lock (gate)
            {
                return accepted.ToArray();
            }
        }
    }

    /// <summary>Gets the number of independently Host-accepted candidates.</summary>
    public int AcceptedCount
    {
        get
        {
            lock (gate)
            {
                return accepted.Count;
            }
        }
    }

    /// <summary>Gets the number of explicit Host effects applied; acceptance and completion never increment it.</summary>
    public int EffectCount => Volatile.Read(ref effectCount);

    /// <summary>
    /// Records one correlated Host acceptance of a parseable synthetic item. The first record for a
    /// submission identity is retained; later repetition cannot multiply accepted progress.
    /// </summary>
    /// <exception cref="ArgumentNullException">The item is null.</exception>
    internal void RecordAccepted(Guid executionId, Guid submissionId, Guid? repairsSubmissionId, AprItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (gate)
        {
            if (accepted.Any(record => record.SubmissionId == submissionId))
            {
                return;
            }

            accepted.Add(new AprAcceptanceRecord(executionId, submissionId, repairsSubmissionId, item));
        }
    }

    /// <summary>
    /// The explicit separate Host effect operation. It applies each accepted candidate at most once
    /// and returns the number of newly applied effects. No acknowledgement, receipt or completed
    /// outcome calls this operation.
    /// </summary>
    public int ApplyEffects()
    {
        lock (gate)
        {
            var newlyApplied = 0;
            foreach (var record in accepted)
            {
                if (applied.Add(record.SubmissionId))
                {
                    newlyApplied++;
                }
            }

            effectCount += newlyApplied;
            return newlyApplied;
        }
    }

    /// <summary>Returns structural counts without synthetic data.</summary>
    public override string ToString() => $"AprHostAcceptance {{ AcceptedCount = {AcceptedCount}, EffectCount = {EffectCount} }}";
}

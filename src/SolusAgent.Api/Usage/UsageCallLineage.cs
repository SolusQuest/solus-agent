namespace SolusAgent.Api.Usage;

/// <summary>Original attempt linkage for a logical operation continued in a new execution round.</summary>
public sealed class UsageCallLineage
{
    /// <summary>Creates a nonempty predecessor with an ordinal that leaves room for another attempt.</summary>
    public UsageCallLineage(Guid logicalCallId, Guid executionId, Guid physicalAttemptId, int attemptNumber)
    {
        if (logicalCallId == Guid.Empty || executionId == Guid.Empty || physicalAttemptId == Guid.Empty
            || attemptNumber < 1 || attemptNumber == int.MaxValue) throw new ArgumentException("Invalid predecessor.");
        LogicalCallId = logicalCallId; ExecutionId = executionId; PhysicalAttemptId = physicalAttemptId; AttemptNumber = attemptNumber;
    }
    /// <summary>Gets the preserved logical operation.</summary>
    public Guid LogicalCallId { get; }
    /// <summary>Gets the original predecessor execution.</summary>
    public Guid ExecutionId { get; }
    /// <summary>Gets the original predecessor attempt.</summary>
    public Guid PhysicalAttemptId { get; }
    /// <summary>Gets its global operation ordinal.</summary>
    public int AttemptNumber { get; }
    /// <summary>Compares the complete predecessor.</summary>
    public bool Matches(UsageCallLineage other) => other is not null && LogicalCallId == other.LogicalCallId
        && ExecutionId == other.ExecutionId && PhysicalAttemptId == other.PhysicalAttemptId && AttemptNumber == other.AttemptNumber;
    internal static IReadOnlyList<UsageCallLineage> Copy(Guid executionId, IReadOnlyList<UsageCallLineage>? values)
    {
        var copy = values?.ToArray() ?? [];
        if (copy.Any(v => v is null || v.ExecutionId == executionId) || copy.Select(v => v.LogicalCallId).Distinct().Count() != copy.Length
            || copy.Select(v => v.PhysicalAttemptId).Distinct().Count() != copy.Length) throw new ArgumentException("Invalid lineage.");
        return Array.AsReadOnly(copy);
    }
    internal static bool Contiguous(IEnumerable<(Guid Call, Guid Physical, int Number)> values, IReadOnlyList<UsageCallLineage> seeds)
    {
        var copy = values.ToArray();
        if (copy.Any(v => seeds.Any(s => s.PhysicalAttemptId == v.Physical))) return false;
        return copy.GroupBy(v => v.Call).All(g =>
        {
            var first = seeds.SingleOrDefault(s => s.LogicalCallId == g.Key)?.AttemptNumber ?? 0;
            return g.Min(v => v.Number) == (long)first + 1 && g.Max(v => v.Number) == (long)first + g.Count();
        });
    }
}

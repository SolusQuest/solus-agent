using SolusAgent.Api.Context;

namespace AprHost;

/// <summary>Host-owned synthetic checkpoint association, independent of Runtime's compatibility admission.</summary>
public sealed record AprCheckpointBinding(string Head, string InputDigest, string Target, string Authority);

/// <summary>One independently committed Host checkpoint. Capture delivery alone does not create this record.</summary>
public sealed record AprCheckpointRecord(ContextCheckpointInfo Source, AprCheckpointBinding Binding, bool Accepted, long Sequence);

/// <summary>Illustrative APR policy; it is not a universal Runtime rule for repository heads.</summary>
public static class AprCheckpointSelector
{
    /// <summary>Selects an exact same-head failed checkpoint, or the latest accepted checkpoint from the Host-authorized prior source set for a new-head transition. The caller owns lineage policy; this method does not infer Git ancestry.</summary>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="InvalidOperationException">No unambiguous matching source is available.</exception>
    public static AprCheckpointRecord Select(IEnumerable<AprCheckpointRecord> records, AprCheckpointBinding current, bool newHead,
        IReadOnlySet<ContextCheckpointInfo>? authorizedPriorSources = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(current);
        var authorized = newHead ? authorizedPriorSources?.ToHashSet()
            ?? throw new InvalidOperationException("A new-head transition requires explicit prior-source authorization.") : null;
        var eligible = records.Where(r => r.Binding.Target == current.Target && r.Binding.Authority == current.Authority
            && (newHead ? r.Accepted && r.Binding.Head != current.Head && authorized!.Contains(r.Source) : !r.Accepted && r.Binding == current))
            .OrderByDescending(r => r.Sequence).ToArray();
        if (eligible.Length == 0 || eligible.Length > 1 && eligible[0].Sequence == eligible[1].Sequence)
            throw new InvalidOperationException("No unambiguous authorized checkpoint matches the requested transition.");
        return eligible[0];
    }
}

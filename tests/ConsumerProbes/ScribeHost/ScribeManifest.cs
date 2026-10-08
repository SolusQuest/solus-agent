namespace SolusAgent.ConsumerProbes.ScribeHost;

/// <summary>Immutable synthetic documentation manifest: the ordered member tokens selected for one Host task.</summary>
/// <remarks>Selection is Host-owned business data, not authority or an actual product target identity. This test-only shape models no checkpoint or campaign structure.</remarks>
public sealed class ScribeManifest
{
    /// <summary>The maximum number of selected members admitted by this synthetic manifest.</summary>
    public const int MaximumMembers = 16;

    /// <summary>Creates an ordered selection after member-token and duplicate validation.</summary>
    /// <exception cref="ArgumentNullException">The selection is null.</exception>
    /// <exception cref="ArgumentException">The selection is empty or oversized, or a member token is invalid or duplicated.</exception>
    public ScribeManifest(IReadOnlyList<string> selectedMembers)
    {
        ArgumentNullException.ThrowIfNull(selectedMembers);
        if (selectedMembers.Count == 0)
        {
            throw new ArgumentException("A nonempty selection is required.", nameof(selectedMembers));
        }

        if (selectedMembers.Count > MaximumMembers)
        {
            throw new ArgumentException("The selection exceeds the synthetic manifest bound.", nameof(selectedMembers));
        }

        var snapshot = selectedMembers.ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in snapshot)
        {
            ScribeText.Member(member!);
            if (!seen.Add(member!))
            {
                throw new ArgumentException("Selected members must be unique.", nameof(selectedMembers));
            }
        }

        SelectedMembers = Array.AsReadOnly(snapshot);
    }

    /// <summary>Gets the copied ordered selection; order is Host-owned and comparison stays ordinal.</summary>
    public IReadOnlyList<string> SelectedMembers { get; }

    /// <summary>Gets whether the exact member token is selected, without normalization.</summary>
    /// <exception cref="ArgumentNullException">The member is null.</exception>
    public bool Selects(string member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return SelectedMembers.Contains(member, StringComparer.Ordinal);
    }

    /// <summary>Returns the type name without business data.</summary>
    public override string ToString() => nameof(ScribeManifest);
}

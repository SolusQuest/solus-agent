namespace SolusAgent.ConsumerProbes.ScribeHost;

/// <summary>One minimal validated documentation fact retained after candidate acceptance.</summary>
/// <remarks>Fact text is Host business data and never ordinary diagnostics. This shape carries no evidence, review transcript or provider reply.</remarks>
public sealed class ScribeFact
{
    /// <summary>The maximum UTF-8 fact-text size admitted by this synthetic progress.</summary>
    public const int MaximumTextBytes = 4_096;

    /// <summary>Creates one validated fact about a member token.</summary>
    /// <exception cref="ArgumentNullException">The member or text is null.</exception>
    /// <exception cref="ArgumentException">The member token or fact text is invalid.</exception>
    public ScribeFact(string member, string text)
    {
        Member = ScribeText.Member(member);
        Text = ScribeText.Fact(text);
    }

    /// <summary>Gets the exact selected member token this fact is about.</summary>
    public string Member { get; }

    /// <summary>Gets the validated fact text retained as Host business data.</summary>
    public string Text { get; }

    /// <summary>Returns the type name without fact data.</summary>
    public override string ToString() => nameof(ScribeFact);
}

/// <summary>Immutable minimal validated business progress: accepted facts plus derived unresolved work.</summary>
/// <remarks>This synthetic shape retains only validated facts. It is not a checkpoint, campaign history, evidence store, storage policy or restoration codec, and it cannot establish product acceptance.</remarks>
public sealed class ScribeProgress
{
    /// <summary>Creates validated progress for a manifest; empty facts mean no accepted work yet.</summary>
    /// <exception cref="ArgumentNullException">The manifest or fact list is null.</exception>
    /// <exception cref="ArgumentException">A fact is null, names an unselected member, or repeats an accepted member.</exception>
    public ScribeProgress(ScribeManifest manifest, IReadOnlyList<ScribeFact> acceptedFacts)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(acceptedFacts);
        Manifest = manifest;
        AcceptedFacts = Array.AsReadOnly(Validate(manifest, acceptedFacts));
        var accepted = new HashSet<string>(AcceptedFacts.Select(fact => fact.Member), StringComparer.Ordinal);
        UnresolvedMembers = Array.AsReadOnly(manifest.SelectedMembers.Where(member => !accepted.Contains(member)).ToArray());
    }

    /// <summary>Gets the manifest whose selection bounds this progress.</summary>
    public ScribeManifest Manifest { get; }

    /// <summary>Gets the copied accepted facts in acceptance order; nothing else is retained.</summary>
    public IReadOnlyList<ScribeFact> AcceptedFacts { get; }

    /// <summary>Gets manifest-ordered members with no accepted fact yet, distinguishing unresolved work from accepted facts.</summary>
    public IReadOnlyList<string> UnresolvedMembers { get; }

    /// <summary>Gets the number of selected members in the manifest.</summary>
    public int SelectedCount => Manifest.SelectedMembers.Count;

    /// <summary>Gets the number of accepted facts.</summary>
    public int AcceptedCount => AcceptedFacts.Count;

    /// <summary>Gets whether every selected member has an accepted fact, calculated independently of any agent outcome.</summary>
    public bool IsComplete => UnresolvedMembers.Count == 0;

    /// <summary>Commits one newly accepted fact after the same validation, returning new immutable progress.</summary>
    /// <exception cref="ArgumentNullException">The fact is null.</exception>
    /// <exception cref="ArgumentException">The fact names an unselected member or repeats an accepted member.</exception>
    public ScribeProgress Accept(ScribeFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var combined = new List<ScribeFact>(AcceptedFacts.Count + 1);
        combined.AddRange(AcceptedFacts);
        combined.Add(fact);
        return new ScribeProgress(Manifest, combined);
    }

    /// <summary>Returns the type name without fact data.</summary>
    public override string ToString() => nameof(ScribeProgress);

    private static ScribeFact[] Validate(ScribeManifest manifest, IReadOnlyList<ScribeFact> acceptedFacts)
    {
        if (acceptedFacts.Any(fact => fact is null))
        {
            throw new ArgumentException("Accepted facts must be non-null.", nameof(acceptedFacts));
        }

        var snapshot = acceptedFacts.ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in snapshot)
        {
            if (!manifest.Selects(fact.Member))
            {
                throw new ArgumentException("Each fact must name a selected member.", nameof(acceptedFacts));
            }

            if (!seen.Add(fact.Member))
            {
                throw new ArgumentException("Accepted facts cannot repeat a member.", nameof(acceptedFacts));
            }
        }

        return snapshot;
    }
}

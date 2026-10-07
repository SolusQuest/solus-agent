using SolusAgent.Api.Candidates;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Execution;
using SolusAgent.ConsumerProbes.ScribeHost;

namespace SolusAgent.ContractTests.ConsumerProbes.Scribe;

/// <summary>Shared Host construction for the Scribe scenarios; all control values stay explicit and Host-owned.</summary>
internal static class ScribeFixtures
{
    internal const string TrustedInstructions = "Scribe synthetic task: draft the remaining selected documentation members from supplied progress.";
    internal static readonly string[] DefaultMembers = ["intro", "usage", "limits"];

    internal static ScribeHostControl CreateControl(int maximumWorkUnits = 8,
        int maximumSubmissions = 3, int maximumRepairs = 1, int maximumContinuations = 1) =>
        new(TrustedInstructions,
            new AgentExecutionBounds(maximumWorkUnits, TimeSpan.FromMinutes(1)),
            new CandidateExecutionBounds(maximumSubmissions, maximumRepairs, maximumContinuations),
            AgentCapability.Cancellation);

    internal static ScribeBusinessHost CreateHost(
        IReadOnlyList<ScribeExchangePlan> plan,
        IReadOnlyList<(string Member, string Fact)>? acceptedFacts = null,
        IReadOnlyList<string>? members = null,
        int maximumWorkUnits = 8,
        int maximumSubmissions = 3,
        int maximumRepairs = 1,
        int maximumContinuations = 1)
    {
        var manifest = new ScribeManifest((members ?? DefaultMembers).ToArray());
        var facts = (acceptedFacts ?? []).Select(fact => new ScribeFact(fact.Member, fact.Fact)).ToArray();
        var progress = new ScribeProgress(manifest, facts);
        var control = CreateControl(maximumWorkUnits, maximumSubmissions, maximumRepairs, maximumContinuations);
        return new ScribeBusinessHost(manifest, progress, control, plan);
    }
}

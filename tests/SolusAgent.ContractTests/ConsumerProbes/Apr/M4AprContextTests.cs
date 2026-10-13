using AprHost;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Candidates;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.ConsumerProbes.Apr;

public sealed class M4AprContextTests
{
    [Fact]
    public async Task CombinedInvocationRecordsOnlyActualAcknowledgementAndReopensAppliedHostProgress()
    {
        var directory = Path.Combine(Path.GetTempPath(), "apr-host", Guid.NewGuid().ToString("N"));
        var provider = new PersistentProvider { Respond = r => new(r.Scope, r.Attempt, ProviderFinish.Final,
            new AprItem("alpha", 7).ToPayload(), [], new(r.Scope, r.Attempt, "APR_REPLAY"u8.ToArray())) };
        var agent = CandidateContextFixture.Agent(provider);
        var host = new AprBusinessHost((IAgent)agent, contextCandidateAgent: agent, acceptance: new(directory));
        var channel = CandidateContextFixture.Host();
        var result = await host.ExecuteCandidatesWithContextAsync(new(CandidateContextFixture.Request(), ContextExecutionIntent.Fresh),
            new(1, 0, 0), channel);
        Assert.Equal(ContextAdmission.Fresh, result.Context.Admission);
        Assert.Equal(1, provider.Effects);
        var submission = Assert.Single(channel.Submissions);
        Assert.Equal(submission.SubmissionId, Assert.Single(host.Acceptance.Accepted).SubmissionId);
        Assert.Equal(0, host.Acceptance.EffectCount);
        Assert.Equal(1, host.Acceptance.ApplyEffects());
        var reopened = new AprHostAcceptance(directory);
        Assert.Equal(1, reopened.AcceptedCount);
        Assert.Equal(1, reopened.EffectCount);
        Assert.Equal(0, reopened.ApplyEffects());
    }

    [Fact]
    public void MissingCombinedBindingCannotFallBackToSeparateMethods()
    {
        var provider = new PersistentProvider();
        var agent = CandidateContextFixture.Agent(provider);
        var host = new AprBusinessHost((IAgent)agent, agent, agent);
        Assert.Throws<NotSupportedException>(() => host.ExecuteCandidatesWithContextAsync(
            new(CandidateContextFixture.Request(), ContextExecutionIntent.Fresh), new(1, 0, 0), CandidateContextFixture.Host()));
        Assert.Equal(0, provider.Effects);
    }

    [Fact]
    public void SelectorPreservesAcceptedHistoryAndScopesInputChecksToTransition()
    {
        var accepted = new AprCheckpointRecord(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), new("n-1", "old-input", "snapshot", "authority"), true, 1);
        var failed = new AprCheckpointRecord(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), new("n", "failed-input", "snapshot", "authority"), false, 2);
        Assert.Same(failed, AprCheckpointSelector.Select([accepted, failed], failed.Binding, false));
        Assert.Same(accepted, AprCheckpointSelector.Select([accepted, failed], new("n+1", "new-input", "snapshot", "authority"), true));
        foreach (var mismatch in new[] { failed.Binding with { InputDigest = "other" }, failed.Binding with { Target = "other" },
            failed.Binding with { Authority = "other" }, failed.Binding with { Head = "other" } })
            Assert.Throws<InvalidOperationException>(() => AprCheckpointSelector.Select([accepted, failed], mismatch, false));
        Assert.Throws<InvalidOperationException>(() => AprCheckpointSelector.Select([accepted, accepted with { Source = failed.Source }],
            new("n+1", "new-input", "snapshot", "authority"), true));
    }

    [Fact]
    public void OrphanedOrCorruptHostEffectsFailClosedOnReopen()
    {
        var directory = Path.Combine(Path.GetTempPath(), "apr-host", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(directory, $"effect-{id:D}.json"), System.Text.Json.JsonSerializer.Serialize(id));
        Assert.Throws<InvalidOperationException>(() => new AprHostAcceptance(directory));
    }
}

using AprHost;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Candidates;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Consumption;

internal static class AprJournalProcessFixture
{
    internal static async Task<M4AprFreshProcessTests.Observation> Execute(string directory, string stage)
    {
        if (stage == "order-source")
        {
            var provider = new PersistentProvider();
            provider.Respond = r => new(r.Scope, r.Attempt, ProviderFinish.Final,
                new AprItem("entry" + provider.Effects, provider.Effects).ToPayload(), [],
                new(r.Scope, r.Attempt, "APR_ORDER_REPLAY"u8.ToArray()));
            var agent = CandidateContextFixture.Agent(provider);
            var host = new AprBusinessHost((IAgent)agent, contextCandidateAgent: agent,
                acceptance: new(Path.Combine(directory, "ordered-original")));
            var submissions = 0;
            var channel = new ScriptedCandidateHost((s, _) =>
            {
                submissions++;
                return ValueTask.FromResult<CandidateFeedback?>(new(s.ExecutionId, s.SubmissionId,
                    CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept,
                    submissions == 16 ? CandidateContinuation.End : CandidateContinuation.Continue));
            });
            var result = await host.ExecuteCandidatesWithContextAsync(new(CandidateContextFixture.Request(work: 16,
                limits: new(maximumLogicalCalls: 16, maximumPhysicalDispatches: 16)), ContextExecutionIntent.Fresh), new(16, 0, 15), channel);
            Assert.Equal(CandidateStopReason.Completed, result.Candidates!.StopReason);
            Assert.Equal(Enumerable.Range(1, 16).Select(n => (long)n), host.Acceptance.Accepted.Select(r => r.Item.Value));
            return new(stage, Environment.ProcessId, result.Context.Admission, provider.Imports, provider.Effects, submissions,
                host.Acceptance.AcceptedCount, host.Acceptance.EffectCount, 0, 0, 0, null, 0,
                AcceptedIds: host.Acceptance.Accepted.Select(r => r.SubmissionId).ToArray());
        }
        AprHostAcceptance reopened;
        try { reopened = new(Path.Combine(directory, "ordered-reopen")); }
        catch (InvalidOperationException) when (stage == "order-corrupt")
        {
            return new(stage, Environment.ProcessId, ContextAdmission.Rejected, 0, 0, 0, 0, 0, 0, 0, 0, null, 0);
        }
        var applied = reopened.ApplyEffects();
        return new(stage, Environment.ProcessId, ContextAdmission.Fresh, 0, 0, 0,
            reopened.AcceptedCount, reopened.EffectCount, applied, 0, 0, null, 0,
            AcceptedIds: reopened.Accepted.Select(r => r.SubmissionId).ToArray());
    }
}

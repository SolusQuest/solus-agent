using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.ContractTests.Runtime.Execution;

namespace SolusAgent.ContractTests.Runtime.Candidates;

internal static class CandidateFixture
{
    public static CandidateExecutionRequest Request(int units = 8, int submissions = 8, int repairs = 8, int continuations = 8,
        TimeSpan? duration = null, Guid? executionId = null, AgentCapability required = AgentCapability.None) =>
        new(RuntimeFixture.Request(units, duration, required, executionId: executionId), new(submissions, repairs, continuations));
    public static ICandidateAgent Agent(IModelProvider provider, RuntimeHooks? hooks = null, RuntimeOptions? options = null,
        ProviderExchangeBounds? bounds = null) => (ICandidateAgent)RuntimeFixture.Agent(provider, hooks, options, bounds);
    public static ScriptedProvider Provider(int steps = 8) => new(Enumerable.Repeat<
        Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>>>(ScriptedProvider.Final, steps).ToArray());
    public static ValueTask<CandidateFeedback?> Feedback(CandidateSubmission submission, CandidateDecision decision = CandidateDecision.Accept,
        CandidateContinuation instruction = CandidateContinuation.End, string? correction = null) =>
        ValueTask.FromResult<CandidateFeedback?>(new(submission.ExecutionId, submission.SubmissionId,
            CandidateAcknowledgement.Acknowledged, decision, instruction, correction));
}

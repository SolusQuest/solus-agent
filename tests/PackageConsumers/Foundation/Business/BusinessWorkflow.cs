using SolusAgent.Api.Execution;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using System.Text.Json;

namespace PackageConsumers.Foundation.Business;

public static class BusinessWorkflow
{
    public static async Task<AgentOutcome> ExecuteAsync(IAgent agent, CancellationToken cancellationToken = default)
    {
        var request = new AgentRequest(Guid.NewGuid(), "Complete the synthetic bounded task.", [],
            new(2, TimeSpan.FromSeconds(10)), AgentCapability.None);
        var result = await agent.ExecuteAsync(request, cancellationToken: cancellationToken);
        if (result.ExecutionId != request.ExecutionId || result.Reason != AgentTerminationReason.Completed
            || result.CompletedWorkUnits != 2 || result.Usage?.Attempts.Count != 2)
            throw new InvalidOperationException("Public business execution did not complete both turns.");
        return result;
    }

    public static async Task<CandidateExecutionResult> ExecuteLargeAsync(ICandidateAgent agent, string expectedFinal)
    {
        var host = new CompleteArtifactHost(expectedFinal);
        var request = new AgentRequest(Guid.NewGuid(), "Complete the bounded synthetic artifact task.",
            [new(AgentInputSource.Repository, new string('x', 4194304))], new(2, TimeSpan.FromSeconds(30)), AgentCapability.None);
        var result = await agent.ExecuteCandidatesAsync(new(request, new(1, 0, 0, CandidateSubmission.MaximumPayloadBytes)), host);
        if (host.Submissions != 1 || result.StopReason != CandidateStopReason.Completed || result.AcceptedCount != 1
            || result.Outcome.CompletedWorkUnits != 2 || result.Outcome.Usage!.Attempts.Count != 2)
            throw new InvalidOperationException("Opt-in candidate did not complete through the Api-only Host.");
        return result;
    }
    private sealed class CompleteArtifactHost(string expected) : ICandidateHost
    {
        internal int Submissions;
        public ValueTask<CandidateFeedback?> SubmitAsync(CandidateSubmission submission, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Submissions++;
            using var json = JsonDocument.Parse(submission.Payload);
            if (!StringComparer.Ordinal.Equals(expected, submission.Payload)) throw new InvalidOperationException("Complete candidate content differed.");
            return ValueTask.FromResult<CandidateFeedback?>(new(submission.ExecutionId, submission.SubmissionId,
                CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept, CandidateContinuation.End));
        }
    }
}

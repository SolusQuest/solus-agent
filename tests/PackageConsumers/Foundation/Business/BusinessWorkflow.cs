using SolusAgent.Api.Execution;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Usage;
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

    public static async Task<AgentOutcome> ExecuteRetriesAsync(IAgent agent, int attempts, CancellationToken cancellationToken = default)
    {
        var policy = new AgentRetryPolicy(attempts);
        var request = new AgentRequest(Guid.NewGuid(), "Complete one logical call after classified failures.", [],
            new(1, TimeSpan.FromSeconds(30)), AgentCapability.None,
            new(maximumLogicalCalls: 1, maximumPhysicalDispatches: attempts, retryPolicy: policy));
        var result = await agent.ExecuteAsync(request, cancellationToken: cancellationToken);
        if (result.Reason == AgentTerminationReason.Cancelled) cancellationToken.ThrowIfCancellationRequested();
        var usage = result.Usage!;
        if (result.Reason != AgentTerminationReason.Completed || result.CompletedWorkUnits != 1 || usage.Attempts.Count != attempts
            || usage.Attempts.Select(a => a.LogicalCallId).Distinct().Count() != 1
            || usage.Attempts.Select(a => a.PhysicalAttemptId).Distinct().Count() != attempts
            || !usage.Attempts.Select(a => a.AttemptNumber).SequenceEqual(Enumerable.Range(1, attempts))
            || usage.InputTokens.ObservedTokens != 3L * attempts || usage.OutputTokens.ObservedTokens != 2L * attempts)
            throw new InvalidOperationException("Public single-call retry capacity did not complete and retain all observations.");
        return result;
    }

    public static async Task<CandidateExecutionResult> ExecuteLargeAsync(ICandidateAgent agent, string expectedFinal, CancellationToken cancellationToken = default)
    {
        var host = new CompleteArtifactHost(expectedFinal);
        var request = new AgentRequest(Guid.NewGuid(), "Complete the bounded synthetic artifact task.",
            [new(AgentInputSource.Repository, new string('x', 4194304))], new(2, TimeSpan.FromSeconds(30)), AgentCapability.None);
        var result = await agent.ExecuteCandidatesAsync(new(request, new(1, 0, 0, CandidateSubmission.MaximumPayloadBytes)), host, cancellationToken: cancellationToken);
        if (result.Outcome.Reason == AgentTerminationReason.Cancelled) cancellationToken.ThrowIfCancellationRequested();
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

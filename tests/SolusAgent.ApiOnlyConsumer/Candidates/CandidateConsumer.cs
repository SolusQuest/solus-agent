using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;

namespace SolusAgent.ApiOnlyConsumer.Candidates;

/// <summary>Actual Api-only calling consumer; its returned observations contain no Host payloads.</summary>
public static class CandidateConsumer
{
    public static async ValueTask<ObservedCandidateExecution> RunAsync(
        ICandidateAgent agent,
        CandidateExecutionRequest request,
        ICandidateHost host,
        CancellationToken cancellationToken = default)
    {
        var progress = new List<AgentProgress>();
        var result = await agent.ExecuteCandidatesAsync(request, host, new InlineProgress(progress.Add), cancellationToken);
        return new ObservedCandidateExecution(progress.AsReadOnly(), result);
    }

    private sealed class InlineProgress(Action<AgentProgress> report) : IProgress<AgentProgress>
    {
        public void Report(AgentProgress value) => report(value);
    }
}

public sealed record ObservedCandidateExecution(IReadOnlyList<AgentProgress> Progress, CandidateExecutionResult Result);

/// <summary>Test-only Host storage and feedback through the independent Api boundary.</summary>
public sealed class ScriptedCandidateHost(Func<CandidateSubmission, CancellationToken, ValueTask<CandidateFeedback?>> exchange) : ICandidateHost
{
    private readonly List<CandidateSubmission> submissions = [];
    private int effects;

    public IReadOnlyList<CandidateSubmission> Submissions => submissions.AsReadOnly();
    public int Effects => Volatile.Read(ref effects);

    public ValueTask<CandidateFeedback?> SubmitAsync(CandidateSubmission submission, CancellationToken cancellationToken = default)
    {
        submissions.Add(submission);
        return exchange(submission, cancellationToken);
    }

    // Intentionally separate Host action: neither acknowledgement nor runtime completion calls this.
    public void ApplyEffect() => Interlocked.Increment(ref effects);
}

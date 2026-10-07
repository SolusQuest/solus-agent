using SolusAgent.Api.Execution;

namespace SolusAgent.ApiOnlyConsumer;

/// <summary>Actual test-only business consumption through the outer interface alone.</summary>
public static class AgentConsumer
{
    public static async ValueTask<ObservedExecution> RunAsync(
        IAgent agent,
        AgentRequest request,
        CancellationToken cancellationToken = default)
    {
        var progress = new CollectingProgress();
        var outcome = await agent.ExecuteAsync(request, progress, cancellationToken);
        return new ObservedExecution(progress.Values.AsReadOnly(), outcome);
    }

    private sealed class CollectingProgress : IProgress<AgentProgress>
    {
        internal List<AgentProgress> Values { get; } = [];

        public void Report(AgentProgress value) => Values.Add(value);
    }
}

public sealed record ObservedExecution(IReadOnlyList<AgentProgress> Progress, AgentOutcome Outcome);

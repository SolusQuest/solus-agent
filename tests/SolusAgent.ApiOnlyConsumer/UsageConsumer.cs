using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;

namespace SolusAgent.ApiOnlyConsumer;

/// <summary>A Host consuming current snapshots and the retained terminal snapshot solely through the outer API.</summary>
public static class UsageConsumer
{
    public static async ValueTask<ObservedUsageExecution> RunAsync(IAgent agent, AgentRequest request, CancellationToken cancellationToken = default)
    {
        var progress = new CollectingProgress();
        var outcome = await agent.ExecuteAsync(request, progress, cancellationToken);
        return new ObservedUsageExecution(progress.Values.AsReadOnly(), outcome, outcome.Usage);
    }
    private sealed class CollectingProgress : IProgress<AgentProgress>
    {
        internal List<AgentProgress> Values { get; } = [];
        public void Report(AgentProgress value) => Values.Add(value);
    }
}

public sealed record ObservedUsageExecution(IReadOnlyList<AgentProgress> Progress, AgentOutcome Outcome, AgentRunUsage? FinalUsage);

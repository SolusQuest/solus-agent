using SolusAgent.Api.Execution;
using SolusAgent.Api.Capabilities;

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
}

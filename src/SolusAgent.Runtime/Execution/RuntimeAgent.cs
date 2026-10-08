using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Runtime.Tools;

namespace SolusAgent.Runtime.Execution;

internal sealed partial class RuntimeAgent(RuntimeConfiguration configuration, RuntimeOptions options) : ICandidateAgent
{
    public AgentCapability SupportedCapabilities => RuntimeAgentFactory.Support.SupportedCapabilities;

    public async ValueTask<AgentOutcome> ExecuteAsync(AgentRequest request, IProgress<AgentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var unsupported = request.RequiredCapabilities & ~SupportedCapabilities;
        if (unsupported != AgentCapability.None)
            return new(request.ExecutionId, AgentTerminationReason.UnsupportedCapability, 0, unsupported,
                usage: new AgentRunUsage(request.ExecutionId, UsageInventoryCoverage.Complete, []));
        using var cut = new RunCut(options.TimeProvider, request.Bounds.MaximumDuration, cancellationToken);
        var state = new RunState(request, configuration, options, cut);
        try
        {
            if (cut.Check() != RuntimeStop.None) return CutOutcome(state);
            state.Initialize();
            while (true)
            {
                var providerRequest = state.AdmitTurn();
                if (providerRequest is null) return StopOutcome(state);
                var attempt = await ProviderAttemptOperation.ExecuteAsync(state, providerRequest).ConfigureAwait(false);
                if (cut.Check() != RuntimeStop.None) return CutOutcome(state);
                if (attempt.Stop != RuntimeStop.None) return StopOutcome(state);
                if (attempt.SettlementStop is not (RuntimeStop.None or RuntimeStop.HostStopped))
                    return state.Outcome(AgentTerminationReason.Failed, AgentFailureCode.ExecutionFailed);
                if (attempt.Response is null)
                    return attempt.ProviderError == ProviderError.LimitExceeded
                        ? state.Outcome(AgentTerminationReason.ResourceLimit)
                        : state.Outcome(AgentTerminationReason.Failed, AgentFailureCode.ExecutionFailed);

                var finished = attempt.Response.Finish == ProviderFinish.Final;
                if (attempt.Response.Finish == ProviderFinish.ToolCalls)
                {
                    // The accepted model turn is not permission to bypass provider closure or all-member tool admission.
                    if (!state.CanContinue) return StopOutcome(state);
                    var batch = await ToolBatchOperation.ExecuteAsync(state, providerRequest, attempt.Response).ConfigureAwait(false);
                    if (cut.Check() != RuntimeStop.None) return CutOutcome(state);
                    if (batch.Stop != RuntimeStop.None || batch.Error != SolusAgent.Tools.Api.ToolError.None) return StopOutcome(state);
                }
                else if (!finished) return state.Outcome(AgentTerminationReason.Failed, AgentFailureCode.ExecutionFailed);
                if (finished) state.Close(RuntimeStop.HostStopped);
                try
                {
                    if (!cut.TryCommit(() => progress?.Report(new(request.ExecutionId, state.Completed, state.Usage()))))
                        return CutOutcome(state);
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
                { return state.Outcome(AgentTerminationReason.Failed, AgentFailureCode.ProgressObserverFailed); }
                if (cut.Check() != RuntimeStop.None) return CutOutcome(state);
                if (finished) return state.Outcome(AgentTerminationReason.Completed);
            }
        }
        catch (ProviderContractException exception)
        { return exception.Error == ProviderError.LimitExceeded ? state.Outcome(AgentTerminationReason.ResourceLimit)
            : state.Outcome(AgentTerminationReason.Failed, AgentFailureCode.ExecutionFailed); }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return state.Outcome(AgentTerminationReason.Failed, AgentFailureCode.ExecutionFailed);
        }
    }
    private static AgentOutcome CutOutcome(RunState state) => state.Outcome(state.Cut.Check() == RuntimeStop.Cancelled
        ? AgentTerminationReason.Cancelled : AgentTerminationReason.ResourceLimit);
    private static AgentOutcome StopOutcome(RunState state) => state.Cut.Check() != RuntimeStop.None ? CutOutcome(state)
        : state.AdmissionStop == RuntimeStop.ResourceLimit ? state.Outcome(AgentTerminationReason.ResourceLimit)
        : state.AdmissionStop is RuntimeStop.ExposureDenied or RuntimeStop.HostStopped ? state.Outcome(AgentTerminationReason.Partial)
        : state.Outcome(AgentTerminationReason.Failed, AgentFailureCode.ExecutionFailed);
}

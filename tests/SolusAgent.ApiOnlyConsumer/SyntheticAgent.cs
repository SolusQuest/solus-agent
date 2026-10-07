using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Execution;

namespace SolusAgent.ApiOnlyConsumer;

/// <summary>A test-only agent whose work unit is one successfully awaited synthetic operation.</summary>
public sealed class SyntheticAgent : IAgent
{
    private readonly int targetWorkUnits;
    private readonly int? partialAfterWorkUnits;
    private readonly Func<int, CancellationToken, ValueTask> performWork;
    private int totalWorkStarted;

    public SyntheticAgent(
        int targetWorkUnits,
        int? partialAfterWorkUnits = null,
        Func<int, CancellationToken, ValueTask>? performWork = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWorkUnits);
        if (partialAfterWorkUnits is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(partialAfterWorkUnits));
        }

        this.targetWorkUnits = targetWorkUnits;
        this.partialAfterWorkUnits = partialAfterWorkUnits;
        this.performWork = performWork ?? DefaultWorkAsync;
    }

    public AgentCapability SupportedCapabilities => AgentCapability.WorkUnitLimit | AgentCapability.Cancellation;

    public int TotalWorkStarted => Volatile.Read(ref totalWorkStarted);

    public async ValueTask<AgentOutcome> ExecuteAsync(
        AgentRequest request,
        IProgress<AgentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var unsupported = request.RequiredCapabilities & ~SupportedCapabilities;
        if (unsupported != AgentCapability.None)
        {
            return new AgentOutcome(request.ExecutionId, AgentTerminationReason.UnsupportedCapability, 0, unsupported);
        }

        var completed = 0;
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Outcome(AgentTerminationReason.Cancelled);
            }

            if (completed == targetWorkUnits)
            {
                return Outcome(AgentTerminationReason.Completed);
            }

            if (completed == request.Bounds.MaximumWorkUnits)
            {
                return Outcome(AgentTerminationReason.ResourceLimit);
            }

            if (partialAfterWorkUnits is int partialLimit && completed >= partialLimit)
            {
                return Outcome(AgentTerminationReason.Partial);
            }

            try
            {
                Interlocked.Increment(ref totalWorkStarted);
                await performWork(completed + 1, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Outcome(AgentTerminationReason.Cancelled);
            }
            catch (Exception)
            {
                return Outcome(AgentTerminationReason.Failed, AgentFailureCode.ExecutionFailed);
            }

            // A successful work await remains completed even if cancellation arrived before observation.
            completed++;
            try
            {
                progress?.Report(new AgentProgress(request.ExecutionId, completed));
            }
            catch (Exception)
            {
                return Outcome(AgentTerminationReason.Failed, AgentFailureCode.ProgressObserverFailed);
            }
        }

        AgentOutcome Outcome(AgentTerminationReason reason, AgentFailureCode failureCode = AgentFailureCode.None) =>
            new(request.ExecutionId, reason, completed, failureCode: failureCode);
    }

    private static async ValueTask DefaultWorkAsync(int workUnit, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
    }
}

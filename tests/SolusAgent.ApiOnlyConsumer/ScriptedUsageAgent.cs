using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;

namespace SolusAgent.ApiOnlyConsumer;

/// <summary>A finite synthetic attempt script, not a provider or production budget engine.</summary>
public sealed record UsageScriptStep(
    Guid LogicalCallId,
    Func<CancellationToken, ValueTask<UsageObservation>> MeasureAsync,
    Func<bool>? ValidateResponse = null,
    UsageAccounting? Accounting = null);

/// <summary>Actual Api-only usage exchange with dispatch capture before awaiting and measurement capture before validation.</summary>
public sealed class ScriptedUsageAgent : IAgent
{
    private readonly UsageScriptStep[] steps;
    private int totalDispatches;

    public ScriptedUsageAgent(IReadOnlyList<UsageScriptStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        this.steps = steps.ToArray();
        if (this.steps.Length == 0 || this.steps.Any(step => step is null || step.LogicalCallId == Guid.Empty || step.MeasureAsync is null))
            throw new ArgumentException("A nonempty valid synthetic script is required.", nameof(steps));
        var groups = new HashSet<Guid>();
        Guid previous = Guid.Empty;
        foreach (var step in this.steps)
        {
            if (step.LogicalCallId != previous && !groups.Add(step.LogicalCallId))
                throw new ArgumentException("Retries must be contiguous within each logical call.", nameof(steps));
            previous = step.LogicalCallId;
        }
    }

    public AgentCapability SupportedCapabilities => AgentCapability.WorkUnitLimit | AgentCapability.Cancellation
        | AgentCapability.UsageReporting | AgentCapability.DispatchLimits | AgentCapability.UsageThresholds;
    public int TotalDispatches => Volatile.Read(ref totalDispatches);

    public async ValueTask<AgentOutcome> ExecuteAsync(AgentRequest request, IProgress<AgentProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var attempts = new List<UsageAttemptObservation>();
        var snapshot = Snapshot();
        var unsupported = request.RequiredCapabilities & ~SupportedCapabilities;
        if (unsupported != AgentCapability.None)
            return new AgentOutcome(request.ExecutionId, AgentTerminationReason.UnsupportedCapability, 0, unsupported, usage: snapshot);
        var completed = 0;
        var index = 0;
        var logicalCalls = 0;
        var ordinal = 0;
        var currentLogical = Guid.Empty;
        while (true)
        {
            // Successful final validation/reporting wins; thresholds only stop further work.
            if (index == steps.Length) return Outcome(AgentTerminationReason.Completed);
            if (cancellationToken.IsCancellationRequested) return Outcome(AgentTerminationReason.Cancelled);
            if (completed >= request.Bounds.MaximumWorkUnits) return Outcome(AgentTerminationReason.ResourceLimit);
            var step = steps[index];
            var newLogical = currentLogical != step.LogicalCallId;
            if (request.UsageLimits?.MaximumPhysicalDispatches is int maximumDispatches && attempts.Count >= maximumDispatches
                || newLogical && request.UsageLimits?.MaximumLogicalCalls is int maximumCalls && logicalCalls >= maximumCalls)
                return Outcome(AgentTerminationReason.ResourceLimit);
            var thresholdStop = ThresholdStop();
            if (thresholdStop is AgentTerminationReason stop) return Outcome(stop);
            if (newLogical)
            {
                logicalCalls++;
                currentLogical = step.LogicalCallId;
                ordinal = 0;
            }
            ordinal++;
            var physicalId = Guid.NewGuid();
            // Actual synthetic dispatch is captured even if the awaited measurement never returns.
            Interlocked.Increment(ref totalDispatches);
            attempts.Add(new UsageAttemptObservation(request.ExecutionId, currentLogical, physicalId, ordinal, DispatchExposure.Dispatched, new UsageObservation()));
            snapshot = Snapshot();
            if (!Report()) return Outcome(AgentTerminationReason.Failed, AgentFailureCode.ProgressObserverFailed);
            try
            {
                var measurement = await step.MeasureAsync(cancellationToken);
                attempts[^1] = new UsageAttemptObservation(request.ExecutionId, currentLogical, physicalId, ordinal, DispatchExposure.Dispatched, measurement, step.Accounting);
                snapshot = Snapshot();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Outcome(AgentTerminationReason.Cancelled);
            }
            catch (Exception)
            {
                return Outcome(AgentTerminationReason.Failed, AgentFailureCode.ExecutionFailed);
            }
            // Capture precedes optional observers and all response validation.
            if (!Report()) return Outcome(AgentTerminationReason.Failed, AgentFailureCode.ProgressObserverFailed);
            bool accepted;
            try { accepted = step.ValidateResponse?.Invoke() ?? true; }
            catch (Exception) { return Outcome(AgentTerminationReason.Failed, AgentFailureCode.ExecutionFailed); }
            index++;
            if (!accepted)
            {
                if (index < steps.Length && steps[index].LogicalCallId == currentLogical) continue;
                return Outcome(AgentTerminationReason.Failed, AgentFailureCode.ExecutionFailed);
            }
            completed++;
            if (!Report()) return Outcome(AgentTerminationReason.Failed, AgentFailureCode.ProgressObserverFailed);
            // Remaining entries in this group are retry alternatives, unnecessary after acceptance.
            while (index < steps.Length && steps[index].LogicalCallId == currentLogical) index++;
        }

        AgentRunUsage Snapshot() => new(request.ExecutionId, UsageInventoryCoverage.Complete, attempts);
        AgentOutcome Outcome(AgentTerminationReason reason, AgentFailureCode failureCode = AgentFailureCode.None) =>
            new(request.ExecutionId, reason, completed, failureCode: failureCode, usage: snapshot);
        bool Report()
        {
            try { progress?.Report(new AgentProgress(request.ExecutionId, completed, snapshot)); return true; }
            catch (Exception) { return false; }
        }
        AgentTerminationReason? ThresholdStop()
        {
            if (attempts.Count == 0) return null;
            var input = request.UsageLimits?.InputTokenThreshold;
            var output = request.UsageLimits?.OutputTokenThreshold;
            // A reached known dimension is enough to stop, even if another comparison is unknown.
            var inputComparison = Compare(input, attempt => attempt.Usage.InputTokens);
            var outputComparison = Compare(output, attempt => attempt.Usage.OutputTokens);
            if (inputComparison == true || outputComparison == true) return AgentTerminationReason.ResourceLimit;
            if (input.HasValue && inputComparison is null || output.HasValue && outputComparison is null) return AgentTerminationReason.Partial;
            return null;
        }
        bool? Compare(long? threshold, Func<UsageAttemptObservation, long?> read)
        {
            if (!threshold.HasValue) return false;
            long total = 0;
            foreach (var attempt in attempts)
            {
                if (read(attempt) is not long value) return null;
                try { total = checked(total + value); }
                catch (OverflowException) { return null; }
            }
            return total >= threshold.Value;
        }
    }
}

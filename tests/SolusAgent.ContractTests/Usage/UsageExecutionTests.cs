using System.Text.Json;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer;
using Xunit;

namespace SolusAgent.ContractTests.Usage;

public sealed class UsageExecutionTests
{
    private static readonly Guid Execution = Guid.NewGuid();
    private static readonly Guid Logical = Guid.NewGuid();
    private const AgentCapability Required = AgentCapability.WorkUnitLimit | AgentCapability.Cancellation | AgentCapability.UsageReporting | AgentCapability.DispatchLimits | AgentCapability.UsageThresholds;

    [Fact]
    public async Task ActualConsumerRetainsRetryMeasurementsWithDistinctPhysicalIdentityAndImmutableProgress()
    {
        var validations = 0;
        var agent = new ScriptedUsageAgent([Step(3, accepted: () => { validations++; return false; }), Step(4, accepted: () => { validations++; return true; })]);
        var observed = await UsageConsumer.RunAsync(agent, Request());
        Assert.Equal(AgentTerminationReason.Completed, observed.Outcome.Reason);
        Assert.Equal(1, observed.Outcome.CompletedWorkUnits);
        Assert.Equal(2, agent.TotalDispatches);
        Assert.Equal(2, validations);
        var attempts = observed.FinalUsage!.Attempts;
        Assert.Equal(2, attempts.Count);
        Assert.All(attempts, attempt => { Assert.Equal(Execution, attempt.ExecutionId); Assert.Equal(Logical, attempt.LogicalCallId); Assert.Equal(DispatchExposure.Dispatched, attempt.Exposure); });
        Assert.NotEqual(attempts[0].PhysicalAttemptId, attempts[1].PhysicalAttemptId);
        Assert.Equal(new[] { 1, 2 }, attempts.Select(attempt => attempt.AttemptNumber));
        Assert.Equal(new long?[] { 3, 4 }, attempts.Select(attempt => attempt.Usage.InputTokens));
        Assert.All(observed.Progress, value => { Assert.Equal(Execution, value.ExecutionId); Assert.Equal(Execution, value.Usage!.ExecutionId); });
        var capturedBeforeAwait = Assert.Single(observed.Progress[0].Usage!.Attempts);
        Assert.Equal(UsageCompleteness.Unavailable, capturedBeforeAwait.Usage.Completeness);
        Assert.Equal(3, Assert.Single(observed.Progress[1].Usage!.Attempts).Usage.InputTokens);
        Assert.Equal(capturedBeforeAwait.PhysicalAttemptId, attempts[0].PhysicalAttemptId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MeasuredUsageIsPublishedBeforeLaterResponseRejectionOrException(bool throws)
    {
        const string canary = "restricted-response-and-credential-canary";
        AgentProgress? published = null;
        var agent = new ScriptedUsageAgent([Step(7, accepted: () =>
        {
            Assert.Equal(7, Assert.Single(published!.Usage!.Attempts).Usage.InputTokens);
            if (throws) throw new InvalidOperationException(canary);
            return false;
        })]);
        var outcome = await agent.ExecuteAsync(Request(), new InlineProgress(value => published = value));
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason);
        Assert.Equal(AgentFailureCode.ExecutionFailed, outcome.FailureCode);
        Assert.Equal(0, outcome.CompletedWorkUnits);
        Assert.Equal(7, Assert.Single(outcome.Usage!.Attempts).Usage.InputTokens);
        Assert.DoesNotContain(canary, JsonSerializer.Serialize(outcome) + outcome + published, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MeasurementFailureAfterActualDispatchRetainsUnavailableExposureWithoutFabricatedZero()
    {
        var agent = new ScriptedUsageAgent([new(Logical, _ => ValueTask.FromException<UsageObservation>(new InvalidOperationException("restricted-canary")))]);
        var outcome = await agent.ExecuteAsync(Request());
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason);
        Assert.Equal(1, agent.TotalDispatches);
        var attempt = Assert.Single(outcome.Usage!.Attempts);
        Assert.Equal(DispatchExposure.Dispatched, attempt.Exposure);
        Assert.Null(attempt.Usage.InputTokens);
        Assert.Null(attempt.Usage.OutputTokens);
        Assert.Null(attempt.Accounting);
        Assert.DoesNotContain("restricted-canary", JsonSerializer.Serialize(outcome), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationBeforeDispatchProvesEmptyCompleteInventory()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var agent = new ScriptedUsageAgent([Step(1)]);
        var observed = await UsageConsumer.RunAsync(agent, Request(), cancellation.Token);
        Assert.Equal(AgentTerminationReason.Cancelled, observed.Outcome.Reason);
        Assert.Equal(0, agent.TotalDispatches);
        Assert.Empty(observed.Progress);
        Assert.Equal(UsageInventoryCoverage.Complete, observed.FinalUsage!.Coverage);
        Assert.Empty(observed.FinalUsage.Attempts);
    }

    [Fact]
    public async Task DeterministicCancellationWhileMeasurementIsPendingRetainsDispatchedUnknown()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverMeasured = new TaskCompletionSource<UsageObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new ScriptedUsageAgent([new(Logical, async token => { started.SetResult(); return await neverMeasured.Task.WaitAsync(token); })]);
        var pending = UsageConsumer.RunAsync(agent, Request(), cancellation.Token).AsTask();
        await started.Task;
        cancellation.Cancel();
        var observed = await pending;
        Assert.Equal(AgentTerminationReason.Cancelled, observed.Outcome.Reason);
        Assert.Equal(0, observed.Outcome.CompletedWorkUnits);
        Assert.Equal(1, agent.TotalDispatches);
        var attempt = Assert.Single(observed.FinalUsage!.Attempts);
        Assert.Equal(DispatchExposure.Dispatched, attempt.Exposure);
        Assert.Equal(UsageCompleteness.Unavailable, attempt.Usage.Completeness);
        Assert.Null(attempt.Accounting);
    }

    [Theory]
    [InlineData(1, AgentTerminationReason.Completed)]
    [InlineData(2, AgentTerminationReason.Cancelled)]
    public async Task CancellationAfterKnownUsagePreservesSuccessfulWorkAndFinalCompletionPriority(int calls, AgentTerminationReason expected)
    {
        using var cancellation = new CancellationTokenSource();
        var steps = new List<UsageScriptStep> { new(Logical, _ => { cancellation.Cancel(); return ValueTask.FromResult(new UsageObservation(7, 2)); }) };
        if (calls == 2) steps.Add(Step(1, logical: Guid.NewGuid()));
        var agent = new ScriptedUsageAgent(steps);
        var observed = await UsageConsumer.RunAsync(agent, Request(new AgentUsageLimits(inputTokenThreshold: 1)), cancellation.Token);
        Assert.Equal(expected, observed.Outcome.Reason);
        Assert.Equal(1, observed.Outcome.CompletedWorkUnits);
        Assert.Equal(7, Assert.Single(observed.FinalUsage!.Attempts).Usage.InputTokens);
        Assert.Equal(1, agent.TotalDispatches);
    }

    [Theory]
    [InlineData(1, 0, UsageCompleteness.Unavailable)]
    [InlineData(2, 0, UsageCompleteness.Complete)]
    [InlineData(3, 1, UsageCompleteness.Complete)]
    public async Task ObserverFailureAtEveryPublicationBoundaryRetainsCurrentCapture(int throwOnReport, int completed, UsageCompleteness completeness)
    {
        var reports = 0;
        var agent = new ScriptedUsageAgent([Step(5)]);
        var outcome = await agent.ExecuteAsync(Request(), new InlineProgress(_ => { if (++reports == throwOnReport) throw new InvalidOperationException("observer-restricted-canary"); }));
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason);
        Assert.Equal(AgentFailureCode.ProgressObserverFailed, outcome.FailureCode);
        Assert.Equal(completed, outcome.CompletedWorkUnits);
        Assert.Equal(1, agent.TotalDispatches);
        Assert.Equal(completeness, Assert.Single(outcome.Usage!.Attempts).Usage.Completeness);
        Assert.DoesNotContain("observer-restricted-canary", JsonSerializer.Serialize(outcome), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AgentCapability.UsageReporting)]
    [InlineData(AgentCapability.DispatchLimits)]
    [InlineData(AgentCapability.UsageThresholds)]
    public async Task BaselineAgentRejectsUnadvertisedUsageGuaranteesBeforeWork(AgentCapability capability)
    {
        var agent = new SyntheticAgent(1);
        var observed = await AgentConsumer.RunAsync(agent, Request(required: capability));
        Assert.Equal(AgentTerminationReason.UnsupportedCapability, observed.Outcome.Reason);
        Assert.Equal(capability, observed.Outcome.UnsupportedCapabilities);
        Assert.Empty(observed.Progress);
        Assert.Equal(0, agent.TotalWorkStarted);
    }

    [Fact]
    public async Task ScriptRejectsDurationBeforeDispatchOrProgress()
    {
        var agent = new ScriptedUsageAgent([Step(1)]);
        var observed = await UsageConsumer.RunAsync(agent, Request(required: Required | AgentCapability.DurationLimit));
        Assert.Equal(AgentTerminationReason.UnsupportedCapability, observed.Outcome.Reason);
        Assert.Equal(AgentCapability.DurationLimit, observed.Outcome.UnsupportedCapabilities);
        Assert.Empty(observed.Progress);
        Assert.Empty(observed.FinalUsage!.Attempts);
        Assert.Equal(0, agent.TotalDispatches);
    }

    [Fact]
    public async Task LogicalLimitStopsNewCallsButPhysicalLimitCountsRetryAttempts()
    {
        var retry = new ScriptedUsageAgent([Step(2, accepted: () => false), Step(3), Step(4, logical: Guid.NewGuid())]);
        var byLogical = await UsageConsumer.RunAsync(retry, Request(new AgentUsageLimits(maximumLogicalCalls: 1)));
        Assert.Equal(AgentTerminationReason.ResourceLimit, byLogical.Outcome.Reason);
        Assert.Equal(1, byLogical.Outcome.CompletedWorkUnits);
        Assert.Equal(2, byLogical.FinalUsage!.Attempts.Count);
        var byPhysical = await UsageConsumer.RunAsync(retry, Request(new AgentUsageLimits(maximumPhysicalDispatches: 1)));
        Assert.Equal(AgentTerminationReason.ResourceLimit, byPhysical.Outcome.Reason);
        Assert.Equal(0, byPhysical.Outcome.CompletedWorkUnits);
        Assert.Single(byPhysical.FinalUsage!.Attempts);
        var byWork = await UsageConsumer.RunAsync(new ScriptedUsageAgent([Step(1), Step(1, logical: Guid.NewGuid())]), Request(workBound: 1));
        Assert.Equal(AgentTerminationReason.ResourceLimit, byWork.Outcome.Reason);
        Assert.Single(byWork.FinalUsage!.Attempts);
    }

    [Theory]
    [InlineData(1, 10, false, AgentTerminationReason.Completed)]
    [InlineData(1, 11, false, AgentTerminationReason.Completed)]
    [InlineData(2, 10, false, AgentTerminationReason.ResourceLimit)]
    [InlineData(2, 11, false, AgentTerminationReason.ResourceLimit)]
    [InlineData(1, 11, true, AgentTerminationReason.Completed)]
    [InlineData(2, 11, true, AgentTerminationReason.ResourceLimit)]
    public async Task PostResponseThresholdRetainsOvershootAndStopsOnlyRemainingWork(int calls, long tokens, bool outputDimension, AgentTerminationReason expected)
    {
        var measurement = outputDimension ? new UsageObservation(0, tokens) : new UsageObservation(tokens, 0);
        var steps = new List<UsageScriptStep> { new(Logical, _ => ValueTask.FromResult(measurement)) };
        if (calls == 2) steps.Add(Step(1, logical: Guid.NewGuid()));
        var limits = outputDimension ? new AgentUsageLimits(outputTokenThreshold: 10) : new AgentUsageLimits(inputTokenThreshold: 10);
        var agent = new ScriptedUsageAgent(steps);
        var observed = await UsageConsumer.RunAsync(agent, Request(limits));
        Assert.Equal(expected, observed.Outcome.Reason);
        Assert.Equal(1, observed.Outcome.CompletedWorkUnits);
        Assert.Equal(1, agent.TotalDispatches);
        Assert.Same(measurement, Assert.Single(observed.FinalUsage!.Attempts).Usage);
    }

    [Theory]
    [InlineData(1, AgentTerminationReason.Completed)]
    [InlineData(2, AgentTerminationReason.Partial)]
    public async Task UnknownRequiredComparisonStopsRemainingWorkWithoutUndoingCompletion(int calls, AgentTerminationReason expected)
    {
        var steps = new List<UsageScriptStep> { new(Logical, _ => ValueTask.FromResult(new UsageObservation(outputTokens: 0))) };
        if (calls == 2) steps.Add(Step(1, logical: Guid.NewGuid()));
        var agent = new ScriptedUsageAgent(steps);
        var observed = await UsageConsumer.RunAsync(agent, Request(new AgentUsageLimits(inputTokenThreshold: 10)));
        Assert.Equal(expected, observed.Outcome.Reason);
        Assert.Equal(1, agent.TotalDispatches);
        Assert.Null(Assert.Single(observed.FinalUsage!.Attempts).Usage.InputTokens);
    }

    [Fact]
    public async Task OnlyConfiguredDimensionsNeedKnownComparisonsAndAccountingIsNotConsumption()
    {
        var accounting = new UsageAccounting(UsageSettlement.Unsettled, new(1000, 1000), new(1000, 1000), new(1000, "USD"));
        var steps = new[] { new UsageScriptStep(Logical, _ => ValueTask.FromResult(new UsageObservation(2)), Accounting: accounting), new UsageScriptStep(Guid.NewGuid(), _ => ValueTask.FromResult(new UsageObservation(3))) };
        var observed = await UsageConsumer.RunAsync(new ScriptedUsageAgent(steps), Request(new AgentUsageLimits(inputTokenThreshold: 10)));
        Assert.Equal(AgentTerminationReason.Completed, observed.Outcome.Reason);
        Assert.Equal(2, observed.FinalUsage!.Attempts.Count);
        Assert.Null(observed.FinalUsage.Attempts[0].Usage.OutputTokens);
        Assert.Equal(2, observed.FinalUsage.Attempts[0].Usage.InputTokens);
        Assert.Same(accounting, observed.FinalUsage.Attempts[0].Accounting);
    }

    [Fact]
    public async Task CumulativeComparisonIncludesAllRetryAttemptsAndOverflowRemainsUnknown()
    {
        var cumulative = new ScriptedUsageAgent([Step(6, accepted: () => false), Step(6), Step(1, logical: Guid.NewGuid())]);
        var stopped = await UsageConsumer.RunAsync(cumulative, Request(new AgentUsageLimits(inputTokenThreshold: 10)));
        Assert.Equal(AgentTerminationReason.ResourceLimit, stopped.Outcome.Reason);
        Assert.Equal(2, stopped.FinalUsage!.Attempts.Count);
        var overflow = new ScriptedUsageAgent([Step(long.MaxValue - 1), Step(long.MaxValue - 1, logical: Guid.NewGuid()), Step(1, logical: Guid.NewGuid())]);
        var unknown = await UsageConsumer.RunAsync(overflow, Request(new AgentUsageLimits(inputTokenThreshold: long.MaxValue)));
        Assert.Equal(AgentTerminationReason.Partial, unknown.Outcome.Reason);
        Assert.Equal(2, unknown.FinalUsage!.Attempts.Count);
        Assert.All(unknown.FinalUsage.Attempts, attempt => Assert.Equal(long.MaxValue - 1, attempt.Usage.InputTokens));
    }

    [Fact]
    public async Task RetryThresholdStopsNextPhysicalAttemptButTerminalValidationFailureRemainsFailed()
    {
        var retry = new ScriptedUsageAgent([Step(10, accepted: () => false), Step(1)]);
        var stopped = await UsageConsumer.RunAsync(retry, Request(new AgentUsageLimits(inputTokenThreshold: 10)));
        Assert.Equal(AgentTerminationReason.ResourceLimit, stopped.Outcome.Reason);
        Assert.Equal(0, stopped.Outcome.CompletedWorkUnits);
        Assert.Single(stopped.FinalUsage!.Attempts);
        var terminal = await UsageConsumer.RunAsync(new ScriptedUsageAgent([Step(10, accepted: () => false)]), Request(new AgentUsageLimits(inputTokenThreshold: 10)));
        Assert.Equal(AgentTerminationReason.Failed, terminal.Outcome.Reason);
        Assert.Single(terminal.FinalUsage!.Attempts);
    }

    private static UsageScriptStep Step(long input, Func<bool>? accepted = null, Guid? logical = null) => new(logical ?? Logical, _ => ValueTask.FromResult(new UsageObservation(input, 0)), accepted);
    private static AgentRequest Request(AgentUsageLimits? limits = null, int workBound = 10, AgentCapability required = Required) => new(Execution, "Synthetic Host task", [], new AgentExecutionBounds(workBound, TimeSpan.FromSeconds(1)), required, limits);
    private sealed class InlineProgress(Action<AgentProgress> observe) : IProgress<AgentProgress>
    {
        public void Report(AgentProgress value) => observe(value);
    }
}

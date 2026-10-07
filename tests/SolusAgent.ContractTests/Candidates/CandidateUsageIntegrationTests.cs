using System.Text.Json;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer;
using SolusAgent.ApiOnlyConsumer.Candidates;
using Xunit;

namespace SolusAgent.ContractTests.Candidates;

public sealed class CandidateUsageIntegrationTests
{
    private static readonly Guid ExecutionId = Guid.Parse("c787b083-5f65-4acf-a128-888320cbe27c");

    [Theory]
    [InlineData(AgentCapability.UsageReporting, false)]
    [InlineData(AgentCapability.UsageReporting, true)]
    [InlineData(AgentCapability.DispatchLimits, false)]
    [InlineData(AgentCapability.DispatchLimits, true)]
    [InlineData(AgentCapability.UsageThresholds, false)]
    [InlineData(AgentCapability.UsageThresholds, true)]
    [InlineData(AgentCapability.UsageReporting | AgentCapability.DispatchLimits | AgentCapability.UsageThresholds, false)]
    [InlineData(AgentCapability.UsageReporting | AgentCapability.DispatchLimits | AgentCapability.UsageThresholds, true)]
    public async Task RequiredUsageGuaranteesRejectBeforeCancellationProductionProgressAndHost(AgentCapability required, bool preCancelled)
    {
        using var cancellation = new CancellationTokenSource();
        if (preCancelled) { cancellation.Cancel(); }
        var execution = Request(required | AgentCapability.WorkUnitLimit | AgentCapability.Cancellation);
        var agent = new ScriptedCandidateAgent((_, _) => throw new InvalidOperationException("production must not start"));
        var host = new ScriptedCandidateHost((_, _) => throw new InvalidOperationException("Host must not be called"));
        var observed = await CandidateConsumer.RunAsync(agent, new(execution, new(2, 0, 1)), host, cancellation.Token);
        Assert.Equal(CandidateStopReason.UnsupportedCapability, observed.Result.StopReason);
        Assert.Equal(AgentTerminationReason.UnsupportedCapability, observed.Result.Outcome.Reason);
        Assert.Equal(required, observed.Result.Outcome.UnsupportedCapabilities);
        Assert.Equal(0, observed.Result.Outcome.CompletedWorkUnits);
        Assert.Null(observed.Result.Outcome.Usage);
        Assert.Equal(0, agent.TotalProductionStarted);
        Assert.Empty(observed.Result.Receipts);
        Assert.Empty(observed.Progress);
        Assert.Empty(host.Submissions);
        Assert.Equal(0, host.Effects);
        Assert.Equal(AgentCapability.None, agent.SupportedCapabilities & required);

        var ordinary = await AgentConsumer.RunAsync(agent, execution, cancellation.Token);
        Assert.Equal(AgentTerminationReason.UnsupportedCapability, ordinary.Outcome.Reason);
        Assert.Equal(required, ordinary.Outcome.UnsupportedCapabilities);
        Assert.Equal(0, ordinary.Outcome.CompletedWorkUnits);
        Assert.Null(ordinary.Outcome.Usage);
        Assert.Empty(ordinary.Progress);
    }

    [Fact]
    public async Task OptionalUsageLimitsRemainAdvisoryWithoutFabricatedMeasurementsOrEnforcement()
    {
        var agent = new ScriptedCandidateAgent((_, _) => ValueTask.FromResult("first"), (_, _) => ValueTask.FromResult("second"));
        var host = new ScriptedCandidateHost((submission, _) => ValueTask.FromResult<CandidateFeedback?>(
            new(submission.ExecutionId, submission.SubmissionId, CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept, CandidateContinuation.Continue)));
        var request = Request(AgentCapability.WorkUnitLimit | AgentCapability.Cancellation);
        var observed = await CandidateConsumer.RunAsync(agent, new(request, new(2, 0, 1)), host);
        Assert.Equal(1, request.UsageLimits!.MaximumLogicalCalls);
        Assert.Equal(1, request.UsageLimits.MaximumPhysicalDispatches);
        Assert.Equal(CandidateStopReason.Completed, observed.Result.StopReason);
        Assert.Equal(2, observed.Result.AcceptedCount);
        Assert.Equal(2, observed.Result.Outcome.CompletedWorkUnits);
        Assert.Equal(2, host.Submissions.Count);
        Assert.Null(observed.Result.Outcome.Usage);
        Assert.All(observed.Progress, progress => Assert.Null(progress.Usage));
    }

    [Fact]
    public void ComposedUsageRemainsIndependentOfAcceptanceWorkUnitsAndHostEffects()
    {
        var measurements = new UsageObservation(3, null,
            [new(ProviderTokenCounterKind.UncachedInput, 3, TokenCounterRelationship.IncludedInInput)]);
        var accounting = new UsageAccounting(UsageSettlement.Unknown, new(1, 2), new(0, 7), new(0.01m, "USD"));
        var attempt = new UsageAttemptObservation(ExecutionId, Guid.NewGuid(), Guid.NewGuid(), 1, DispatchExposure.Dispatched, measurements, accounting);
        var usage = new AgentRunUsage(ExecutionId, UsageInventoryCoverage.Partial, [attempt]);
        var outcome = new AgentOutcome(ExecutionId, AgentTerminationReason.Partial, 1, usage: usage);
        var result = new CandidateExecutionResult(outcome, CandidateStopReason.HostEnded,
            [new(ExecutionId, Guid.NewGuid(), CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept, CandidateContinuation.Continue),
             new(ExecutionId, Guid.NewGuid(), CandidateAcknowledgement.Acknowledged, CandidateDecision.Reject, CandidateContinuation.End)]);
        Assert.Same(outcome, result.Outcome);
        Assert.Same(usage, result.Outcome.Usage);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(2, result.Receipts.Count);
        Assert.Equal(1, result.Outcome.CompletedWorkUnits);
        Assert.Equal(UsageCompleteness.Partial, measurements.Completeness);
        Assert.Null(measurements.OutputTokens);
        Assert.Equal(UsageSettlement.Unknown, accounting.Settlement);
        Assert.Equal("USD", accounting.EstimatedCost!.Currency);
        Assert.DoesNotContain("Payload", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("CorrectionText", JsonSerializer.Serialize(result));
        var error = Assert.Throws<ArgumentException>(() => new UsageCostEstimate(0, "restricted-candidate-canary"));
        Assert.DoesNotContain("restricted-candidate-canary", error.ToString());
        Assert.Throws<ArgumentException>(() => new AgentOutcome(Guid.NewGuid(), AgentTerminationReason.Partial, 1, usage: usage));
    }

    private static AgentRequest Request(AgentCapability required) => new(ExecutionId, "Trusted Host task", [],
        new AgentExecutionBounds(2, TimeSpan.FromSeconds(1)), required, new AgentUsageLimits(1, 1, 1, 1));
}

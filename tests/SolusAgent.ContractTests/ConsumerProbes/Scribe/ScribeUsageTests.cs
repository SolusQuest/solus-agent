using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer;
using SolusAgent.ConsumerProbes.ScribeHost;
using Xunit;

namespace SolusAgent.ContractTests.ConsumerProbes.Scribe;

/// <summary>AC4: independent outer usage producer over Host-owned inputs, with honest unavailable/partial/failure neighbors.</summary>
public sealed class ScribeUsageTests
{
    [Fact]
    public async Task UsageProducerReportsKnownMeasurementsThroughProgressAndOutcome()
    {
        var host = ScribeFixtures.CreateHost([]);
        var request = host.CreateFreshRequest().Request;
        var step = new UsageScriptStep(Guid.NewGuid(), _ => ValueTask.FromResult(new UsageObservation(3, 2)));
        var agent = new ScriptedUsageAgent([step]);

        var observed = await UsageConsumer.RunAsync(agent, request);

        Assert.Equal(AgentTerminationReason.Completed, observed.Outcome.Reason);
        Assert.Equal(request.ExecutionId, observed.Outcome.ExecutionId);
        Assert.NotNull(observed.FinalUsage);
        var usage = observed.FinalUsage!;
        Assert.Equal(UsageInventoryCoverage.Complete, usage.Coverage);
        var attempt = Assert.Single(usage.Attempts);
        Assert.Equal(DispatchExposure.Dispatched, attempt.Exposure);
        Assert.Equal(3, attempt.Usage.InputTokens);
        Assert.Equal(2, attempt.Usage.OutputTokens);
        Assert.All(observed.Progress, progress => Assert.NotNull(progress.Usage));
    }

    [Fact]
    public async Task UsageNeighborsRetainUnavailablePartialAndFailureAfterDispatchHonestly()
    {
        var host = ScribeFixtures.CreateHost([]);

        var unavailableStep = new UsageScriptStep(Guid.NewGuid(), _ => ValueTask.FromResult(new UsageObservation()));
        var unavailable = await UsageConsumer.RunAsync(new ScriptedUsageAgent([unavailableStep]), host.CreateFreshRequest().Request);
        var unavailableAttempt = Assert.Single(unavailable.FinalUsage!.Attempts);
        Assert.Equal(UsageCompleteness.Unavailable, unavailableAttempt.Usage.Completeness);
        Assert.Null(unavailableAttempt.Usage.InputTokens);
        Assert.Null(unavailableAttempt.Usage.OutputTokens);

        var partialStep = new UsageScriptStep(Guid.NewGuid(), _ => ValueTask.FromResult(new UsageObservation(5, null)));
        var partial = await UsageConsumer.RunAsync(new ScriptedUsageAgent([partialStep]), host.CreateFreshRequest().Request);
        var partialAttempt = Assert.Single(partial.FinalUsage!.Attempts);
        Assert.Equal(UsageCompleteness.Partial, partialAttempt.Usage.Completeness);
        Assert.Equal(5, partialAttempt.Usage.InputTokens);
        Assert.Null(partialAttempt.Usage.OutputTokens);

        // A dispatched attempt keeps its observation even when the response then fails validation.
        var failedStep = new UsageScriptStep(Guid.NewGuid(), _ => ValueTask.FromResult(new UsageObservation(7, 1)), () => false);
        var failed = await UsageConsumer.RunAsync(new ScriptedUsageAgent([failedStep]), host.CreateFreshRequest().Request);
        Assert.Equal(AgentTerminationReason.Failed, failed.Outcome.Reason);
        Assert.Equal(AgentFailureCode.ExecutionFailed, failed.Outcome.FailureCode);
        var retained = Assert.Single(failed.FinalUsage!.Attempts);
        Assert.Equal(DispatchExposure.Dispatched, retained.Exposure);
        Assert.Equal(7, retained.Usage.InputTokens);
        Assert.Equal(1, retained.Usage.OutputTokens);
    }

    [Fact]
    public async Task CandidateRuntimeAndUsageObservationsStaySeparateWithoutAggregation()
    {
        var host = ScribeFixtures.CreateHost([new ScribeExchangePlan(true, ScribeDelivery.Delivered, CandidateContinuation.End)]);
        var round = new ScribeRound(host, [new ScribeProductionStep("intro", "INTRO_FACT_CANARY usage separation")]);
        var candidateObserved = await round.RunAsync();

        // The candidate producer reports no usage at all; null stays a valid disclosure rather than hidden zero.
        Assert.Null(candidateObserved.Result.Outcome.Usage);
        Assert.All(candidateObserved.Progress, progress => Assert.Null(progress.Usage));

        var usageRequest = host.CreateFreshRequest().Request;
        var step = new UsageScriptStep(Guid.NewGuid(), _ => ValueTask.FromResult(new UsageObservation(11, 4)));
        var usageObserved = await UsageConsumer.RunAsync(new ScriptedUsageAgent([step]), usageRequest);

        Assert.NotEqual(candidateObserved.Result.Outcome.ExecutionId, usageObserved.Outcome.ExecutionId);
        Assert.NotNull(usageObserved.FinalUsage);
        var usage = usageObserved.FinalUsage!;
        var attempt = Assert.Single(usage.Attempts);
        Assert.Equal(usageRequest.ExecutionId, attempt.ExecutionId);
        Assert.Equal(11, attempt.Usage.InputTokens);
        Assert.Equal(4, attempt.Usage.OutputTokens);

        // No inferred aggregation or double count across the separate candidate, runtime and usage scenarios.
        Assert.Empty(round.Runtime.Productions.Select(record => record.Exchange.Attempt.PhysicalAttemptId)
            .Intersect(usage.Attempts.Select(item => item.PhysicalAttemptId)));
    }
}

using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer;
using Xunit;

namespace SolusAgent.ContractTests.Usage;

public sealed class UsageRegressionTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task KnownCountExhaustionWinsOverUnknownOrOverflowedTokenComparison(bool physicalLimit, bool overflow)
    {
        var admitted = overflow ? 2 : 1;
        var steps = Enumerable.Range(0, admitted + 1).Select(_ => new UsageScriptStep(Guid.NewGuid(), _ => ValueTask.FromResult(overflow ? new UsageObservation(long.MaxValue - 1, 0) : new UsageObservation(outputTokens: 0)))).ToArray();
        var limits = new AgentUsageLimits(maximumLogicalCalls: physicalLimit ? null : admitted, maximumPhysicalDispatches: physicalLimit ? admitted : null, inputTokenThreshold: overflow ? long.MaxValue : 10);
        var stopped = await UsageConsumer.RunAsync(new ScriptedUsageAgent(steps), Request(limits));
        Assert.Equal(AgentTerminationReason.ResourceLimit, stopped.Outcome.Reason);
        Assert.Equal(admitted, stopped.FinalUsage!.Attempts.Count);
        // With the count gate still open, the same missing comparison stays explicitly unknown.
        var available = new AgentUsageLimits(maximumLogicalCalls: physicalLimit ? null : admitted + 1, maximumPhysicalDispatches: physicalLimit ? admitted + 1 : null, inputTokenThreshold: overflow ? long.MaxValue : 10);
        var unknown = await UsageConsumer.RunAsync(new ScriptedUsageAgent(steps), Request(available));
        Assert.Equal(AgentTerminationReason.Partial, unknown.Outcome.Reason);
        Assert.Equal(admitted, unknown.FinalUsage!.Attempts.Count);
    }

    [Fact]
    public async Task ExistingLogicalCallRetryDoesNotConsumeANewLogicalAdmission()
    {
        var logical = Guid.NewGuid();
        var agent = new ScriptedUsageAgent([new(logical, _ => ValueTask.FromResult(new UsageObservation(outputTokens: 0)), () => false), new(logical, _ => ValueTask.FromResult(new UsageObservation(1, 0)))]);
        var observed = await UsageConsumer.RunAsync(agent, Request(new AgentUsageLimits(maximumLogicalCalls: 1, inputTokenThreshold: 10)));
        Assert.Equal(AgentTerminationReason.Partial, observed.Outcome.Reason);
        Assert.Single(observed.FinalUsage!.Attempts);
        Assert.Equal(1, agent.TotalDispatches);
    }

    [Fact]
    public async Task FinalCompletionStillWinsOverCountExhaustionAndUnknownMeasurement()
    {
        var agent = new ScriptedUsageAgent([new(Guid.NewGuid(), _ => ValueTask.FromResult(new UsageObservation()))]);
        var observed = await UsageConsumer.RunAsync(agent, Request(new AgentUsageLimits(maximumLogicalCalls: 1, maximumPhysicalDispatches: 1, inputTokenThreshold: 10)));
        Assert.Equal(AgentTerminationReason.Completed, observed.Outcome.Reason);
        Assert.Equal(UsageCompleteness.Unavailable, Assert.Single(observed.FinalUsage!.Attempts).Usage.Completeness);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(null)]
    public void InherentInputAxisRejectsOutputRelationshipEvenWhenTheValueIsZeroOrUnknown(long? value)
    {
        Assert.Throws<ArgumentException>(() => new ProviderTokenCounter(ProviderTokenCounterKind.UncachedInput, value, TokenCounterRelationship.IncludedInOutput));
        var input = new UsageObservation(1, 0, [new(ProviderTokenCounterKind.UncachedInput, value, TokenCounterRelationship.IncludedInInput)]);
        Assert.Equal(value, Assert.Single(input.ProviderCounters).Value);
        var independent = new UsageObservation(0, 0, [new(ProviderTokenCounterKind.UncachedInput, value, TokenCounterRelationship.Independent)]);
        Assert.Equal(value, Assert.Single(independent.ProviderCounters).Value);
    }

    [Fact]
    public void GenericProviderDetailsKeepProducerDeclaredAxesAndDoNotInventFixedPartitions()
    {
        foreach (var kind in new[] { ProviderTokenCounterKind.CacheRead, ProviderTokenCounterKind.CacheWrite, ProviderTokenCounterKind.Reasoning })
        {
            var facts = new UsageObservation(2, 2, [new(kind, 1, TokenCounterRelationship.IncludedInInput)]);
            Assert.Equal(TokenCounterRelationship.IncludedInInput, Assert.Single(facts.ProviderCounters).Relationship);
            var otherAxis = new UsageObservation(2, 2, [new(kind, 1, TokenCounterRelationship.IncludedInOutput)]);
            Assert.Equal(TokenCounterRelationship.IncludedInOutput, Assert.Single(otherAxis.ProviderCounters).Relationship);
        }
    }

    private static AgentRequest Request(AgentUsageLimits limits) => new(Guid.NewGuid(), "Synthetic Host task", [], new AgentExecutionBounds(10, TimeSpan.FromSeconds(1)), AgentCapability.UsageReporting | AgentCapability.DispatchLimits | AgentCapability.UsageThresholds, limits);
}

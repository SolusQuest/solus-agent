using Solus.Telemetry.Contracts;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Telemetry;
using Xunit;
using static SolusAgent.ContractTests.Telemetry.TelemetryFixtures;

namespace SolusAgent.ContractTests.Telemetry;

public sealed class TelemetryMappingTests
{
    [Fact]
    public void CompleteEmptyAndProvenNoDispatchAreZeroButAbsentOrUnobservedUsageIsUnknown()
    {
        var empty = RoundTrip(Run()).Usage.Runs[0];
        Assert.Equal(0L, empty.InputTokens.Value); Assert.Equal(TelemetryMeasurementCoverage.Complete, empty.InputTokens.Coverage);
        Assert.Equal(0L, empty.PhysicalAttempts); Assert.Equal(0L, empty.LogicalCalls); Assert.Null(empty.Tools);
        var id = Guid.NewGuid();
        var undispatched = RoundTrip(new(id, UsageInventoryCoverage.Complete,
            [Attempt(id, new(), exposure: DispatchExposure.NotDispatched)])).Usage.Runs[0];
        Assert.Equal(0L, undispatched.InputTokens.Value); Assert.Equal(TelemetryMeasurementCoverage.Complete, undispatched.OutputTokens.Coverage);
        var unknown = RoundTrip(Run(UsageInventoryCoverage.Complete, new UsageObservation())).Usage.Runs[0];
        Assert.Null(unknown.InputTokens.Value); Assert.Equal(TelemetryMeasurementCoverage.Unavailable, unknown.OutputTokens.Coverage);
        var absent = AgentTelemetryMapper.MapRun(new AgentOutcome(id, AgentTerminationReason.Completed, 0));
        Assert.Equal(id, absent.ExecutionId); Assert.Null(absent.PhysicalAttempts); Assert.Null(absent.InputTokens.Value);
        Assert.Equal(TelemetryInventoryCoverage.Unavailable, absent.InventoryCoverage);
    }

    [Fact]
    public void PartialZeroAndIndependentDimensionsRetainTheirKnowledge()
    {
        var partial = RoundTrip(Run(UsageInventoryCoverage.Partial, new UsageObservation(0))).Usage.Runs[0];
        Assert.Equal(0L, partial.InputTokens.Value); Assert.Equal(TelemetryMeasurementCoverage.Partial, partial.InputTokens.Coverage);
        Assert.Equal(TelemetryMeasurementCoverage.Unavailable, partial.OutputTokens.Coverage); Assert.Equal(1L, partial.PhysicalAttempts);
        var mixed = RoundTrip(Run(UsageInventoryCoverage.Complete, new UsageObservation(7, 3), new UsageObservation(5))).Usage.Runs[0];
        Assert.Equal(12L, mixed.InputTokens.Value); Assert.Equal(TelemetryMeasurementCoverage.Complete, mixed.InputTokens.Coverage);
        Assert.Equal(3L, mixed.OutputTokens.Value); Assert.Equal(TelemetryMeasurementCoverage.Partial, mixed.OutputTokens.Coverage);
        var missing = RoundTrip(Run(UsageInventoryCoverage.Unavailable)).Usage.Runs[0];
        Assert.Null(missing.PhysicalAttempts); Assert.Null(missing.LogicalCalls);
    }

    [Theory]
    [InlineData(AgentTerminationReason.Failed)]
    [InlineData(AgentTerminationReason.Cancelled)]
    [InlineData(AgentTerminationReason.ResourceLimit)]
    [InlineData(AgentTerminationReason.Partial)]
    public void FailedAndRetriedWorkIsRetainedWithoutInferringProductOutcome(AgentTerminationReason reason)
    {
        var id = Guid.NewGuid(); var call = Guid.NewGuid();
        var usage = new AgentRunUsage(id, UsageInventoryCoverage.Complete,
            [Attempt(id, new(5, 1), call: call), Attempt(id, new(7, 2), call: call, number: 2)]);
        var mapped = AgentTelemetryMapper.MapRun(Outcome(usage, reason));
        Assert.Equal(1L, mapped.LogicalCalls); Assert.Equal(2L, mapped.PhysicalAttempts);
        Assert.Equal(12L, mapped.InputTokens.Value); Assert.Equal(3L, mapped.OutputTokens.Value);
    }

    [Fact]
    public void ContinuedCallAndUnusedPredecessorSeedsAreNotHistoricalConsumption()
    {
        var id = Guid.NewGuid(); var call = Guid.NewGuid();
        var seed = new UsageCallLineage(call, Guid.NewGuid(), Guid.NewGuid(), 2);
        var usage = new AgentRunUsage(id, UsageInventoryCoverage.Complete, [Attempt(id, new(7, 0), call: call, number: 3)], continuedCalls: [seed]);
        var run = RoundTrip(usage).Usage.Runs[0];
        Assert.Equal(1L, run.LogicalCalls); Assert.Equal(1L, run.PhysicalAttempts); Assert.Equal(7L, run.InputTokens.Value);
        run = RoundTrip(new(id, UsageInventoryCoverage.Complete, [], continuedCalls: [seed])).Usage.Runs[0];
        Assert.Equal(0L, run.LogicalCalls); Assert.Equal(0L, run.PhysicalAttempts); Assert.Equal(0L, run.InputTokens.Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ToolKnowledgeIsIndependentOfProviderInventoryAndPeerCounts(bool admissions, bool invocations)
    {
        var run = RoundTrip(new(Guid.NewGuid(), UsageInventoryCoverage.Unavailable, [],
            invocations ? new ToolInvocationUsage(2, 3, 4) : null,
            toolCallAdmissions: admissions ? new ToolCallAdmissionUsage(11) : null)).Usage.Runs[0];
        if (!admissions && !invocations) Assert.Null(run.Tools);
        else
        {
            Assert.Equal(admissions ? 11L : null, run.Tools!.Admitted);
            Assert.Equal(invocations ? 2L : null, run.Tools.Invoked);
            Assert.Equal(invocations ? 3L : null, run.Tools.ReservedUnstarted);
            Assert.Equal(invocations ? 4L : null, run.Tools.ReleasedUnstarted);
        }
    }

    [Fact]
    public void CoreOverflowDominatesMissingAndLargeValuesStayExactOnWire()
    {
        var record = RoundTrip(Run(UsageInventoryCoverage.Partial,
            new UsageObservation(long.MaxValue, 9007199254740993), new UsageObservation(1), new UsageObservation()));
        var run = record.Usage.Runs[0];
        Assert.Equal(TelemetryMeasurementCoverage.Overflow, run.InputTokens.Coverage); Assert.Null(run.InputTokens.Value);
        Assert.Equal(9007199254740993L, run.OutputTokens.Value); Assert.Equal(TelemetryMeasurementCoverage.Partial, run.OutputTokens.Coverage);
        Assert.True(TelemetryJson.TrySerialize(record, out var json, out _)); Assert.Contains("\"9007199254740993\"", json!);
    }

    [Fact]
    public void OverlappingDetailsRemainSeparateFactsWithoutCachePartitionInference()
    {
        var read = new ProviderTokenCounter(ProviderTokenCounterKind.CacheRead, 8, TokenCounterRelationship.IncludedInInput);
        var write = new ProviderTokenCounter(ProviderTokenCounterKind.CacheWrite, 8, TokenCounterRelationship.IncludedInInput);
        var independent = new ProviderTokenCounter(ProviderTokenCounterKind.CacheRead, 10, TokenCounterRelationship.Independent);
        var run = RoundTrip(Run(UsageInventoryCoverage.Complete, new UsageObservation(10, 0, [read, write]),
            new UsageObservation(10, 0, [independent]))).Usage.Runs[0];
        Assert.Equal(20L, run.InputTokens.Value); Assert.Equal(3, run.ProviderCounters.Count);
        Assert.DoesNotContain(run.ProviderCounters, c => c.Kind == TelemetryCounterKind.UncachedInput);
        Assert.All(run.ProviderCounters, c => { Assert.Equal(1L, c.ReportedAttempts); Assert.Equal(TelemetryMeasurementCoverage.Partial, c.Observation.Coverage); });
    }

    [Theory]
    [InlineData(ProviderTokenCounterKind.CacheRead, TokenCounterRelationship.IncludedInInput)]
    [InlineData(ProviderTokenCounterKind.CacheWrite, TokenCounterRelationship.Independent)]
    [InlineData(ProviderTokenCounterKind.UncachedInput, TokenCounterRelationship.IncludedInInput)]
    [InlineData(ProviderTokenCounterKind.Reasoning, TokenCounterRelationship.IncludedInOutput)]
    public void ProviderGroupsPreserveKnownZeroMissingAndOverflow(ProviderTokenCounterKind kind, TokenCounterRelationship relationship)
    {
        UsageObservation Value(long? count) => new(null, null, [new(kind, count, relationship)]);
        var zero = RoundTrip(Run(UsageInventoryCoverage.Complete, Value(0))).Usage.Runs[0];
        Assert.Null(zero.InputTokens.Value); // Detail-only measurements cannot invent core knowledge.
        var counter = Assert.Single(zero.ProviderCounters);
        Assert.Equal(0L, counter.Observation.Value); Assert.Equal(TelemetryMeasurementCoverage.Complete, counter.Observation.Coverage);
        counter = Assert.Single(RoundTrip(Run(UsageInventoryCoverage.Complete, Value(null))).Usage.Runs[0].ProviderCounters);
        Assert.Null(counter.Observation.Value); Assert.Equal(0L, counter.MeasuredAttempts);
        counter = Assert.Single(RoundTrip(Run(UsageInventoryCoverage.Partial, Value(long.MaxValue), Value(1), Value(null))).Usage.Runs[0].ProviderCounters);
        Assert.Null(counter.Observation.Value); Assert.Equal(TelemetryMeasurementCoverage.Overflow, counter.Observation.Coverage);
        Assert.Equal(3L, counter.ReportedAttempts); Assert.Equal(2L, counter.MeasuredAttempts);
    }

    [Fact]
    public void CallerMembershipIsBoundedDistinctCopiedAndIndependentOfPerRunKnowledge()
    {
        var outcome = Outcome(Run()); var inputs = new[] { outcome };
        var partial = AgentTelemetryMapper.MapUsage(TelemetryInventoryCoverage.Partial, inputs); inputs[0] = null!;
        Assert.Equal(outcome.ExecutionId, partial.Runs[0].ExecutionId); Assert.Equal(TelemetryMeasurementCoverage.Complete, partial.Runs[0].InputTokens.Coverage);
        Assert.Throws<ArgumentException>(() => AgentTelemetryMapper.MapUsage(TelemetryInventoryCoverage.Complete, [outcome, outcome]));
        Assert.Throws<ArgumentException>(() => AgentTelemetryMapper.MapUsage(TelemetryInventoryCoverage.Unavailable, [outcome]));
        Assert.Throws<ArgumentException>(() => AgentTelemetryMapper.MapUsage(TelemetryInventoryCoverage.Complete, [null!]));
        var many = Enumerable.Range(0, 257).Select(_ => Outcome(Run())).ToArray();
        Assert.Equal(256, AgentTelemetryMapper.MapUsage(TelemetryInventoryCoverage.Complete, many[..256]).Runs.Count);
        Assert.Throws<ArgumentException>(() => AgentTelemetryMapper.MapUsage(TelemetryInventoryCoverage.Complete, many));
        Assert.Empty(AgentTelemetryMapper.MapUsage(TelemetryInventoryCoverage.Complete, []).Runs);
        Assert.Empty(AgentTelemetryMapper.MapUsage(TelemetryInventoryCoverage.Partial, []).Runs);
        Assert.Empty(AgentTelemetryMapper.MapUsage(TelemetryInventoryCoverage.Unavailable, []).Runs);
        Assert.Throws<ArgumentException>(() => new AgentOutcome(Guid.NewGuid(), AgentTerminationReason.Completed, 0, usage: outcome.Usage));
    }
}

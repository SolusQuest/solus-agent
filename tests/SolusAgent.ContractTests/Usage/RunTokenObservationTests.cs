using SolusAgent.Api.Usage;
using Xunit;

namespace SolusAgent.ContractTests.Usage;

public sealed class RunTokenObservationTests
{
    private readonly Guid execution = Guid.NewGuid();
    private UsageAttemptObservation Attempt(UsageObservation usage, DispatchExposure exposure = DispatchExposure.Dispatched) =>
        new(execution, Guid.NewGuid(), Guid.NewGuid(), 1, exposure, usage);

    [Fact]
    public void CoverageAndKnownSubtotalsAreIndependentPerDimension()
    {
        var run = new AgentRunUsage(execution, UsageInventoryCoverage.Complete, [Attempt(new(3, 2)), Attempt(new(null, 4))]);
        Assert.Equal(3, run.InputTokens.ObservedTokens);
        Assert.Equal(TokenObservationCoverage.Partial, run.InputTokens.Coverage);
        Assert.Equal(6, run.OutputTokens.ObservedTokens);
        Assert.Equal(TokenObservationCoverage.Complete, run.OutputTokens.Coverage);
        var partial = new AgentRunUsage(execution, UsageInventoryCoverage.Partial, [Attempt(new(0, 2))]);
        Assert.Equal(0, partial.InputTokens.ObservedTokens);
        Assert.Equal(TokenObservationCoverage.Partial, partial.InputTokens.Coverage);
        Assert.Equal(TokenObservationCoverage.Partial, partial.OutputTokens.Coverage);
    }

    [Theory]
    [InlineData(UsageInventoryCoverage.Complete, TokenObservationCoverage.Complete, 0L)]
    [InlineData(UsageInventoryCoverage.Partial, TokenObservationCoverage.Unavailable, null)]
    [InlineData(UsageInventoryCoverage.Unavailable, TokenObservationCoverage.Unavailable, null)]
    public void EmptyInventoryDoesNotInventZero(UsageInventoryCoverage inventory, TokenObservationCoverage coverage, long? count)
    {
        var run = new AgentRunUsage(execution, inventory, []);
        Assert.Equal(coverage, run.InputTokens.Coverage); Assert.Equal(count, run.InputTokens.ObservedTokens);
        Assert.Equal(coverage, run.OutputTokens.Coverage); Assert.Equal(count, run.OutputTokens.ObservedTokens);
    }

    [Theory]
    [InlineData(DispatchExposure.NotDispatched, TokenObservationCoverage.Complete, 0L)]
    [InlineData(DispatchExposure.Unknown, TokenObservationCoverage.Unavailable, null)]
    [InlineData(DispatchExposure.Dispatched, TokenObservationCoverage.Unavailable, null)]
    public void KnownAbsenceOfDispatchDoesNotRewriteMissingIndividualMeasurement(DispatchExposure exposure, TokenObservationCoverage coverage, long? count)
    {
        var run = new AgentRunUsage(execution, UsageInventoryCoverage.Complete, [Attempt(new(), exposure)]);
        Assert.Equal(coverage, run.InputTokens.Coverage); Assert.Equal(count, run.InputTokens.ObservedTokens);
        Assert.Null(run.Attempts[0].Usage.InputTokens); Assert.Null(run.Attempts[0].Usage.OutputTokens);
    }

    [Fact]
    public void DetailsAccountingAndOverlapNeverContributeToCoreSum()
    {
        var facts = new UsageObservation(5, 2, [new(ProviderTokenCounterKind.CacheRead, 5, TokenCounterRelationship.IncludedInInput),
            new(ProviderTokenCounterKind.CacheWrite, 5, TokenCounterRelationship.IncludedInInput),
            new(ProviderTokenCounterKind.Reasoning, 100, TokenCounterRelationship.Independent)]);
        var attempt = new UsageAttemptObservation(execution, Guid.NewGuid(), Guid.NewGuid(), 1, DispatchExposure.Dispatched,
            facts, new(inFlightReservation: new(100, 200), conservativeUnobservedCharge: new(50, 50)));
        var run = new AgentRunUsage(execution, UsageInventoryCoverage.Complete, [attempt, Attempt(new(null, 0,
            [new(ProviderTokenCounterKind.UncachedInput, 3, TokenCounterRelationship.IncludedInInput)]))]);
        Assert.Equal(5, run.InputTokens.ObservedTokens); Assert.Equal(TokenObservationCoverage.Partial, run.InputTokens.Coverage);
        Assert.Equal(2, run.OutputTokens.ObservedTokens); Assert.Equal(TokenObservationCoverage.Complete, run.OutputTokens.Coverage);
        Assert.Equal(3, run.Attempts[0].Usage.ProviderCounters.Count);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OverflowIsOrderIndependentAndPreservesAllValuesEvenWithMissingMeasurements(bool reverse)
    {
        var attempts = new[] { Attempt(new(long.MaxValue - 1, 1)), Attempt(new(null, 2)), Attempt(new(2, 3)) };
        if (reverse) Array.Reverse(attempts);
        var run = new AgentRunUsage(execution, UsageInventoryCoverage.Complete, attempts);
        Assert.Equal(TokenObservationCoverage.Overflow, run.InputTokens.Coverage); Assert.Null(run.InputTokens.ObservedTokens);
        Assert.Equal(6, run.OutputTokens.ObservedTokens); Assert.Equal(TokenObservationCoverage.Complete, run.OutputTokens.Coverage);
        Assert.Equal(3, run.Attempts.Count); Assert.Contains(run.Attempts, a => a.Usage.InputTokens == long.MaxValue - 1);
        var boundary = new AgentRunUsage(execution, UsageInventoryCoverage.Complete, [Attempt(new(long.MaxValue - 1)), Attempt(new(1))]);
        Assert.Equal(long.MaxValue, boundary.InputTokens.ObservedTokens); Assert.Equal(TokenObservationCoverage.Complete, boundary.InputTokens.Coverage);
    }

    [Fact]
    public void ValidatedRetryInventoryIsCountedOnceAndSnapshotsAreImmutable()
    {
        var logical = Guid.NewGuid();
        var attempts = new List<UsageAttemptObservation> { new(execution, logical, Guid.NewGuid(), 1, DispatchExposure.Dispatched, new(3, 2)),
            new(execution, logical, Guid.NewGuid(), 2, DispatchExposure.Dispatched, new(4, 1)) };
        var run = new AgentRunUsage(execution, UsageInventoryCoverage.Complete, attempts);
        attempts.Clear();
        Assert.Equal(7, run.InputTokens.ObservedTokens); Assert.Equal(3, run.OutputTokens.ObservedTokens); Assert.Equal(2, run.Attempts.Count);
        Assert.Throws<ArgumentException>(() => new AgentRunUsage(Guid.NewGuid(), UsageInventoryCoverage.Complete, run.Attempts));
        Assert.Throws<ArgumentException>(() => new AgentRunUsage(execution, UsageInventoryCoverage.Complete, [run.Attempts[0], run.Attempts[0]]));
        Assert.Throws<NotSupportedException>(() => ((IList<UsageAttemptObservation>)run.Attempts).Clear());
    }
}

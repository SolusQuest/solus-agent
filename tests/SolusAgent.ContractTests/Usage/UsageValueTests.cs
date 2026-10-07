using System.Reflection;
using System.Text.Json;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using Xunit;

namespace SolusAgent.ContractTests.Usage;

public sealed class UsageValueTests
{
    private static readonly Guid Execution = Guid.NewGuid();
    private static readonly Guid Logical = Guid.NewGuid();

    [Fact]
    public void KnownZeroMissingCoreAndOptionalFactsHaveIndependentAvailability()
    {
        Assert.Equal(UsageCompleteness.Complete, new UsageObservation(0, 0).Completeness);
        Assert.Equal(UsageCompleteness.Partial, new UsageObservation(0).Completeness);
        Assert.Equal(UsageCompleteness.Unavailable, new UsageObservation().Completeness);
        var unknownDetail = new ProviderTokenCounter(ProviderTokenCounterKind.CacheRead, null, TokenCounterRelationship.IncludedInInput);
        Assert.Equal(UsageCompleteness.Unavailable, new UsageObservation(providerCounters: [unknownDetail]).Completeness);
        var uncached = new ProviderTokenCounter(ProviderTokenCounterKind.UncachedInput, 3, TokenCounterRelationship.IncludedInInput);
        var detailOnly = new UsageObservation(providerCounters: [uncached]);
        Assert.Equal(UsageCompleteness.Partial, detailOnly.Completeness);
        Assert.Null(detailOnly.InputTokens);
        Assert.Null(detailOnly.OutputTokens);
        Assert.Equal(ProviderTokenCounterKind.UncachedInput, Assert.Single(detailOnly.ProviderCounters).Kind);
        var all = new UsageObservation(10, 2, [uncached, new(ProviderTokenCounterKind.CacheRead, 6, TokenCounterRelationship.IncludedInInput)]);
        Assert.Equal(10, all.InputTokens);
        Assert.Equal(2, all.ProviderCounters.Count);
        Assert.DoesNotContain(all.ProviderCounters, item => item.Kind == ProviderTokenCounterKind.CacheWrite);
    }

    [Fact]
    public void RelationshipsValidateKnownParentsWithoutInventingPartitionsOrSummingOverlap()
    {
        var overlap = new UsageObservation(5, 2, [new(ProviderTokenCounterKind.CacheRead, 5, TokenCounterRelationship.IncludedInInput), new(ProviderTokenCounterKind.CacheWrite, 5, TokenCounterRelationship.IncludedInInput), new(ProviderTokenCounterKind.Reasoning, 100, TokenCounterRelationship.Independent)]);
        Assert.Equal(5, overlap.InputTokens);
        Assert.Equal(2, overlap.OutputTokens);
        Assert.Throws<ArgumentException>(() => new UsageObservation(5, 2, [new(ProviderTokenCounterKind.CacheRead, 6, TokenCounterRelationship.IncludedInInput)]));
        Assert.Throws<ArgumentException>(() => new UsageObservation(5, 2, [new(ProviderTokenCounterKind.Reasoning, 3, TokenCounterRelationship.IncludedInOutput)]));
        Assert.Single(new UsageObservation(providerCounters: [new(ProviderTokenCounterKind.Reasoning, 3, TokenCounterRelationship.IncludedInOutput)]).ProviderCounters);
    }

    [Fact]
    public void InvalidNumericEnumsAndDuplicateDetailsRejectSafely()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsageObservation(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsageObservation(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderTokenCounter((ProviderTokenCounterKind)99, 0, TokenCounterRelationship.Independent));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderTokenCounter(ProviderTokenCounterKind.CacheRead, -1, TokenCounterRelationship.Independent));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderTokenCounter(ProviderTokenCounterKind.CacheRead, 0, (TokenCounterRelationship)99));
        var counter = new ProviderTokenCounter(ProviderTokenCounterKind.CacheRead, 0, TokenCounterRelationship.Independent);
        Assert.Throws<ArgumentException>(() => new UsageObservation(providerCounters: [counter, counter]));
        Assert.Throws<ArgumentException>(() => new UsageObservation(providerCounters: [null!]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentUsageLimits(maximumLogicalCalls: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentUsageLimits(maximumPhysicalDispatches: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentUsageLimits(inputTokenThreshold: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentUsageLimits(outputTokenThreshold: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsageTokenAmounts(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsageTokenAmounts(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsageCostEstimate(-1, "USD"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsageAccounting((UsageSettlement)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentRunUsage(Execution, (UsageInventoryCoverage)99, []));
    }

    [Theory]
    [InlineData(DispatchExposure.Unknown)]
    [InlineData(DispatchExposure.NotDispatched)]
    public void PositiveActualCoreOrDetailConsumptionProvesDispatch(DispatchExposure exposure)
    {
        Assert.Throws<ArgumentException>(() => Attempt(1, exposure, new UsageObservation(1)));
        Assert.Throws<ArgumentException>(() => Attempt(1, exposure, new UsageObservation(outputTokens: 1)));
        Assert.Throws<ArgumentException>(() => Attempt(1, exposure, new UsageObservation(providerCounters: [new(ProviderTokenCounterKind.UncachedInput, 1, TokenCounterRelationship.Independent)])));
        Assert.Equal(exposure, Attempt(1, exposure, new UsageObservation(0, 0)).Exposure);
        Assert.Equal(UsageCompleteness.Unavailable, Attempt(1, exposure, new UsageObservation()).Usage.Completeness);
    }

    [Fact]
    public void ExposureReservationSettlementChargeAndEstimateRemainDistinct()
    {
        var accounting = new UsageAccounting(UsageSettlement.Unsettled, new UsageTokenAmounts(20, 10), new UsageTokenAmounts(3, 4), new UsageCostEstimate(0.25m, "USD"));
        var unknown = Attempt(1, DispatchExposure.Unknown, new UsageObservation(), accounting);
        Assert.Equal(UsageCompleteness.Unavailable, unknown.Usage.Completeness);
        Assert.Equal(20, unknown.Accounting!.InFlightReservation!.InputTokens);
        Assert.Equal(3, unknown.Accounting.ConservativeUnobservedCharge!.InputTokens);
        Assert.Equal(0.25m, unknown.Accounting.EstimatedCost!.Amount);
        Assert.Throws<ArgumentException>(() => Attempt(1, DispatchExposure.NotDispatched, new UsageObservation(0, 0), accounting));
        Assert.NotNull(Attempt(1, DispatchExposure.NotDispatched, new UsageObservation(0, 0), new UsageAccounting(inFlightReservation: new UsageTokenAmounts(20, 10))).Accounting);
        Assert.NotNull(Attempt(1, DispatchExposure.Unknown, new UsageObservation(0, 0), accounting).Accounting);
        Assert.Throws<ArgumentException>(() => new UsageAccounting(UsageSettlement.Settled, new UsageTokenAmounts(0, 0)));
        var settled = Attempt(1, DispatchExposure.Dispatched, new UsageObservation(), new UsageAccounting(UsageSettlement.Settled));
        Assert.Equal(UsageCompleteness.Unavailable, settled.Usage.Completeness);
        Assert.Null(settled.Accounting!.ConservativeUnobservedCharge);
        Assert.Null(settled.Accounting.EstimatedCost);
    }

    [Fact]
    public void InventoryRejectsDuplicateOrCrossRunAttemptsAndIncompleteOrdinals()
    {
        var first = Attempt(1);
        var second = Attempt(2);
        Assert.Equal(2, new AgentRunUsage(Execution, UsageInventoryCoverage.Complete, [second, first]).Attempts.Count);
        Assert.Throws<ArgumentException>(() => new AgentRunUsage(Execution, UsageInventoryCoverage.Complete, [second]));
        Assert.Throws<ArgumentException>(() => new AgentRunUsage(Execution, UsageInventoryCoverage.Complete, [first, Attempt(3)]));
        Assert.Single(new AgentRunUsage(Execution, UsageInventoryCoverage.Partial, [second]).Attempts);
        Assert.Throws<ArgumentException>(() => new AgentRunUsage(Execution, UsageInventoryCoverage.Partial, [first, first]));
        Assert.Throws<ArgumentException>(() => new AgentRunUsage(Execution, UsageInventoryCoverage.Partial, [first, Attempt(1)]));
        var reused = new UsageAttemptObservation(Execution, Guid.NewGuid(), first.PhysicalAttemptId, 1, DispatchExposure.Dispatched, new UsageObservation());
        Assert.Throws<ArgumentException>(() => new AgentRunUsage(Execution, UsageInventoryCoverage.Partial, [first, reused]));
        Assert.Throws<ArgumentException>(() => new AgentRunUsage(Guid.NewGuid(), UsageInventoryCoverage.Complete, [first]));
        Assert.Throws<ArgumentException>(() => new AgentRunUsage(Execution, UsageInventoryCoverage.Unavailable, [first]));
        Assert.Throws<ArgumentException>(() => new AgentRunUsage(Execution, UsageInventoryCoverage.Partial, [null!]));
        Assert.Throws<ArgumentNullException>(() => new AgentRunUsage(Execution, UsageInventoryCoverage.Complete, null!));
        Assert.Throws<ArgumentException>(() => new AgentRunUsage(Guid.Empty, UsageInventoryCoverage.Complete, []));
        Assert.Throws<ArgumentException>(() => new UsageAttemptObservation(Guid.Empty, Logical, Guid.NewGuid(), 1, DispatchExposure.Unknown, new UsageObservation()));
        Assert.Throws<ArgumentException>(() => new UsageAttemptObservation(Execution, Guid.Empty, Guid.NewGuid(), 1, DispatchExposure.Unknown, new UsageObservation()));
        Assert.Throws<ArgumentException>(() => new UsageAttemptObservation(Execution, Logical, Guid.Empty, 1, DispatchExposure.Unknown, new UsageObservation()));
        Assert.Throws<ArgumentOutOfRangeException>(() => Attempt(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Attempt(1, (DispatchExposure)99));
        Assert.Throws<ArgumentNullException>(() => new UsageAttemptObservation(Execution, Logical, Guid.NewGuid(), 1, DispatchExposure.Unknown, null!));
    }

    [Fact]
    public void CopiedInventoriesAndDetailsStayImmutableAndCrossRunLinksReject()
    {
        var counters = new List<ProviderTokenCounter> { new(ProviderTokenCounterKind.UncachedInput, 3, TokenCounterRelationship.IncludedInInput) };
        var usage = new UsageObservation(providerCounters: counters);
        counters.Clear();
        Assert.Single(usage.ProviderCounters);
        Assert.Throws<NotSupportedException>(() => ((IList<ProviderTokenCounter>)usage.ProviderCounters).Clear());
        var attempts = new List<UsageAttemptObservation> { Attempt(1) };
        var run = new AgentRunUsage(Execution, UsageInventoryCoverage.Complete, attempts);
        attempts.Clear();
        Assert.Single(run.Attempts);
        Assert.Throws<NotSupportedException>(() => ((IList<UsageAttemptObservation>)run.Attempts).Clear());
        Assert.Throws<ArgumentException>(() => new AgentProgress(Guid.NewGuid(), 0, run));
        Assert.Throws<ArgumentException>(() => new AgentOutcome(Guid.NewGuid(), AgentTerminationReason.Completed, 0, usage: run));
        Assert.Throws<ArgumentException>(() => new AgentOutcome(Execution, AgentTerminationReason.UnsupportedCapability, 0, AgentCapability.DurationLimit, usage: run));
        var absent = new AgentRunUsage(Execution, UsageInventoryCoverage.Unavailable, []);
        var none = new AgentRunUsage(Execution, UsageInventoryCoverage.Complete, []);
        Assert.NotEqual(absent.Coverage, none.Coverage);
        Assert.Empty(absent.Attempts);
        Assert.Empty(none.Attempts);
    }

    [Theory]
    [InlineData("restricted-canary")]
    [InlineData("usd")]
    [InlineData("US")]
    [InlineData("")]
    [InlineData(null)]
    public void CurrencyValidationNeverEchoesRejectedPayload(string? currency)
    {
        var exception = Assert.Throws<ArgumentException>(() => new UsageCostEstimate(0, currency!));
        Assert.DoesNotContain("restricted-canary", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryUsageSurfacesHaveOnlyClosedMetadataAndNumericValues()
    {
        var types = typeof(UsageObservation).Assembly.GetExportedTypes().Where(type => type.Namespace == typeof(UsageObservation).Namespace && !type.IsEnum).ToHashSet();
        var allowedCollections = new[] { typeof(IReadOnlyList<ProviderTokenCounter>), typeof(IReadOnlyList<UsageAttemptObservation>) };
        foreach (var type in types)
        {
            Assert.All(type.GetProperties(BindingFlags.Public | BindingFlags.Instance), property =>
            {
                var valueType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                Assert.True(valueType.IsEnum || valueType == typeof(Guid) || valueType == typeof(int) || valueType == typeof(long) || valueType == typeof(decimal) || types.Contains(valueType) || allowedCollections.Contains(valueType) || type == typeof(UsageCostEstimate) && property.Name == nameof(UsageCostEstimate.Currency) && valueType == typeof(string), $"Unexpected ordinary property: {type.Name}.{property.Name}");
                Assert.Null(property.SetMethod);
            });
        }
        Assert.DoesNotContain("restricted-canary", JsonSerializer.Serialize(new UsageObservation()));
    }

    private static UsageAttemptObservation Attempt(int ordinal, DispatchExposure exposure = DispatchExposure.Dispatched, UsageObservation? usage = null, UsageAccounting? accounting = null) => new(Execution, Logical, Guid.NewGuid(), ordinal, exposure, usage ?? new UsageObservation(), accounting);
}

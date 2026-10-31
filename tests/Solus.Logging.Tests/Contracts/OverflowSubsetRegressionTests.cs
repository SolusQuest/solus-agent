using System.Text.Json.Nodes;
using Solus.Telemetry.Contracts;
using Xunit;

namespace Solus.Logging.Tests.Contracts;

public sealed class OverflowSubsetRegressionTests
{
    public static IEnumerable<object[]> ParentCoverageCases()
    {
        foreach (var relationship in Enum.GetValues<TelemetryCounterRelationship>())
            foreach (var coverage in Enum.GetValues<TelemetryMeasurementCoverage>())
                yield return [relationship, coverage];
    }

    [Theory]
    [MemberData(nameof(ParentCoverageCases))]
    public void B03OverflowHonorsOnlyItsMatchingCompleteParent(TelemetryCounterRelationship relationship, TelemetryMeasurementCoverage parentCoverage)
    {
        var reject = relationship != TelemetryCounterRelationship.Independent && parentCoverage == TelemetryMeasurementCoverage.Complete;
        Verify(relationship, Parent(parentCoverage, 5), reject);
    }

    [Theory]
    [InlineData(TelemetryCounterRelationship.IncludedInInput, 0L)]
    [InlineData(TelemetryCounterRelationship.IncludedInOutput, 0L)]
    [InlineData(TelemetryCounterRelationship.IncludedInInput, long.MaxValue)]
    [InlineData(TelemetryCounterRelationship.IncludedInOutput, long.MaxValue)]
    public void B03OverflowCannotFitEvenTheLargestCompleteInt64Parent(TelemetryCounterRelationship relationship, long parent)
        => Verify(relationship, SyntheticTelemetry.Known(parent), reject: true);

    private static TelemetryTokenObservation Parent(TelemetryMeasurementCoverage coverage, long value) =>
        new(coverage is TelemetryMeasurementCoverage.Complete or TelemetryMeasurementCoverage.Partial ? value : null, coverage);

    private static void Verify(TelemetryCounterRelationship relationship, TelemetryTokenObservation parent, bool reject)
    {
        var overflow = new TelemetryTokenObservation(null, TelemetryMeasurementCoverage.Overflow);
        if (reject) Assert.Throws<ArgumentException>(() => Run(relationship, parent, overflow));
        else
        {
            var run = Run(relationship, parent, overflow);
            var read = SyntheticTelemetry.Consume(SyntheticTelemetry.Wire(SyntheticTelemetry.Produce(new(TelemetryInventoryCoverage.Complete, [run]))));
            var fact = Assert.Single(Assert.Single(read.Usage.Runs).ProviderCounters);
            Assert.Null(fact.Observation.Value);
            Assert.Equal(TelemetryMeasurementCoverage.Overflow, fact.Observation.Coverage);
        }

        // Start from an admissible unknown counter, then submit the reported wire trigger.
        var baseline = Run(relationship, parent, SyntheticTelemetry.Missing());
        var node = JsonNode.Parse(SyntheticTelemetry.Wire(SyntheticTelemetry.Produce(new(TelemetryInventoryCoverage.Complete, [baseline]))))!.AsObject();
        var counter = node["usage"]!["runs"]![0]!["providerCounters"]![0]!;
        counter["observation"]!["coverage"] = "overflow";
        counter["measuredAttempts"] = "2";
        if (reject) TelemetryContractTests.Rejected(node.ToJsonString());
        else Assert.Equal(TelemetryMeasurementCoverage.Overflow, SyntheticTelemetry.Consume(node.ToJsonString()).Usage.Runs[0].ProviderCounters[0].Observation.Coverage);
    }

    private static TelemetryRunSummary Run(TelemetryCounterRelationship relationship, TelemetryTokenObservation parent, TelemetryTokenObservation detail)
    {
        var input = relationship == TelemetryCounterRelationship.IncludedInOutput ? SyntheticTelemetry.Known(5) : parent;
        var output = relationship == TelemetryCounterRelationship.IncludedInOutput ? parent : SyntheticTelemetry.Known(5);
        return new(Guid.NewGuid(), TelemetryInventoryCoverage.Complete, input, output, 1, 2,
            providerCounters: [new(TelemetryCounterKind.CacheRead, relationship, detail, 2,
                detail.Coverage == TelemetryMeasurementCoverage.Unavailable ? 0 : 2)]);
    }
}

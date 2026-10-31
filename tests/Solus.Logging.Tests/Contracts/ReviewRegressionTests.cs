using System.Text.Json.Nodes;
using Solus.Telemetry.Contracts;
using Xunit;

namespace Solus.Logging.Tests.Contracts;

public sealed class ReviewRegressionTests
{
    // Run with TZ=UTC as well as ordinary CI: offset-free text must never acquire local-zone authority.
    [Theory]
    [InlineData("2026-01-02T03:04:05", false)]
    [InlineData("2026-01-02T03:04:05.1234567", false)]
    [InlineData("2026-01-02", false)]
    [InlineData("2026-01-02T03:04:05-00:00", false)]
    [InlineData("2026-01-02T03:04:05+01:00", false)]
    [InlineData("2026-01-02T03:04:05.12345678Z", false)]
    [InlineData("2026-01-02T03:04:05.12345678+00:00", false)]
    [InlineData("2026-01-02T03:04:05Z", true)]
    [InlineData("2026-01-02T03:04:05.1234567Z", true)]
    [InlineData("2026-01-02T03:04:05+00:00", true)]
    public void B01RequiresExplicitKnownUtcInsteadOfHostLocalInterpretation(string timestamp, bool accepted)
    {
        var node = JsonNode.Parse(SyntheticTelemetry.Wire(SyntheticTelemetry.Produce()))!.AsObject();
        node["occurredAt"] = timestamp;
        var result = TelemetryJson.TryDeserialize(node.ToJsonString(), out var record, out var diagnostic);
        Assert.Equal(accepted, result);
        if (accepted) { Assert.NotNull(record); Assert.Equal(TimeSpan.Zero, record.OccurredAt.Offset); }
        else { Assert.Null(record); Assert.Equal(TelemetryDiagnostic.InvalidInput, diagnostic); }
    }

    [Fact]
    public void B02SameKindGroupsCannotInventAdditionalPhysicalAttempts()
    {
        TelemetryProviderCounter[] groups =
        [
            new(TelemetryCounterKind.CacheRead, TelemetryCounterRelationship.IncludedInInput, new(1, TelemetryMeasurementCoverage.Partial), 1, 1),
            new(TelemetryCounterKind.CacheRead, TelemetryCounterRelationship.Independent, new(1, TelemetryMeasurementCoverage.Partial), 1, 1),
        ];
        Assert.Throws<ArgumentException>(() => Run(1, groups));
        var valid = Run(2, groups);
        var wire = SyntheticTelemetry.Wire(SyntheticTelemetry.Produce(new(TelemetryInventoryCoverage.Complete, [valid])));
        var node = JsonNode.Parse(wire)!.AsObject();
        node["usage"]!["runs"]![0]!["physicalAttempts"] = "1";
        TelemetryContractTests.Rejected(node.ToJsonString());
        Assert.Equal(2, SyntheticTelemetry.Consume(wire).Usage.Runs[0].ProviderCounters.Count);
    }

    [Fact]
    public void B02CombinedCountOverflowRejectsWithoutWrapping()
    {
        TelemetryProviderCounter[] groups =
        [
            new(TelemetryCounterKind.CacheRead, TelemetryCounterRelationship.IncludedInInput, SyntheticTelemetry.Missing(), long.MaxValue, 0),
            new(TelemetryCounterKind.CacheRead, TelemetryCounterRelationship.Independent, SyntheticTelemetry.Missing(), 1, 0),
        ];
        Assert.Throws<ArgumentException>(() => Run(long.MaxValue, groups));
    }

    [Theory]
    [InlineData(TelemetryCounterRelationship.IncludedInInput)]
    [InlineData(TelemetryCounterRelationship.IncludedInOutput)]
    public void B03EveryKnownSubsetIsBoundedByItsCompleteParent(TelemetryCounterRelationship relationship)
    {
        TelemetryProviderCounter[] Excess() => [new(TelemetryCounterKind.CacheRead, relationship, new(6, TelemetryMeasurementCoverage.Partial), 1, 1)];
        Assert.Throws<ArgumentException>(() => Run(2, Excess()));
        var valid = Run(2, [new(TelemetryCounterKind.CacheRead, relationship, new(5, TelemetryMeasurementCoverage.Partial), 1, 1)]);
        var wire = SyntheticTelemetry.Wire(SyntheticTelemetry.Produce(new(TelemetryInventoryCoverage.Complete, [valid])));
        var node = JsonNode.Parse(wire)!.AsObject();
        node["usage"]!["runs"]![0]!["providerCounters"]![0]!["observation"]!["value"] = "6";
        TelemetryContractTests.Rejected(node.ToJsonString());
        // Incomplete parent subtotals may cover different attempts and are not a valid upper bound.
        Assert.NotNull(new TelemetryRunSummary(Guid.NewGuid(), TelemetryInventoryCoverage.Partial,
            new(5, TelemetryMeasurementCoverage.Partial), new(5, TelemetryMeasurementCoverage.Partial), 1, 2, providerCounters: Excess()));
    }

    private static TelemetryRunSummary Run(long physical, IReadOnlyList<TelemetryProviderCounter> counters) =>
        new(Guid.NewGuid(), TelemetryInventoryCoverage.Complete, SyntheticTelemetry.Known(5), SyntheticTelemetry.Known(5), 1, physical, providerCounters: counters);
}

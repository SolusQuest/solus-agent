using System.Text.Json;
using Solus.Telemetry.Contracts;
using Xunit;

namespace Solus.Logging.Tests.Contracts;

internal static class SyntheticTelemetry
{
    internal static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    internal static TelemetryTokenObservation Known(long value = 0) => new(value, TelemetryMeasurementCoverage.Complete);
    internal static TelemetryTokenObservation Missing() => new(null, TelemetryMeasurementCoverage.Unavailable);

    internal static TelemetryRunSummary Run(Guid? id = null, bool rich = false)
    {
        var providers = rich ? Enum.GetValues<TelemetryCounterKind>().SelectMany(kind => Enum.GetValues<TelemetryCounterRelationship>()
            .Where(relationship => kind != TelemetryCounterKind.UncachedInput || relationship != TelemetryCounterRelationship.IncludedInOutput)
            .Select(relationship => new TelemetryProviderCounter(kind, relationship, Known(1), 2, 2))).ToArray() : [];
        var accounting = rich ? Enum.GetValues<TelemetryAccountingSource>().SelectMany(source => Enum.GetValues<TelemetryAccountingKind>()
            .Select(kind => new TelemetryAccountingObservation(source, kind,
                new(kind == TelemetryAccountingKind.RemainingAllowance ? -1 : 0, TelemetryAccountingKnowledge.Known),
                new(0, TelemetryAccountingKnowledge.Known)))).ToArray() : [];
        return new(id ?? Guid.NewGuid(), TelemetryInventoryCoverage.Complete, Known(long.MaxValue), Known(4),
            1, 2, new(3, 2, 0, 1), providers, accounting, rich ? TelemetrySettlement.Mixed : null,
            rich ? [new(TelemetryAccountingSource.ProviderClaim, "USD", decimal.MaxValue, TelemetryAccountingKnowledge.Provisional)] : []);
    }

    // The producer chooses current membership and explicitly names historical accounting in its own extension.
    internal static TelemetryRecord Produce(TelemetryUsage? usage = null, JsonElement? extensions = null, string product = "scribe") =>
        new(Guid.Parse("20000000-0000-0000-0000-000000000001"), "synthetic-invocation-1",
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), product, "1.0-test",
            new("synthetic/repository", "9007199254740993", 2, "test", "matrix-a"), new(TelemetryOutcomeKind.Partial, "synthetic.partial"),
            usage ?? new(TelemetryInventoryCoverage.Complete, [Run(rich: true),
                new(Guid.NewGuid(), TelemetryInventoryCoverage.Partial, new(0, TelemetryMeasurementCoverage.Partial), Missing(), 1, 1)]),
            extensions ?? Json("{\"" + product + "\":{\"historicalAccounting\":{\"inputTokens\":\"7\"}},\"caller\":{\"team\":\"test\"}}"));

    internal static string Wire(TelemetryRecord record)
    {
        Assert.True(TelemetryJson.TrySerialize(record, out var json, out var diagnostic), diagnostic.ToString());
        Assert.NotNull(json); return json;
    }

    // A consumer uses only the neutral public JSON entrypoint, not producer object identity or Agent types.
    internal static TelemetryRecord Consume(string json)
    {
        Assert.True(TelemetryJson.TryDeserialize(json, out var record, out var diagnostic), diagnostic.ToString());
        Assert.NotNull(record); return record;
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Solus.Telemetry.Contracts;
using Xunit;

namespace Solus.Logging.Tests.Contracts;

public sealed class TelemetryContractTests
{
    [Theory]
    [InlineData("scribe")]
    [InlineData("apr")]
    [InlineData("another-product")]
    public void ProducerAndIndependentConsumerRetainCurrentRunsAndEveryPublicFact(string product)
    {
        var source = SyntheticTelemetry.Produce(product: product);
        var json = SyntheticTelemetry.Wire(source);
        var read = SyntheticTelemetry.Consume(json);
        Assert.Equal(source.EventId, read.EventId); Assert.Equal(source.InvocationId, read.InvocationId);
        Assert.Equal(source.OccurredAt, read.OccurredAt); Assert.Equal(source.Product, read.Product);
        Assert.Equal(source.ProductVersion, read.ProductVersion); Assert.Equal(source.Execution.WorkflowRunId, read.Execution.WorkflowRunId);
        Assert.Equal(TelemetryOutcomeKind.Partial, read.Outcome.Kind);
        Assert.Equal(2, read.Usage.Runs.Count);
        var run = read.Usage.Runs[0];
        Assert.Equal(long.MaxValue, run.InputTokens.Value);
        Assert.Equal(11, run.ProviderCounters.Count); Assert.Equal(14, run.Accounting.Count);
        Assert.Equal(TelemetrySettlement.Mixed, run.ProviderSettlement);
        Assert.Equal(decimal.MaxValue, Assert.Single(run.EstimatedCosts).Amount);
        Assert.Equal(-1, run.Accounting.First(a => a.Kind == TelemetryAccountingKind.RemainingAllowance).Input.Value);
        Assert.Equal("7", read.Extensions.GetProperty(product).GetProperty("historicalAccounting").GetProperty("inputTokens").GetString());
        Assert.Equal(TelemetryMeasurementCoverage.Partial, read.Usage.Runs[1].InputTokens.Coverage);
        Assert.Null(read.Usage.Runs[1].OutputTokens.Value);
        Assert.Equal(json, SyntheticTelemetry.Wire(read));
        using var wire = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.String, wire.RootElement.GetProperty("usage").GetProperty("runs")[0].GetProperty("inputTokens").GetProperty("value").ValueKind);
        Assert.Contains("\"9223372036854775807\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("attempts\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyMembershipEmptyAttemptsUnavailableAndPartialZeroRemainDifferent()
    {
        var empty = SyntheticTelemetry.Consume(SyntheticTelemetry.Wire(SyntheticTelemetry.Produce(new(TelemetryInventoryCoverage.Complete, []))));
        Assert.Empty(empty.Usage.Runs); Assert.Equal(TelemetryInventoryCoverage.Complete, empty.Usage.InventoryCoverage);
        var zero = new TelemetryRunSummary(Guid.NewGuid(), TelemetryInventoryCoverage.Complete, SyntheticTelemetry.Known(), SyntheticTelemetry.Known(), 0, 0);
        var unavailable = new TelemetryRunSummary(Guid.NewGuid(), TelemetryInventoryCoverage.Unavailable, SyntheticTelemetry.Missing(), SyntheticTelemetry.Missing());
        var partial = new TelemetryRunSummary(Guid.NewGuid(), TelemetryInventoryCoverage.Partial,
            new(0, TelemetryMeasurementCoverage.Partial), new(null, TelemetryMeasurementCoverage.Overflow));
        var record = SyntheticTelemetry.Produce(new(TelemetryInventoryCoverage.Partial, [zero, unavailable, partial]));
        var read = SyntheticTelemetry.Consume(SyntheticTelemetry.Wire(record));
        Assert.Equal(0, read.Usage.Runs[0].InputTokens.Value);
        Assert.Null(read.Usage.Runs[1].InputTokens.Value);
        Assert.Equal(TelemetryMeasurementCoverage.Partial, read.Usage.Runs[2].InputTokens.Coverage);
        Assert.Equal(TelemetryMeasurementCoverage.Overflow, read.Usage.Runs[2].OutputTokens.Coverage);
        Assert.Throws<ArgumentException>(() => new TelemetryTokenObservation(null, TelemetryMeasurementCoverage.Complete));
        Assert.Throws<ArgumentException>(() => new TelemetryTokenObservation(0, TelemetryMeasurementCoverage.Unavailable));
        Assert.Throws<ArgumentException>(() => new TelemetryTokenObservation(-1, TelemetryMeasurementCoverage.Partial));
        Assert.Throws<ArgumentException>(() => new TelemetryRunSummary(Guid.NewGuid(), TelemetryInventoryCoverage.Partial, SyntheticTelemetry.Known(), SyntheticTelemetry.Missing()));
        Assert.Throws<ArgumentException>(() => new TelemetryRunSummary(Guid.NewGuid(), TelemetryInventoryCoverage.Complete, SyntheticTelemetry.Missing(), SyntheticTelemetry.Missing(), 0, 0));
        Assert.Throws<ArgumentException>(() => new TelemetryUsage(TelemetryInventoryCoverage.Unavailable, [zero]));
        Assert.Throws<ArgumentException>(() => new TelemetryUsage(TelemetryInventoryCoverage.Complete, [zero, zero]));
    }

    [Fact]
    public void ProviderGroupsKeepDifferentRelationshipsAndAccountingCannotAlterMeasuredUsage()
    {
        var facts = new[]
        {
            new TelemetryProviderCounter(TelemetryCounterKind.CacheRead, TelemetryCounterRelationship.IncludedInInput, new(2, TelemetryMeasurementCoverage.Partial), 1, 1),
            new TelemetryProviderCounter(TelemetryCounterKind.CacheRead, TelemetryCounterRelationship.Independent, new(null, TelemetryMeasurementCoverage.Unavailable), 1, 0),
        };
        var run = new TelemetryRunSummary(Guid.NewGuid(), TelemetryInventoryCoverage.Complete, SyntheticTelemetry.Known(5), SyntheticTelemetry.Known(), 1, 2,
            providerCounters: facts, accounting: [new(TelemetryAccountingSource.RunLedger, TelemetryAccountingKind.ConservativeCharge,
                new(long.MaxValue, TelemetryAccountingKnowledge.Provisional), new(0, TelemetryAccountingKnowledge.Unknown))]);
        var read = SyntheticTelemetry.Consume(SyntheticTelemetry.Wire(SyntheticTelemetry.Produce(new(TelemetryInventoryCoverage.Complete, [run])))).Usage.Runs[0];
        Assert.Equal(5, read.InputTokens.Value); Assert.Equal(2, read.ProviderCounters.Count);
        Assert.Null(read.ProviderCounters[1].Observation.Value); Assert.Equal(long.MaxValue, read.Accounting[0].Input.Value);
        Assert.Throws<ArgumentException>(() => new TelemetryRunSummary(Guid.NewGuid(), TelemetryInventoryCoverage.Complete, SyntheticTelemetry.Known(5), SyntheticTelemetry.Known(), 1, 2, providerCounters: [facts[0], facts[0]]));
        Assert.Throws<ArgumentException>(() => new TelemetryProviderCounter(TelemetryCounterKind.UncachedInput, TelemetryCounterRelationship.IncludedInOutput, SyntheticTelemetry.Known(), 1, 1));
        Assert.Throws<ArgumentException>(() => new TelemetryProviderCounter(TelemetryCounterKind.CacheRead, TelemetryCounterRelationship.Independent, SyntheticTelemetry.Known(), 2, 1));
        Assert.Throws<ArgumentException>(() => new TelemetryRunSummary(Guid.NewGuid(), TelemetryInventoryCoverage.Complete, SyntheticTelemetry.Known(5), SyntheticTelemetry.Known(), 1, 2,
            providerCounters: [new(TelemetryCounterKind.CacheRead, TelemetryCounterRelationship.IncludedInInput, SyntheticTelemetry.Known(6), 2, 2)]));
        Assert.Throws<ArgumentException>(() => new TelemetryAccountingObservation(TelemetryAccountingSource.RunLedger, TelemetryAccountingKind.Measured, new(-1, TelemetryAccountingKnowledge.Known), new(0, TelemetryAccountingKnowledge.Known)));
    }

    [Fact]
    public void SnapshotsSurviveArrayMutationAndSourceJsonDisposalAndIdentityRemainsProducerOwned()
    {
        var providers = new[] { new TelemetryProviderCounter(TelemetryCounterKind.Reasoning, TelemetryCounterRelationship.Independent, SyntheticTelemetry.Known(1), 1, 1) };
        var runs = new[] { new TelemetryRunSummary(Guid.NewGuid(), TelemetryInventoryCoverage.Complete, SyntheticTelemetry.Known(1), SyntheticTelemetry.Known(1), 1, 1, providerCounters: providers) };
        TelemetryRecord record;
        using (var document = JsonDocument.Parse("{\"scribe\":{\"nested\":[1,true,null,{\"text\":\"synthetic\"}]}}"))
            record = SyntheticTelemetry.Produce(new(TelemetryInventoryCoverage.Complete, runs), document.RootElement);
        var before = SyntheticTelemetry.Wire(record);
        providers[0] = new(TelemetryCounterKind.CacheRead, TelemetryCounterRelationship.Independent, SyntheticTelemetry.Known(2), 1, 1);
        runs[0] = SyntheticTelemetry.Run();
        Assert.Equal(before, SyntheticTelemetry.Wire(record));
        Assert.Equal(record.EventId, SyntheticTelemetry.Consume(before).EventId);
        var nextInvocation = new TelemetryRecord(Guid.NewGuid(), "second-invocation", record.OccurredAt, record.Product,
            record.ProductVersion, record.Execution, record.Outcome, record.Usage, record.Extensions);
        Assert.NotEqual(record.EventId, nextInvocation.EventId);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("9007199254740993")]
    [InlineData("9223372036854775807")]
    public void CanonicalInt64StringsAreExact(string value)
    {
        var node = Node(); node["usage"]!["runs"]![0]!["inputTokens"]!["value"] = value;
        node["usage"]!["runs"]![0]!["providerCounters"] = new JsonArray();
        var read = SyntheticTelemetry.Consume(node.ToJsonString());
        Assert.Equal(long.Parse(value, System.Globalization.CultureInfo.InvariantCulture), read.Usage.Runs[0].InputTokens.Value);
    }

    [Theory]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData("-0")]
    [InlineData("1e0")]
    [InlineData(" 1")]
    [InlineData("9223372036854775808")]
    [InlineData("-1")]
    public void NoncanonicalOrNegativeMeasuredCountersRejectWithoutEcho(string value)
    {
        var node = Node(); node["usage"]!["runs"]![0]!["inputTokens"]!["value"] = value;
        Rejected(node.ToJsonString());
    }

    [Theory]
    [InlineData("unknown-root")]
    [InlineData("unknown-nested")]
    [InlineData("numeric-counter")]
    [InlineData("empty-guid")]
    [InlineData("uuid-alias")]
    [InlineData("missing-required")]
    [InlineData("null-required")]
    [InlineData("non-utc")]
    [InlineData("numeric-enum")]
    [InlineData("wrong-case")]
    [InlineData("duplicate-run")]
    public void StrictReadRejectsInvalidFixedFields(string mutation)
    {
        var node = Node();
        switch (mutation)
        {
            case "unknown-root": node["prompt"] = "synthetic-canary"; break;
            case "unknown-nested": node["outcome"]!["exception"] = "synthetic-canary"; break;
            case "numeric-counter": node["usage"]!["runs"]![0]!["inputTokens"]!["value"] = 1; break;
            case "empty-guid": node["eventId"] = Guid.Empty.ToString("D"); break;
            case "uuid-alias": node["eventId"] = Guid.NewGuid().ToString("N"); break;
            case "missing-required": node.Remove("invocationId"); break;
            case "null-required": node["usage"] = null; break;
            case "non-utc": node["occurredAt"] = "2026-01-01T01:00:00+01:00"; break;
            case "numeric-enum": node["outcome"]!["kind"] = 0; break;
            case "wrong-case": node["outcome"]!["kind"] = "PARTIAL"; break;
            case "duplicate-run": node["usage"]!["runs"]![1]!["executionId"] = node["usage"]!["runs"]![0]!["executionId"]!.DeepClone(); break;
        }
        Rejected(node.ToJsonString());
    }

    [Fact]
    public void UnsupportedVersionDuplicateDecodedNamesAndTrailingInputNeverAdmit()
    {
        var node = Node(); node["schemaVersion"] = 2;
        Assert.False(TelemetryJson.TryDeserialize(node.ToJsonString(), out var record, out var code));
        Assert.Null(record); Assert.Equal(TelemetryDiagnostic.UnsupportedSchema, code);
        var json = SyntheticTelemetry.Wire(SyntheticTelemetry.Produce());
        Rejected(json[..^1] + ",\"\\u0065ventId\":\"20000000-0000-0000-0000-000000000001\"}");
        Rejected(json + "{}"); Rejected("null"); Rejected("{}"); Rejected("");
        node = Node(); node["extensions"] = JsonNode.Parse("{\"scribe\":{\"x\":[{\"team\":1,\"\\u0074eam\":2}]}}");
        Rejected(node.ToJsonString());
    }

    [Fact]
    public void InvalidConstructionFailsBeforeAnyOutputAndNullSerializationIsUnavailable()
    {
        var record = SyntheticTelemetry.Produce();
        Assert.Throws<ArgumentException>(() => new TelemetryRecord(Guid.Empty, record.InvocationId, record.OccurredAt, record.Product, record.ProductVersion, record.Execution, record.Outcome, record.Usage, record.Extensions));
        Assert.Throws<ArgumentException>(() => new TelemetryRecord(record.EventId, " ", record.OccurredAt, record.Product, record.ProductVersion, record.Execution, record.Outcome, record.Usage, record.Extensions));
        Assert.Throws<ArgumentException>(() => new TelemetryRecord(record.EventId, record.InvocationId, record.OccurredAt.ToOffset(TimeSpan.FromHours(1)), record.Product, record.ProductVersion, record.Execution, record.Outcome, record.Usage, record.Extensions));
        Assert.Throws<ArgumentException>(() => new TelemetryOutcome((TelemetryOutcomeKind)999));
        Assert.Throws<ArgumentException>(() => new TelemetryOutcome(TelemetryOutcomeKind.Failed, "raw exception body"));
        Assert.Throws<ArgumentException>(() => new TelemetryExecution(job: "line\nbreak"));
        Assert.Throws<ArgumentException>(() => new TelemetryExecution(job: "\ud800"));
        Assert.False(TelemetryJson.TrySerialize(null, out var output, out var diagnostic));
        Assert.Null(output); Assert.Equal(TelemetryDiagnostic.InvalidInput, diagnostic);
    }

    [Fact]
    public void Exactly256RunsAdmitAnd257Reject()
    {
        var runs = Enumerable.Range(0, 256).Select(_ => SyntheticTelemetry.Run()).ToArray();
        Assert.Equal(256, SyntheticTelemetry.Consume(SyntheticTelemetry.Wire(SyntheticTelemetry.Produce(new(TelemetryInventoryCoverage.Complete, runs)))).Usage.Runs.Count);
        Assert.Throws<ArgumentException>(() => new TelemetryUsage(TelemetryInventoryCoverage.Complete, runs.Append(SyntheticTelemetry.Run()).ToArray()));
    }

    [Fact]
    public void RecordByteBoundaryIsInclusiveAndNeverReturnsPartialOutput()
    {
        var runs = new List<TelemetryRunSummary>();
        TelemetryRecord candidate;
        string json;
        do
        {
            runs.Add(SyntheticTelemetry.Run(rich: true));
            candidate = SyntheticTelemetry.Produce(new(TelemetryInventoryCoverage.Complete, runs), SyntheticTelemetry.Json("{\"scribe\":{\"padding\":\"\"}}"));
            json = SyntheticTelemetry.Wire(candidate);
        } while (Encoding.UTF8.GetByteCount(json) < TelemetryLimits.MaximumRecordBytes - TelemetryLimits.MaximumExtensionBytes + 1000);
        var padding = TelemetryLimits.MaximumRecordBytes - Encoding.UTF8.GetByteCount(json);
        candidate = SyntheticTelemetry.Produce(candidate.Usage, SyntheticTelemetry.Json("{\"scribe\":{\"padding\":\"" + new string('a', padding) + "\"}}"));
        json = SyntheticTelemetry.Wire(candidate);
        Assert.Equal(TelemetryLimits.MaximumRecordBytes, Encoding.UTF8.GetByteCount(json));
        Assert.NotNull(SyntheticTelemetry.Consume(json));
        var oversized = SyntheticTelemetry.Produce(candidate.Usage, SyntheticTelemetry.Json("{\"scribe\":{\"padding\":\"" + new string('a', padding + 1) + "\"}}"));
        Assert.False(TelemetryJson.TrySerialize(oversized, out var output, out var diagnostic));
        Assert.Null(output); Assert.Equal(TelemetryDiagnostic.RecordTooLarge, diagnostic);
        Rejected(json + " ", TelemetryDiagnostic.RecordTooLarge);
    }

    private static JsonObject Node() => JsonNode.Parse(SyntheticTelemetry.Wire(SyntheticTelemetry.Produce()))!.AsObject();
    internal static void Rejected(string json, TelemetryDiagnostic expected = TelemetryDiagnostic.InvalidInput)
    {
        Assert.False(TelemetryJson.TryDeserialize(json, out var record, out var diagnostic));
        Assert.Null(record); Assert.Equal(expected, diagnostic);
    }
}

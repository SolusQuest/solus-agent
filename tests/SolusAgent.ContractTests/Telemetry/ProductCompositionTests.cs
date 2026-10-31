using System.Text.Json;
using Solus.Telemetry.Contracts;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using Xunit;
using static SolusAgent.ContractTests.Telemetry.TelemetryFixtures;

namespace SolusAgent.ContractTests.Telemetry;

public sealed class ProductCompositionTests
{
    [Fact]
    public void ScribeProducerAndIndependentConsumerPreserveMultipleCurrentRunsAndSeparateHistory()
    {
        using var product = JsonDocument.Parse("{\"scribe\":{\"historicalAccounting\":{\"inputTokens\":\"100\"},\"patchCount\":2}}");
        using var caller = JsonDocument.Parse("{\"team\":\"synthetic\"}");
        var first = Outcome(Run(UsageInventoryCoverage.Complete, new UsageObservation(9007199254740993, 0)));
        var second = Outcome(Run(UsageInventoryCoverage.Partial, new UsageObservation(7)), AgentTerminationReason.Failed);
        var outcomes = new[] { first, second }; var eventId = Guid.NewGuid(); var businessOutcome = new TelemetryOutcome(TelemetryOutcomeKind.Partial);
        Assert.True(ProductTelemetryExamples.ProduceScribe(eventId, outcomes, TelemetryInventoryCoverage.Complete,
            businessOutcome, product.RootElement, caller.RootElement, out var json, out _));
        outcomes[0] = null!; product.Dispose(); caller.Dispose();
        Assert.True(TelemetryJson.TryDeserialize(json!, out var read, out _));
        Assert.Equal(eventId, read!.EventId); Assert.Equal(TelemetryOutcomeKind.Partial, read.Outcome.Kind);
        Assert.Equal(TelemetryInventoryCoverage.Complete, read.Usage.InventoryCoverage); Assert.Equal(2, read.Usage.Runs.Count);
        Assert.Equal(first.ExecutionId, read.Usage.Runs[0].ExecutionId); Assert.Equal(9007199254740993L, read.Usage.Runs[0].InputTokens.Value);
        Assert.Equal(7L, read.Usage.Runs[1].InputTokens.Value); Assert.Equal(TelemetryMeasurementCoverage.Partial, read.Usage.Runs[1].InputTokens.Coverage);
        Assert.Equal("100", read.Extensions.GetProperty("scribe").GetProperty("historicalAccounting").GetProperty("inputTokens").GetString());
        Assert.Equal("synthetic", read.Extensions.GetProperty("caller").GetProperty("team").GetString());
        using var wire = JsonDocument.Parse(json!);
        Assert.Equal("9007199254740993", wire.RootElement.GetProperty("usage").GetProperty("runs")[0].GetProperty("inputTokens").GetProperty("value").GetString());
    }

    [Fact]
    public void AprRestoredContextMapsCurrentOutcomeAndNeverRebillsHistoricalRound()
    {
        var historical = Run(UsageInventoryCoverage.Complete, new UsageObservation(100, 4));
        var previous = historical.Attempts[0]; var id = Guid.NewGuid();
        var seed = new UsageCallLineage(previous.LogicalCallId, previous.ExecutionId, previous.PhysicalAttemptId, previous.AttemptNumber);
        var current = new AgentRunUsage(id, UsageInventoryCoverage.Complete,
            [Attempt(id, new(7, 1), call: previous.LogicalCallId, number: 2)], continuedCalls: [seed]);
        var context = new ContextExecutionResult(id, ContextExecutionIntent.NewRunFromContext, ContextAdmission.Supplied,
            Outcome(current), history: [new(Guid.NewGuid(), Guid.NewGuid(), historical, AgentTerminationReason.Partial, 1)]);
        using var product = JsonDocument.Parse("{\"apr\":{\"historicalAccounting\":{\"inputTokens\":\"100\"},\"findingCount\":3}}");
        using var caller = JsonDocument.Parse("{}");
        Assert.True(ProductTelemetryExamples.ProduceApr(Guid.NewGuid(), context, TelemetryInventoryCoverage.Complete,
            new(TelemetryOutcomeKind.Partial), product.RootElement, caller.RootElement, out var json, out _));
        Assert.True(TelemetryJson.TryDeserialize(json!, out var read, out _)); var run = Assert.Single(read!.Usage.Runs);
        Assert.Equal(id, run.ExecutionId); Assert.Equal(7L, run.InputTokens.Value);
        Assert.Equal(1L, run.LogicalCalls); Assert.Equal(1L, run.PhysicalAttempts);
        Assert.DoesNotContain(historical.ExecutionId.ToString(), json!); Assert.DoesNotContain(previous.PhysicalAttemptId.ToString(), json!);
        Assert.Equal(TelemetryOutcomeKind.Partial, read.Outcome.Kind); Assert.True(context.Outcome!.IsCompleted);
    }

    [Fact]
    public void RejectedContextCannotManufactureCurrentAgentWork()
    {
        var result = new ContextExecutionResult(Guid.NewGuid(), ContextExecutionIntent.NewRunFromContext,
            ContextAdmission.Rejected, rejectionCode: ContextRejectionCode.InvalidContext);
        using var empty = JsonDocument.Parse("{}");
        Assert.True(ProductTelemetryExamples.ProduceApr(Guid.NewGuid(), result, TelemetryInventoryCoverage.Complete,
            new(TelemetryOutcomeKind.Rejected), empty.RootElement, empty.RootElement, out var json, out _));
        Assert.True(TelemetryJson.TryDeserialize(json!, out var read, out _));
        Assert.Empty(read!.Usage.Runs); Assert.Equal(TelemetryOutcomeKind.Rejected, read.Outcome.Kind);
    }

    [Theory]
    [InlineData("{\"scribe\":{},\"caller\":{\"team\":1}}", "{\"team\":2}")]
    [InlineData("{}", "[]")]
    [InlineData("{}", "{\"x\":1,\"\\u0078\":2}")]
    [InlineData("{}", "{\"x\":9007199254740993}")]
    [InlineData("{\"apr\":{}}", "{}")]
    public void InvalidExtensionsProduceNoOutputOrBusinessOutcomeMutation(string extensions, string callerAttributes)
    {
        using var product = JsonDocument.Parse(extensions); using var caller = JsonDocument.Parse(callerAttributes);
        var business = new TelemetryOutcome(TelemetryOutcomeKind.Succeeded);
        Assert.False(ProductTelemetryExamples.ProduceScribe(Guid.NewGuid(), [Outcome(Run())], TelemetryInventoryCoverage.Complete,
            business, product.RootElement, caller.RootElement, out var json, out var diagnostic));
        Assert.Null(json); Assert.Equal(TelemetryDiagnostic.InvalidInput, diagnostic); Assert.Equal(TelemetryOutcomeKind.Succeeded, business.Kind);
    }

    [Fact]
    public void CapacityFailuresHaveNoPartialRecordAndNeverNormalizeDuplicateMembership()
    {
        using var empty = JsonDocument.Parse("{}"); var outcome = Outcome(Run());
        Assert.False(ProductTelemetryExamples.ProduceScribe(Guid.NewGuid(), [outcome, outcome], TelemetryInventoryCoverage.Complete,
            new(TelemetryOutcomeKind.Succeeded), empty.RootElement, empty.RootElement, out var json, out _)); Assert.Null(json);
        using var large = JsonDocument.Parse("{\"scribe\":{\"x\":\"" + new string('a', TelemetryLimits.MaximumExtensionBytes) + "\"}}");
        Assert.False(ProductTelemetryExamples.ProduceScribe(Guid.NewGuid(), [outcome], TelemetryInventoryCoverage.Complete,
            new(TelemetryOutcomeKind.Succeeded), large.RootElement, empty.RootElement, out json, out _)); Assert.Null(json);
    }
}

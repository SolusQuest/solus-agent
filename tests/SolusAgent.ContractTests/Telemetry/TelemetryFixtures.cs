using Solus.Telemetry.Contracts;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Telemetry;
using System.Text.Json;
using Xunit;

namespace SolusAgent.ContractTests.Telemetry;

internal static class TelemetryFixtures
{
    internal static AgentRunUsage Run(UsageInventoryCoverage coverage = UsageInventoryCoverage.Complete,
        params UsageObservation[] observations)
    {
        var id = Guid.NewGuid();
        return new(id, coverage, observations.Select(o => Attempt(id, o)).ToArray());
    }

    internal static UsageAttemptObservation Attempt(Guid id, UsageObservation usage, UsageAccounting? accounting = null,
        Guid? call = null, int number = 1, DispatchExposure exposure = DispatchExposure.Dispatched) =>
        new(id, call ?? Guid.NewGuid(), Guid.NewGuid(), number, exposure, usage, accounting);

    internal static AgentOutcome Outcome(AgentRunUsage usage, AgentTerminationReason reason = AgentTerminationReason.Completed) =>
        new(usage.ExecutionId, reason, 1, failureCode: reason == AgentTerminationReason.Failed ? AgentFailureCode.ExecutionFailed : AgentFailureCode.None, usage: usage);

    internal static TelemetryRecord RoundTrip(AgentRunUsage usage)
    {
        using var extensions = JsonDocument.Parse("{}");
        var record = new TelemetryRecord(Guid.NewGuid(), "synthetic", DateTimeOffset.UtcNow, "test", "1-test", new(),
            new(TelemetryOutcomeKind.Partial), AgentTelemetryMapper.MapUsage(TelemetryInventoryCoverage.Complete, [Outcome(usage)]), extensions.RootElement);
        Assert.True(TelemetryJson.TrySerialize(record, out var json, out var diagnostic), diagnostic.ToString());
        Assert.True(TelemetryJson.TryDeserialize(json!, out var read, out diagnostic), diagnostic.ToString());
        return read!;
    }
}

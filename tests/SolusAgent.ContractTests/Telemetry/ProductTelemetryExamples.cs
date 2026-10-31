using System.Text.Json;
using Solus.Telemetry.Contracts;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Telemetry;

namespace SolusAgent.ContractTests.Telemetry;

/// <summary>Callable synthetic product producers; they perform no Action, provider, storage or product repository operations.</summary>
public static class ProductTelemetryExamples
{
    /// <summary>Composes distinct caller-selected runs; historical accounting is supplied only inside the product extension.</summary>
    public static bool ProduceScribe(Guid eventId, IReadOnlyList<AgentOutcome> currentRuns, TelemetryInventoryCoverage membership,
        TelemetryOutcome productOutcome, JsonElement productExtensions, JsonElement callerAttributes,
        out string? json, out TelemetryDiagnostic diagnostic) => Produce("scribe", eventId,
            () => AgentTelemetryMapper.MapUsage(membership, currentRuns), productOutcome, productExtensions, callerAttributes, out json, out diagnostic);

    /// <summary>Composes only this context call's current outcome. Rejected admission supplies no new run; History is never traversed.</summary>
    public static bool ProduceApr(Guid eventId, ContextExecutionResult currentRound, TelemetryInventoryCoverage membership,
        TelemetryOutcome productOutcome, JsonElement productExtensions, JsonElement callerAttributes,
        out string? json, out TelemetryDiagnostic diagnostic) => Produce("apr", eventId,
            () => AgentTelemetryMapper.MapUsage(membership, currentRound.Outcome is { } current ? [current] : []),
            productOutcome, productExtensions, callerAttributes, out json, out diagnostic);

    private static bool Produce(string product, Guid eventId, Func<TelemetryUsage> usage, TelemetryOutcome productOutcome,
        JsonElement extensions, JsonElement callerAttributes, out string? json, out TelemetryDiagnostic diagnostic)
    {
        json = null; diagnostic = TelemetryDiagnostic.InvalidInput;
        try
        {
            var record = new TelemetryRecord(eventId, "synthetic-invocation", DateTimeOffset.UtcNow, product, "1-test",
                new(invocationKey: "synthetic-current"), productOutcome, usage(), extensions).WithCallerAttributes(callerAttributes);
            return TelemetryJson.TrySerialize(record, out json, out diagnostic);
        }
        catch (ArgumentException) { return false; }
    }
}

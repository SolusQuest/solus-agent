using System.Text.Json;
using Solus.Telemetry.Contracts;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Telemetry;

// Synthetic product result and public observations. No model or storage capability.
if (args.Length != 3 || !Guid.TryParse(args[0], out var eventId)) return 2;
File.AppendAllText(args[2], "scribe-once\n");
var business = new { status = "succeeded", patchCount = 2, document = "synthetic-document" };
string? wire = null;
var diagnostic = TelemetryDiagnostic.InvalidInput;
try
{
    var first = Run("10000000-0000-0000-0000-000000000001", UsageInventoryCoverage.Complete, 9007199254740993, 0,
        [new(ProviderTokenCounterKind.CacheRead, 5, TokenCounterRelationship.IncludedInInput)]);
    var partial = Run("10000000-0000-0000-0000-000000000002", UsageInventoryCoverage.Partial, 0, null);
    var large = Run("10000000-0000-0000-0000-000000000003", UsageInventoryCoverage.Complete, long.MaxValue, 1);
    using var product = JsonDocument.Parse("{\"scribe\":{\"patchCount\":2,\"historicalAccounting\":{\"inputTokens\":\"100\"},\"nested\":[true,null,{\"ratio\":0.125,\"text\":\"你好😀\"}]}}");
    using var caller = JsonDocument.Parse(args[1]);
    var observation = new TelemetryRecord(eventId, "scribe-invocation", new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), "scribe", "synthetic-1",
        new(repository: "synthetic/repository", workflowRunId: "9007199254740993", runAttempt: 2), new(TelemetryOutcomeKind.Succeeded),
        AgentTelemetryMapper.MapUsage(TelemetryInventoryCoverage.Complete, [first, partial, large]), product.RootElement).WithCallerAttributes(caller.RootElement);
    if (observation.Usage.Runs.Count != 3 || observation.Usage.Runs[0].InputTokens.Value != 9007199254740993
        || observation.Usage.Runs[1].InputTokens.Coverage != TelemetryMeasurementCoverage.Partial || observation.Usage.Runs[1].OutputTokens.Value is not null
        || observation.Usage.Runs[2].InputTokens.Value != long.MaxValue) throw new InvalidOperationException();
    TelemetryJson.TrySerialize(observation, out wire, out diagnostic);
    if (wire is not null && !TelemetryJson.TryDeserialize(wire, out _, out _)) throw new InvalidOperationException();
}
catch (Exception error) when (error is ArgumentException or JsonException) { wire = null; }
Console.WriteLine(JsonSerializer.Serialize(new { business, telemetryJson = wire, telemetryDiagnostic = diagnostic.ToString() }));
return 0;

static AgentOutcome Run(string identity, UsageInventoryCoverage coverage, long? input, long? output, IReadOnlyList<ProviderTokenCounter>? counters = null)
{
    var id = Guid.Parse(identity);
    var usage = new AgentRunUsage(id, coverage, [new(id, Guid.NewGuid(), Guid.NewGuid(), 1, DispatchExposure.Dispatched, new(input, output, counters))],
        toolInvocations: new(1, 0, 1), toolCallAdmissions: new(2));
    return new(id, AgentTerminationReason.Completed, 1, usage: usage);
}

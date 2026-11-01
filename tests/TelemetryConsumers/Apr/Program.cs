using System.Text.Json;
using Solus.Telemetry.Contracts;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Telemetry;

if (args.Length != 3 || !Guid.TryParse(args[0], out var eventId)) return 2;
File.AppendAllText(args[2], "apr-once\n");
// A failed business outcome may still have useful terminal telemetry.
var business = new { status = "failed", findingCount = 3, publicationStatus = "withheld" };
string? wire = null;
var diagnostic = TelemetryDiagnostic.InvalidInput;
try
{
    var previous = Run("20000000-0000-0000-0000-000000000001", 100, 4);
    var current = Run("20000000-0000-0000-0000-000000000002", 7, 1);
    var context = new ContextExecutionResult(current.ExecutionId, ContextExecutionIntent.NewRunFromContext, ContextAdmission.Supplied, current,
        history: [new(Guid.NewGuid(), Guid.NewGuid(), previous.Usage!, AgentTerminationReason.Completed, 1)]);
    // Public DTO observation, not a claim that this fixture ran a restoring Runtime.
    var second = Run("20000000-0000-0000-0000-000000000003", 9007199254740993, null, UsageInventoryCoverage.Partial);
    var unknownId = Guid.Parse("20000000-0000-0000-0000-000000000004");
    var unknown = new AgentOutcome(unknownId, AgentTerminationReason.Partial, 0);
    using var product = JsonDocument.Parse("{\"apr\":{\"findingCount\":3,\"publicationStatus\":\"withheld\",\"historicalAccounting\":{\"inputTokens\":\"100\"},\"nested\":{\"items\":[1,true,null,\"你好😀\"]}}}");
    using var caller = JsonDocument.Parse(args[1]);
    var observation = new TelemetryRecord(eventId, "apr-invocation", new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), "apr", "synthetic-1",
        new(repository: "synthetic/repository", workflowRunId: "9007199254740993", runAttempt: 2), new(TelemetryOutcomeKind.Failed),
        AgentTelemetryMapper.MapUsage(TelemetryInventoryCoverage.Complete, [context.Outcome!, second, unknown]), product.RootElement).WithCallerAttributes(caller.RootElement);
    if (observation.Usage.Runs.Count != 3 || observation.Usage.Runs.Any(r => r.ExecutionId == previous.ExecutionId)
        || observation.Usage.Runs[0].InputTokens.Value != 7 || observation.Usage.Runs[1].InputTokens.Value != 9007199254740993
        || observation.Usage.Runs[2].InventoryCoverage != TelemetryInventoryCoverage.Unavailable || observation.Usage.Runs[2].InputTokens.Value is not null) throw new InvalidOperationException();
    TelemetryJson.TrySerialize(observation, out wire, out diagnostic);
    if (wire is not null && (!TelemetryJson.TryDeserialize(wire, out _, out _) || wire.Contains(previous.ExecutionId.ToString(), StringComparison.Ordinal))) throw new InvalidOperationException();
}
catch (Exception error) when (error is ArgumentException or JsonException) { wire = null; }
Console.WriteLine(JsonSerializer.Serialize(new { business, telemetryJson = wire, telemetryDiagnostic = diagnostic.ToString() }));
return 7;

static AgentOutcome Run(string identity, long? input, long? output, UsageInventoryCoverage coverage = UsageInventoryCoverage.Complete)
{
    var id = Guid.Parse(identity);
    var usage = new AgentRunUsage(id, coverage, [new(id, Guid.NewGuid(), Guid.NewGuid(), 1, DispatchExposure.Dispatched, new(input, output))]);
    return new(id, AgentTerminationReason.Completed, 1, usage: usage);
}

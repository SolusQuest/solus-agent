using System.Text.Json;
using Solus.Logging;
using Solus.Logging.Supabase;
using Solus.Telemetry.Contracts;

// This independent package-only consumer exercises public library construction/admission.
// It has no transport invocation; the actual uploader process owns network delivery.
using var product = JsonDocument.Parse("{\"probe\":{\"count\":1}}");
var record = new TelemetryRecord(Guid.NewGuid(), "installed-library-probe", DateTimeOffset.UtcNow, "probe", "synthetic-1", new(),
    new(TelemetryOutcomeKind.Succeeded), new(TelemetryInventoryCoverage.Complete, []), product.RootElement);
if (!TelemetryPayload.TryCreate(record, out var payload) || payload!.EventId != record.EventId) return 2;
using var sink = new SupabaseTelemetrySink(new(new Uri("https://unused.invalid/"), "sb_publishable_synthetic", "synthetic.access.jwt"));
_ = new TelemetryDelivery(sink, new(maximumAttempts: 2, totalTimeout: TimeSpan.FromSeconds(1)));
Console.WriteLine("{\"installedLibraries\":true,\"transportInvoked\":false}");
return 0;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Solus.Logging.Supabase;
using Solus.Telemetry.Contracts;

namespace Solus.Logging.Cli;

internal sealed record CliResult(string DeliveryStatus, string DiagnosticCode, int Attempts = 0)
{
    internal int ExitCode => DeliveryStatus == "failed" ? 1 : 0;
    internal string Json => JsonSerializer.Serialize(new { deliveryStatus = DeliveryStatus, diagnosticCode = DiagnosticCode, attempts = Attempts });
}

// The CLI owns input admission and delivery, never business execution or strict Action policy.
internal static class CliApplication
{
    internal static async Task<CliResult> RunAsync(string[] args, Stream input, Func<string, string?> environment,
        CancellationToken cancellationToken = default, Func<SupabaseTelemetryOptions, ITelemetrySink>? sinkFactory = null)
    {
        var timeoutMs = 10000;
        var attempts = 3;
        var delayMs = 200;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--adapter" or "--endpoint" or "--maximum-attempts" or "--timeout-ms" or "--retry-delay-ms")
                || args[i + 1].Length > 2048 || !values.TryAdd(args[i], args[i + 1])) return Failed("invalid_config");
        }
        if (values.GetValueOrDefault("--adapter", "supabase") != "supabase"
            || !Number("--maximum-attempts", 1, 10, ref attempts) || !Number("--timeout-ms", 1, 120000, ref timeoutMs)
            || !Number("--retry-delay-ms", 0, 10000, ref delayMs)) return Failed("invalid_config");

        bool Number(string name, int minimum, int maximum, ref int value)
        {
            if (!values.TryGetValue(name, out var text)) return true;
            return text.Length <= 6 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= minimum && value <= maximum;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;
        try
        {
            // One sentinel byte detects overflow, including whitespace. No unbounded reader/line allocation.
            var bytes = new byte[TelemetryLimits.MaximumRecordBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var count = await input.ReadAsync(bytes.AsMemory(length), token).AsTask().WaitAsync(token).ConfigureAwait(false);
                if (count == 0) break;
                length += count;
            }
            if (length > TelemetryLimits.MaximumRecordBytes) return Failed("record_too_large");
            var json = new UTF8Encoding(false, true).GetString(bytes, 0, length);
            if (string.IsNullOrWhiteSpace(json)) return new("skipped", "input_unavailable");
            if (!TelemetryJson.TryDeserialize(json, out var record, out var diagnostic)) return Failed(diagnostic switch
            {
                TelemetryDiagnostic.RecordTooLarge => "record_too_large",
                TelemetryDiagnostic.UnsupportedSchema => "unsupported_schema",
                _ => "invalid_record",
            });
            token.ThrowIfCancellationRequested();
            SupabaseTelemetryOptions configuration;
            try
            {
                if (!Uri.TryCreate(values.GetValueOrDefault("--endpoint"), UriKind.Absolute, out var endpoint)) return Failed("invalid_config");
                configuration = new(endpoint, environment("SOLUS_LOGGING_PUBLISHABLE_KEY")!, environment("SOLUS_LOGGING_ACCESS_JWT")!);
            }
            catch (ArgumentException) { return Failed("invalid_config"); }
            var sink = sinkFactory is null ? new SupabaseTelemetrySink(configuration) : sinkFactory(configuration);
            try
            {
                var delivered = await new TelemetryDelivery(sink, new(attempts, TimeSpan.FromMilliseconds(timeoutMs), TimeSpan.FromMilliseconds(delayMs)))
                    .DeliverAsync(record, token).ConfigureAwait(false);
                return new(delivered.Status switch { TelemetryDeliveryStatus.Stored => "stored", TelemetryDeliveryStatus.Duplicate => "duplicate", _ => "failed" },
                    delivered.Diagnostic switch
                    {
                        TelemetryDeliveryDiagnostic.None => "none",
                        TelemetryDeliveryDiagnostic.AuthenticationFailed => "authentication_failed",
                        TelemetryDeliveryDiagnostic.SchemaRejected => "schema_rejected",
                        TelemetryDeliveryDiagnostic.IdentityConflict => "identity_conflict",
                        TelemetryDeliveryDiagnostic.Rejected => "rejected",
                        TelemetryDeliveryDiagnostic.ProtocolFailure => "protocol_failure",
                        TelemetryDeliveryDiagnostic.AttemptsExhausted => "attempts_exhausted",
                        TelemetryDeliveryDiagnostic.DeadlineExceeded => "deadline_exceeded",
                        TelemetryDeliveryDiagnostic.Cancelled => cancellationToken.IsCancellationRequested ? "cancelled" : "deadline_exceeded",
                        _ => "sink_failure",
                    }, delivered.Attempts);
            }
            finally { (sink as IDisposable)?.Dispose(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return Failed(cancellationToken.IsCancellationRequested ? "cancelled" : "deadline_exceeded"); }
        catch (DecoderFallbackException) { return Failed("invalid_record"); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { return Failed("sink_failure"); }
    }
    private static CliResult Failed(string diagnostic) => new("failed", diagnostic);
}

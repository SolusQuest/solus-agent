using System.Text;
using System.Text.Json;
using Solus.Logging.Cli;
using Solus.Logging.Tests.Contracts;
using Solus.Telemetry.Contracts;
using Xunit;

namespace Solus.Logging.Tests.Cli;

public sealed class CliTests
{
    private static readonly string[] Arguments = ["--endpoint", "https://synthetic.invalid/", "--retry-delay-ms", "0"];
    private static string? Environment(string name) => name == "SOLUS_LOGGING_PUBLISHABLE_KEY" ? "sb_publishable_synthetic" : "synthetic.test.jwt";
    private static Task<CliResult> Run(string input, string[]? args = null, ITelemetrySink? sink = null, CancellationToken cancellation = default) =>
        CliApplication.RunAsync(args ?? Arguments, new MemoryStream(Encoding.UTF8.GetBytes(input)), Environment, cancellation, _ => sink ?? new Sink(TelemetrySinkStatus.Stored));

    [Theory]
    [InlineData(TelemetrySinkStatus.Stored, "stored", "none", 1)]
    [InlineData(TelemetrySinkStatus.Duplicate, "duplicate", "none", 1)]
    [InlineData(TelemetrySinkStatus.AuthenticationFailed, "failed", "authentication_failed", 1)]
    [InlineData(TelemetrySinkStatus.SchemaRejected, "failed", "schema_rejected", 1)]
    [InlineData(TelemetrySinkStatus.IdentityConflict, "failed", "identity_conflict", 1)]
    [InlineData(TelemetrySinkStatus.ProtocolFailure, "failed", "protocol_failure", 1)]
    [InlineData(TelemetrySinkStatus.Rejected, "failed", "rejected", 1)]
    [InlineData(TelemetrySinkStatus.TransientFailure, "failed", "attempts_exhausted", 3)]
    public async Task CliMapsActualDeliveryAndRetainsCompleteProducerPayload(TelemetrySinkStatus status, string expected, string diagnostic, int attempts)
    {
        var wire = SyntheticTelemetry.Wire(SyntheticTelemetry.Produce());
        var sink = new Sink(status);
        var result = await Run(wire, sink: sink);
        Assert.Equal(expected, result.DeliveryStatus); Assert.Equal(diagnostic, result.DiagnosticCode); Assert.Equal(attempts, result.Attempts);
        Assert.Equal(expected == "failed" ? 1 : 0, result.ExitCode);
        Assert.All(sink.Payloads, p => Assert.Equal(wire, p));
        using var json = JsonDocument.Parse(result.Json);
        Assert.Equal(3, json.RootElement.EnumerateObject().Count());
        Assert.DoesNotContain("synthetic", result.Json, StringComparison.Ordinal);
        Assert.True(result.Json.Length < 160);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    public async Task CliEmptyIsVisibleSkipWithoutConfigurationOrSink(string input)
    {
        var result = await CliApplication.RunAsync([], new MemoryStream(Encoding.UTF8.GetBytes(input)), _ => throw new Exception(), sinkFactory: _ => throw new Exception());
        Assert.Equal(new CliResult("skipped", "input_unavailable"), result); Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("\uFEFF")]
    [InlineData("\u00A0")]
    [InlineData("{\"raw-secret\":\"canary\"}")]
    public async Task CliInvalidInputCannotAccessConfigurationOrStorage(string input)
    {
        var result = await CliApplication.RunAsync([], new MemoryStream(Encoding.UTF8.GetBytes(input)), _ => throw new Exception(), sinkFactory: _ => throw new Exception());
        Assert.Equal("invalid_record", result.DiagnosticCode); Assert.Equal(0, result.Attempts);
    }

    [Fact]
    public async Task CliMalformedUtf8AndOversizedWhitespaceFailWithoutPartialRecord()
    {
        Assert.Equal("invalid_record", (await CliApplication.RunAsync([], new MemoryStream([0xff]), Environment)).DiagnosticCode);
        Assert.Equal("record_too_large", (await Run(new string(' ', TelemetryLimits.MaximumRecordBytes + 1))).DiagnosticCode);
    }

    [Theory]
    [InlineData("--adapter", "other")]
    [InlineData("--timeout-ms", "0")]
    [InlineData("--maximum-attempts", "11")]
    [InlineData("--retry-delay-ms", "10001")]
    [InlineData("--timeout-ms", "+1")]
    [InlineData("--access-token", "do-not-pass")]
    public async Task CliClosedArgumentsRejectEvenWithEmptyInput(string key, string value) =>
        Assert.Equal("invalid_config", (await Run("", [key, value])).DiagnosticCode);

    [Fact]
    public async Task CliDuplicatesMissingValueAndInvalidCredentialsReject()
    {
        Assert.Equal("invalid_config", (await Run("", ["--adapter", "supabase", "--adapter", "supabase"])).DiagnosticCode);
        Assert.Equal("invalid_config", (await Run("", ["--endpoint"])).DiagnosticCode);
        var wire = SyntheticTelemetry.Wire(SyntheticTelemetry.Produce());
        foreach (var endpoint in new[] { "https://user:secret@synthetic.invalid/", "http://synthetic.invalid", "https://synthetic.invalid/path", "" })
            Assert.Equal("invalid_config", (await Run(wire, ["--endpoint", endpoint])).DiagnosticCode);
        Assert.Equal("invalid_config", (await CliApplication.RunAsync(Arguments, new MemoryStream(Encoding.UTF8.GetBytes(wire)), _ => "secret-canary")).DiagnosticCode);
    }

    [Fact]
    public async Task CliInputAndNoncooperativeSinkAreBoundedAndCancellationIsDistinct()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await CliApplication.RunAsync(["--timeout-ms", "40"], new StalledInput(), Environment);
        Assert.Equal("deadline_exceeded", result.DiagnosticCode); Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Equal("cancelled", (await Run("", cancellation: cancelled.Token)).DiagnosticCode);
        result = await Run(SyntheticTelemetry.Wire(SyntheticTelemetry.Produce()), ["--endpoint", "https://synthetic.invalid/", "--timeout-ms", "80"], new StalledSink());
        Assert.Equal("deadline_exceeded", result.DiagnosticCode); Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task CliFullNeutralByteBoundaryIsAdmittedAndEmitsRealLinuxFixture()
    {
        var runs = new List<TelemetryRunSummary>(); TelemetryRecord candidate; string wire;
        do
        {
            runs.Add(SyntheticTelemetry.Run(rich: true));
            candidate = SyntheticTelemetry.Produce(new(TelemetryInventoryCoverage.Complete, runs), SyntheticTelemetry.Json("{\"scribe\":{\"padding\":\"\"}}"));
            wire = SyntheticTelemetry.Wire(candidate);
        } while (Encoding.UTF8.GetByteCount(wire) < TelemetryLimits.MaximumRecordBytes - TelemetryLimits.MaximumExtensionBytes + 1000);
        var padding = TelemetryLimits.MaximumRecordBytes - Encoding.UTF8.GetByteCount(wire);
        candidate = SyntheticTelemetry.Produce(candidate.Usage, SyntheticTelemetry.Json("{\"scribe\":{\"padding\":\"" + new string('a', padding) + "\"}}"));
        wire = SyntheticTelemetry.Wire(candidate);
        Assert.Equal(TelemetryLimits.MaximumRecordBytes, Encoding.UTF8.GetByteCount(wire));
        var sink = new Sink(TelemetrySinkStatus.Stored);
        Assert.Equal("stored", (await Run(wire, sink: sink)).DeliveryStatus);
        Assert.Equal(wire, Assert.Single(sink.Payloads));
        Assert.Equal("record_too_large", (await Run(wire + " ")).DiagnosticCode);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Solus.Logging.slnx"))) root = root.Parent;
        var fixtures = Path.Combine(root!.FullName, ".local/issue-121/fixtures"); Directory.CreateDirectory(fixtures);
        await File.WriteAllTextAsync(Path.Combine(fixtures, "maximum.json"), wire, new UTF8Encoding(false));
    }

    private sealed class Sink(TelemetrySinkStatus status) : ITelemetrySink
    {
        internal List<string> Payloads { get; } = [];
        public ValueTask<TelemetrySinkStatus> WriteAsync(TelemetryPayload payload, CancellationToken cancellationToken = default)
        { Payloads.Add(payload.Json); return ValueTask.FromResult(status); }
    }
    private sealed class StalledSink : ITelemetrySink
    {
        public ValueTask<TelemetrySinkStatus> WriteAsync(TelemetryPayload payload, CancellationToken cancellationToken = default) =>
            new(new TaskCompletionSource<TelemetrySinkStatus>().Task);
    }
    private sealed class StalledInput : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(new TaskCompletionSource<int>().Task);
    }
}

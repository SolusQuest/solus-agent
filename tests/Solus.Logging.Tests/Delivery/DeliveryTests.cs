using Solus.Logging.Tests.Contracts;
using Solus.Telemetry.Contracts;
using Xunit;

namespace Solus.Logging.Tests.Delivery;

public sealed class DeliveryTests
{
    internal sealed class Sink(Func<TelemetryPayload, CancellationToken, ValueTask<TelemetrySinkStatus>> write) : ITelemetrySink
    {
        public int Calls;
        public ValueTask<TelemetrySinkStatus> WriteAsync(TelemetryPayload payload, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); return write(payload, cancellationToken); }
    }
    private static TelemetryDeliveryOptions Fast(int attempts = 3) => new(attempts, TimeSpan.FromSeconds(5), TimeSpan.Zero);

    [Theory]
    [InlineData(TelemetrySinkStatus.Stored, TelemetryDeliveryStatus.Stored)]
    [InlineData(TelemetrySinkStatus.Duplicate, TelemetryDeliveryStatus.Duplicate)]
    public async Task SuccessfulResultsAreIndependentOfTheProductOutcome(TelemetrySinkStatus status, TelemetryDeliveryStatus expected)
    {
        var record = SyntheticTelemetry.Produce();
        var sink = new Sink((_, _) => ValueTask.FromResult(status));
        var result = await new TelemetryDelivery(sink, Fast()).DeliverAsync(record);
        Assert.Equal(expected, result.Status); Assert.Equal(TelemetryDeliveryDiagnostic.None, result.Diagnostic);
        Assert.Equal(1, result.Attempts); Assert.Equal(1, sink.Calls);
        Assert.Equal(TelemetryOutcomeKind.Partial, record.Outcome.Kind);
    }

    [Theory]
    [InlineData(TelemetrySinkStatus.AuthenticationFailed, TelemetryDeliveryDiagnostic.AuthenticationFailed)]
    [InlineData(TelemetrySinkStatus.SchemaRejected, TelemetryDeliveryDiagnostic.SchemaRejected)]
    [InlineData(TelemetrySinkStatus.IdentityConflict, TelemetryDeliveryDiagnostic.IdentityConflict)]
    [InlineData(TelemetrySinkStatus.Rejected, TelemetryDeliveryDiagnostic.Rejected)]
    [InlineData(TelemetrySinkStatus.ProtocolFailure, TelemetryDeliveryDiagnostic.ProtocolFailure)]
    [InlineData((TelemetrySinkStatus)999, TelemetryDeliveryDiagnostic.SinkFailure)]
    public async Task PermanentAndUndefinedOutcomesNeverRetry(TelemetrySinkStatus status, TelemetryDeliveryDiagnostic diagnostic)
    {
        var sink = new Sink((_, _) => ValueTask.FromResult(status));
        var result = await new TelemetryDelivery(sink, Fast()).DeliverAsync(SyntheticTelemetry.Produce());
        Assert.Equal(TelemetryDeliveryStatus.Failed, result.Status); Assert.Equal(diagnostic, result.Diagnostic);
        Assert.Equal(1, sink.Calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    public async Task TransientAttemptAllowanceIsExactAndReusesTheSameAdmittedPayload(int attempts)
    {
        var payloads = new List<TelemetryPayload>();
        var sink = new Sink((payload, _) => { payloads.Add(payload); return ValueTask.FromResult(TelemetrySinkStatus.TransientFailure); });
        var source = SyntheticTelemetry.Produce();
        var result = await new TelemetryDelivery(sink, Fast(attempts)).DeliverAsync(source);
        Assert.Equal(TelemetryDeliveryDiagnostic.AttemptsExhausted, result.Diagnostic);
        Assert.Equal(attempts, result.Attempts); Assert.Equal(attempts, sink.Calls);
        Assert.All(payloads, p => { Assert.Same(payloads[0], p); Assert.Equal(source.EventId, p.EventId); Assert.Equal(SyntheticTelemetry.Wire(source), p.Json); });
    }

    [Fact]
    public async Task InvalidOrOversizedRecordMakesNoSinkCall()
    {
        var sink = new Sink((_, _) => throw new InvalidOperationException("synthetic-secret-canary"));
        Assert.Equal(TelemetryDeliveryDiagnostic.InvalidRecord, (await new TelemetryDelivery(sink).DeliverAsync(null)).Diagnostic);
        var runs = Enumerable.Range(0, 100).Select(_ => SyntheticTelemetry.Run(rich: true)).ToArray();
        var record = SyntheticTelemetry.Produce(new(TelemetryInventoryCoverage.Complete, runs));
        Assert.False(TelemetryJson.TrySerialize(record, out _, out _));
        Assert.Equal(TelemetryDeliveryDiagnostic.InvalidRecord, (await new TelemetryDelivery(sink).DeliverAsync(record)).Diagnostic);
        Assert.Equal(0, sink.Calls);
    }

    [Fact]
    public async Task SinkExceptionsAreFixedSafeFailuresWithoutRetry()
    {
        var sink = new Sink((_, _) => throw new InvalidOperationException("synthetic-secret-canary"));
        var result = await new TelemetryDelivery(sink, Fast()).DeliverAsync(SyntheticTelemetry.Produce());
        Assert.Equal(TelemetryDeliveryDiagnostic.SinkFailure, result.Diagnostic);
        Assert.DoesNotContain("canary", result.ToString(), StringComparison.Ordinal); Assert.Equal(1, sink.Calls);
    }

    [Fact]
    public async Task CancellationBeforeWorkAdmitsNoAttempt()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var sink = new Sink((_, _) => ValueTask.FromResult(TelemetrySinkStatus.Stored));
        var result = await new TelemetryDelivery(sink).DeliverAsync(SyntheticTelemetry.Produce(), cancellation.Token);
        Assert.Equal(TelemetryDeliveryDiagnostic.Cancelled, result.Diagnostic); Assert.Equal(0, result.Attempts); Assert.Equal(0, sink.Calls);
    }

    [Fact]
    public async Task NoncooperativeAttemptIsCutAndLateSuccessDoesNotChangeTheResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource<TelemetrySinkStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var sink = new Sink((_, _) => { entered.SetResult(); return new(held.Task); });
        var pending = new TelemetryDelivery(sink, Fast()).DeliverAsync(SyntheticTelemetry.Produce(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TelemetryDeliveryDiagnostic.Cancelled, result.Diagnostic); Assert.Equal(1, sink.Calls);
        held.SetResult(TelemetrySinkStatus.Stored);
        Assert.Equal(TelemetryDeliveryStatus.Failed, result.Status);
    }

    [Fact]
    public async Task DeadlineAlsoBoundsASinkThatBlocksBeforeReturningItsValueTask()
    {
        using var held = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new Sink((_, _) => { entered.SetResult(); held.Wait(); return ValueTask.FromResult(TelemetrySinkStatus.Stored); });
        try
        {
            var pending = new TelemetryDelivery(sink, new(3, TimeSpan.FromMilliseconds(250), TimeSpan.Zero)).DeliverAsync(SyntheticTelemetry.Produce());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TelemetryDeliveryDiagnostic.DeadlineExceeded, result.Diagnostic); Assert.Equal(1, sink.Calls);
        }
        finally { held.Set(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationAndDeadlineCutBackoffWithoutAnotherAttempt(bool callerCancellation)
    {
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new Sink((_, _) => { entered.TrySetResult(); return ValueTask.FromResult(TelemetrySinkStatus.TransientFailure); });
        var delivery = new TelemetryDelivery(sink, new(3, callerCancellation ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(10)));
        var pending = delivery.DeliverAsync(SyntheticTelemetry.Produce(), cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (callerCancellation) cancel.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(callerCancellation ? TelemetryDeliveryDiagnostic.Cancelled : TelemetryDeliveryDiagnostic.DeadlineExceeded, result.Diagnostic);
        Assert.Equal(1, sink.Calls);
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(11, 1, 0)]
    [InlineData(1, 0, 0)]
    [InlineData(1, 121, 0)]
    [InlineData(1, 1, -1)]
    [InlineData(1, 1, 11)]
    public void NonfiniteOrUnsupportedPolicyRejects(int attempts, int timeout, int delay) =>
        Assert.Throws<ArgumentException>(() => new TelemetryDeliveryOptions(attempts, TimeSpan.FromSeconds(timeout), TimeSpan.FromSeconds(delay)));
}

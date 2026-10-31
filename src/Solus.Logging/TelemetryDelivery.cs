using Solus.Telemetry.Contracts;

namespace Solus.Logging;

/// <summary>One immutable, fully admitted wire record, reused unchanged across delivery attempts.</summary>
public sealed class TelemetryPayload
{
    private TelemetryPayload(Guid eventId, string json) { EventId = eventId; Json = json; }
    /// <summary>Gets the producer-owned event identity.</summary>
    public Guid EventId { get; }
    /// <summary>Gets complete canonical v1 JSON. No backend wrapper is included.</summary>
    public string Json { get; }
    /// <summary>Admits a complete record without transport access or partial output.</summary>
    public static bool TryCreate(TelemetryRecord? record, out TelemetryPayload? payload)
    {
        payload = null;
        if (!TelemetryJson.TrySerialize(record, out var json, out _)) return false;
        payload = new(record!.EventId, json!); return true;
    }
}

/// <summary>A single attempt's closed classification; only TransientFailure permits automatic retry.</summary>
public enum TelemetrySinkStatus
{
    /// <summary>The first immutable event was stored.</summary>
    Stored,
    /// <summary>The same immutable event was already stored.</summary>
    Duplicate,
    /// <summary>A transient or uncertain transport failure; effects may already have committed.</summary>
    TransientFailure,
    /// <summary>Authentication or authorization failed.</summary>
    AuthenticationFailed,
    /// <summary>The backend schema or supplied record was rejected.</summary>
    SchemaRejected,
    /// <summary>The same event identity already has different immutable content.</summary>
    IdentityConflict,
    /// <summary>A permanent request rejection.</summary>
    Rejected,
    /// <summary>The response did not establish the fixed protocol's outcome.</summary>
    ProtocolFailure,
}

/// <summary>A caller-supplied single-attempt capability, with cooperative cancellation of underlying effects.</summary>
public interface ITelemetrySink
{
    /// <summary>Attempts to store the whole admitted payload, without internal delivery retries.</summary>
    ValueTask<TelemetrySinkStatus> WriteAsync(TelemetryPayload payload, CancellationToken cancellationToken = default);
}

/// <summary>Telemetry delivery status independent of any product business result.</summary>
public enum TelemetryDeliveryStatus
{
    /// <summary>The first event was stored.</summary>
    Stored,
    /// <summary>The same immutable event was already stored.</summary>
    Duplicate,
    /// <summary>Delivery did not establish successful storage; an uncertain attempt may still have committed.</summary>
    Failed,
}

/// <summary>Fixed safe diagnostics; no input, credential, exception or server body is retained.</summary>
public enum TelemetryDeliveryDiagnostic
{
    /// <summary>No delivery error.</summary>
    None,
    /// <summary>The complete neutral record could not be admitted.</summary>
    InvalidRecord,
    /// <summary>Authentication or authorization failed.</summary>
    AuthenticationFailed,
    /// <summary>The backend schema or record was rejected.</summary>
    SchemaRejected,
    /// <summary>Identity reuse with different content.</summary>
    IdentityConflict,
    /// <summary>Permanent rejection.</summary>
    Rejected,
    /// <summary>Invalid or unsupported backend response.</summary>
    ProtocolFailure,
    /// <summary>The finite transient attempt allowance was exhausted.</summary>
    AttemptsExhausted,
    /// <summary>The total wait budget expired.</summary>
    DeadlineExceeded,
    /// <summary>The caller cancelled delivery.</summary>
    Cancelled,
    /// <summary>The supplied sink failed outside its declared protocol.</summary>
    SinkFailure,
}

/// <summary>A safe immutable result; failed delivery never requests product replay.</summary>
/// <param name="Status">Independent delivery status.</param>
/// <param name="Diagnostic">Fixed diagnostic.</param>
/// <param name="Attempts">Number of scheduled sink attempts, at most the configured maximum.</param>
public sealed record TelemetryDeliveryResult(TelemetryDeliveryStatus Status, TelemetryDeliveryDiagnostic Diagnostic, int Attempts);

/// <summary>Finite attempt, total wait and fixed backoff limits.</summary>
public sealed class TelemetryDeliveryOptions
{
    /// <summary>Validates 1..10 attempts, a positive total timeout up to two minutes, and 0..10 seconds backoff.</summary>
    public TelemetryDeliveryOptions(int maximumAttempts = 3, TimeSpan? totalTimeout = null, TimeSpan? retryDelay = null)
    {
        var timeout = totalTimeout ?? TimeSpan.FromSeconds(10);
        var delay = retryDelay ?? TimeSpan.FromMilliseconds(200);
        if (maximumAttempts is < 1 or > 10 || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2)
            || delay < TimeSpan.Zero || delay > TimeSpan.FromSeconds(10))
            throw new ArgumentException("A finite delivery policy within the supported bounds is required.");
        MaximumAttempts = maximumAttempts; TotalTimeout = timeout; RetryDelay = delay;
    }
    /// <summary>Gets the maximum number of attempts.</summary>
    public int MaximumAttempts { get; }
    /// <summary>Gets the total wait budget, including serialization, sends, body reads and backoff.</summary>
    public TimeSpan TotalTimeout { get; }
    /// <summary>Gets fixed bounded backoff; server Retry-After cannot enlarge the budget.</summary>
    public TimeSpan RetryDelay { get; }
}

/// <summary>Admits once and delivers with finite retries and bounded caller waiting.</summary>
/// <remarks>Cancellation cannot roll back an already sent or noncooperative sink's effect. Retain event identity for a later explicit retry.</remarks>
public sealed class TelemetryDelivery
{
    private readonly ITelemetrySink sink;
    private readonly TelemetryDeliveryOptions options;
    /// <summary>Uses a caller-owned sink and an immutable finite policy. The caller owns sink disposal.</summary>
    public TelemetryDelivery(ITelemetrySink sink, TelemetryDeliveryOptions? options = null)
    { this.sink = sink ?? throw new ArgumentNullException(nameof(sink)); this.options = options ?? new(); }

    /// <summary>Returns a fixed safe delivery result; never changes or reexecutes the product invocation.</summary>
    public async Task<TelemetryDeliveryResult> DeliverAsync(TelemetryRecord? record, CancellationToken cancellationToken = default)
    {
        var attempts = 0;
        TelemetryDeliveryResult Failed(TelemetryDeliveryDiagnostic diagnostic) => new(TelemetryDeliveryStatus.Failed, diagnostic, attempts);
        using var deadline = new CancellationTokenSource(options.TotalTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!TelemetryPayload.TryCreate(record, out var payload)) return Failed(TelemetryDeliveryDiagnostic.InvalidRecord);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                attempts++;
                // A custom sink may even block synchronously before returning its ValueTask.
                // Isolate the wait, observe late faults, and never retry a cut attempt.
                var task = Task.Run(async () =>
                {
                    token.ThrowIfCancellationRequested();
                    return await sink.WriteAsync(payload!, token).ConfigureAwait(false);
                }, CancellationToken.None);
                _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                var status = await task.WaitAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (status == TelemetrySinkStatus.Stored) return new(TelemetryDeliveryStatus.Stored, TelemetryDeliveryDiagnostic.None, attempts);
                if (status == TelemetrySinkStatus.Duplicate) return new(TelemetryDeliveryStatus.Duplicate, TelemetryDeliveryDiagnostic.None, attempts);
                if (status != TelemetrySinkStatus.TransientFailure)
                    return Failed(status switch
                    {
                        TelemetrySinkStatus.AuthenticationFailed => TelemetryDeliveryDiagnostic.AuthenticationFailed,
                        TelemetrySinkStatus.SchemaRejected => TelemetryDeliveryDiagnostic.SchemaRejected,
                        TelemetrySinkStatus.IdentityConflict => TelemetryDeliveryDiagnostic.IdentityConflict,
                        TelemetrySinkStatus.Rejected => TelemetryDeliveryDiagnostic.Rejected,
                        TelemetrySinkStatus.ProtocolFailure => TelemetryDeliveryDiagnostic.ProtocolFailure,
                        _ => TelemetryDeliveryDiagnostic.SinkFailure,
                    });
                if (attempts == options.MaximumAttempts) return Failed(TelemetryDeliveryDiagnostic.AttemptsExhausted);
                await Task.Delay(options.RetryDelay, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return Failed(cancellationToken.IsCancellationRequested ? TelemetryDeliveryDiagnostic.Cancelled : TelemetryDeliveryDiagnostic.DeadlineExceeded); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { return Failed(TelemetryDeliveryDiagnostic.SinkFailure); }
    }
}

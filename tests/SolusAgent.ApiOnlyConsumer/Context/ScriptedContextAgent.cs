using System.Text;
using System.Text.Json;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;

namespace SolusAgent.ApiOnlyConsumer.Context;

/// <summary>Test-only context admission and call-scoped capture; not a runtime restoration codec.</summary>
public sealed class ScriptedContextAgent : IContextAgent
{
    public static readonly Guid DefaultImplementationId = Guid.Parse("abcfbe08-cc46-4dbf-8fbd-3d270d3bfe37");
    private readonly int targetWorkUnits;
    private readonly int? partialAfterWorkUnits;
    private readonly bool supportsSuppliedContext;
    private readonly Func<AgentRequest, IReadOnlyList<AgentInput>, int, CancellationToken, ValueTask> performWork;
    private readonly SyntheticAgent ordinaryAgent;
    private int totalWorkStarted;

    public ScriptedContextAgent(int targetWorkUnits, bool supportsSuppliedContext = true,
        Guid? implementationId = null, int? partialAfterWorkUnits = null,
        Func<AgentRequest, IReadOnlyList<AgentInput>, int, CancellationToken, ValueTask>? performWork = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWorkUnits);
        if (partialAfterWorkUnits is <= 0) throw new ArgumentOutOfRangeException(nameof(partialAfterWorkUnits));
        ImplementationId = implementationId ?? DefaultImplementationId;
        if (ImplementationId == Guid.Empty) throw new ArgumentException("An implementation identity is required.", nameof(implementationId));
        this.targetWorkUnits = targetWorkUnits;
        this.partialAfterWorkUnits = partialAfterWorkUnits;
        this.supportsSuppliedContext = supportsSuppliedContext;
        this.performWork = performWork ?? DefaultWorkAsync;
        ordinaryAgent = new SyntheticAgent(targetWorkUnits);
    }

    public Guid ImplementationId { get; }
    public AgentCapability SupportedCapabilities => AgentCapability.WorkUnitLimit | AgentCapability.Cancellation;
    public int TotalWorkStarted => Volatile.Read(ref totalWorkStarted);
    public int OrdinaryWorkStarted => ordinaryAgent.TotalWorkStarted;

    public ValueTask<AgentOutcome> ExecuteAsync(AgentRequest request, IProgress<AgentProgress>? progress = null,
        CancellationToken cancellationToken = default) => ordinaryAgent.ExecuteAsync(request, progress, cancellationToken);

    public async ValueTask<ContextExecutionResult> ExecuteWithContextAsync(ContextExecutionRequest contextRequest,
        IRestrictedContextSink? contextSink = null, IProgress<AgentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contextRequest);
        var request = contextRequest.Request;
        var unavailable = contextSink is null ? ContextCaptureStatus.NotRequested : ContextCaptureStatus.Unavailable;
        var unsupported = request.RequiredCapabilities & ~SupportedCapabilities;
        if (unsupported != AgentCapability.None)
            return new(request.ExecutionId, contextRequest.Intent, ContextAdmission.NotAttempted,
                new AgentOutcome(request.ExecutionId, AgentTerminationReason.UnsupportedCapability, 0, unsupported), captureStatus: unavailable);
        if (cancellationToken.IsCancellationRequested)
            return new(request.ExecutionId, contextRequest.Intent, ContextAdmission.NotAttempted,
                new AgentOutcome(request.ExecutionId, AgentTerminationReason.Cancelled, 0), captureStatus: unavailable);

        Snapshot? supplied = null;
        if (contextRequest.Context is { } envelope)
        {
            if (!supportsSuppliedContext) return Reject(ContextRejectionCode.UnsupportedContext);
            if (envelope.ImplementationId != ImplementationId) return Reject(ContextRejectionCode.ImplementationMismatch);
            if (envelope.FormatVersion != 1) return Reject(ContextRejectionCode.UnsupportedFormat);
            if (envelope.CompatibilityVersion != 1) return Reject(ContextRejectionCode.IncompatibleContext);
            supplied = ReadSnapshot(envelope.CopyRestrictedPayload());
            if (supplied is null) return Reject(ContextRejectionCode.InvalidContext);
            // These Guid/goal rules belong only to this toy implementation, not the public API.
            if ((contextRequest.Intent == ContextExecutionIntent.NewRunFromContext && supplied.OriginExecutionId == request.ExecutionId)
                || (contextRequest.Intent == ContextExecutionIntent.ContinueRun
                    && (supplied.OriginExecutionId != request.ExecutionId || supplied.Finished || supplied.TargetWorkUnits != targetWorkUnits)))
                return Reject(ContextRejectionCode.InvalidRunTransition);
        }

        var completed = contextRequest.Intent == ContextExecutionIntent.ContinueRun ? supplied!.CompletedWorkUnits : 0;
        var data = Array.AsReadOnly((supplied?.Data ?? []).Concat(request.Data).ToArray());
        while (true)
        {
            if (completed == targetWorkUnits) return Finish(AgentTerminationReason.Completed);
            if (cancellationToken.IsCancellationRequested) return Finish(AgentTerminationReason.Cancelled);
            if (completed >= request.Bounds.MaximumWorkUnits) return Finish(AgentTerminationReason.ResourceLimit);
            if (partialAfterWorkUnits is int limit && completed >= limit) return Finish(AgentTerminationReason.Partial);
            try
            {
                Interlocked.Increment(ref totalWorkStarted);
                await performWork(request, data, completed + 1, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Finish(AgentTerminationReason.Cancelled);
            }
            catch (Exception)
            {
                return Finish(AgentTerminationReason.Failed, AgentFailureCode.ExecutionFailed);
            }
            completed++;
            try { progress?.Report(new AgentProgress(request.ExecutionId, completed)); }
            catch (Exception) { return Finish(AgentTerminationReason.Failed, AgentFailureCode.ProgressObserverFailed); }
        }

        ContextExecutionResult Reject(ContextRejectionCode code) =>
            new(request.ExecutionId, contextRequest.Intent, ContextAdmission.Rejected, rejectionCode: code, captureStatus: unavailable);

        ContextExecutionResult Finish(AgentTerminationReason reason, AgentFailureCode failure = AgentFailureCode.None)
        {
            var outcome = new AgentOutcome(request.ExecutionId, reason, completed, failureCode: failure);
            var capture = ContextCaptureStatus.NotRequested;
            if (contextSink is not null)
            {
                try
                {
                    var payload = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        OriginExecutionId = request.ExecutionId, CompletedWorkUnits = completed, TargetWorkUnits = targetWorkUnits,
                        Finished = completed == targetWorkUnits, Data = data.Select(value => new { Source = (int)value.Source, value.Text }).ToArray(),
                    });
                    contextSink.Capture(new AgentContextEnvelope(ImplementationId, 1, 1, payload));
                    capture = ContextCaptureStatus.Delivered;
                }
                catch (Exception) { capture = ContextCaptureStatus.Failed; }
            }
            return new(request.ExecutionId, contextRequest.Intent,
                contextRequest.Intent == ContextExecutionIntent.Fresh ? ContextAdmission.Fresh : ContextAdmission.Supplied,
                outcome, captureStatus: capture);
        }
    }

    private static Snapshot? ReadSnapshot(byte[] payload)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(payload);
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (!HasFields(root, "OriginExecutionId", "CompletedWorkUnits", "TargetWorkUnits", "Finished", "Data")) return null;
            if (root.GetProperty("OriginExecutionId").ValueKind != JsonValueKind.String
                || !root.GetProperty("OriginExecutionId").TryGetGuid(out var origin) || origin == Guid.Empty
                || !root.GetProperty("CompletedWorkUnits").TryGetInt32(out var completed)
                || !root.GetProperty("TargetWorkUnits").TryGetInt32(out var goal)
                || goal <= 0 || completed < 0 || completed > goal
                || root.GetProperty("Finished").ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || root.GetProperty("Finished").GetBoolean() != (completed == goal)
                || root.GetProperty("Data").ValueKind != JsonValueKind.Array) return null;
            var data = new List<AgentInput>();
            foreach (var value in root.GetProperty("Data").EnumerateArray())
            {
                if (!HasFields(value, "Source", "Text") || !value.GetProperty("Source").TryGetInt32(out var source)
                    || !Enum.IsDefined((AgentInputSource)source) || value.GetProperty("Text").ValueKind != JsonValueKind.String) return null;
                data.Add(new AgentInput((AgentInputSource)source, value.GetProperty("Text").GetString()!));
            }
            return new Snapshot(origin, completed, goal, completed == goal, data.AsReadOnly());
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static bool HasFields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!names.Add(property.Name)) return false;
        return names.SetEquals(fields);
    }

    private static async ValueTask DefaultWorkAsync(AgentRequest request, IReadOnlyList<AgentInput> data, int unit, CancellationToken token)
    {
        await Task.Yield();
        token.ThrowIfCancellationRequested();
    }

    private sealed record Snapshot(Guid OriginExecutionId, int CompletedWorkUnits, int TargetWorkUnits, bool Finished, IReadOnlyList<AgentInput> Data);
}

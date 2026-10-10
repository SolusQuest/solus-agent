using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Context;

namespace SolusAgent.Runtime.Execution;

internal sealed partial class RuntimeAgent
{
    public async ValueTask<ContextExecutionResult> ExecuteWithContextAsync(ContextExecutionRequest request,
        IRestrictedContextSink? contextSink = null, IProgress<AgentProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = request.Request;
        var unsupported = current.RequiredCapabilities & ~SupportedCapabilities;
        if (unsupported != 0 || cancellationToken.IsCancellationRequested)
            return new(current.ExecutionId, request.Intent, ContextAdmission.NotAttempted,
                new(current.ExecutionId, unsupported != 0 ? AgentTerminationReason.UnsupportedCapability : AgentTerminationReason.Cancelled,
                    0, unsupported, usage: new AgentRunUsage(current.ExecutionId, UsageInventoryCoverage.Complete, [])),
                captureStatus: contextSink is null ? ContextCaptureStatus.NotRequested : ContextCaptureStatus.Unavailable);
        RestoredOrdinary? restored = null;
        if (request.Context is not null)
        {
            try
            {
                restored = OrdinaryContextCodec.Decode(request, configuration, options);
                if (configuration.ContextAuthority is null || !configuration.ContextAuthority.TryClaim(request.Context, request.RoundGrant!))
                    return Reject(ContextRejectionCode.InvalidRunTransition);
                // Provider recovery may mutate local state. Only a trusted, exclusively claimed source can reach it.
                OrdinaryContextCodec.AdmitProvider(restored, configuration);
            }
            catch (ContextRejected rejection) { return Reject(rejection.Code); }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            { return Reject(ContextRejectionCode.InvalidContext); }
        }
        RunState? completed = null;
        var outcome = await ExecuteOrdinaryAsync(current, progress, cancellationToken, restored,
            (state, selected) => { state.FreezeContext(selected); completed = state; }).ConfigureAwait(false);
        var capture = contextSink is null ? ContextCaptureStatus.NotRequested : ContextCaptureStatus.Unavailable;
        ContextCheckpointInfo? checkpoint = null;
        if (contextSink is not null && completed is not null)
        {
            AgentContextEnvelope? envelope = null;
            try { envelope = completed.CaptureOrdinary(outcome, out checkpoint); }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException)) { }
            if (envelope is not null)
            {
                try { contextSink.Capture(envelope); capture = ContextCaptureStatus.Delivered; }
                catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException)) { capture = ContextCaptureStatus.Failed; }
            }
        }
        return new(current.ExecutionId, request.Intent, restored is null ? ContextAdmission.Fresh : ContextAdmission.Supplied,
            outcome, captureStatus: capture, checkpoint: checkpoint, history: completed?.ContextHistory);

        ContextExecutionResult Reject(ContextRejectionCode code) => new(current.ExecutionId, request.Intent, ContextAdmission.Rejected,
            rejectionCode: code, captureStatus: contextSink is null ? ContextCaptureStatus.NotRequested : ContextCaptureStatus.Unavailable);
    }
}

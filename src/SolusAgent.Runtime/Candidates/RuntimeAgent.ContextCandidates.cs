using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Candidates;
using SolusAgent.Runtime.Context;

namespace SolusAgent.Runtime.Execution;

internal sealed partial class RuntimeAgent : IContextCandidateAgent
{
    public async ValueTask<ContextCandidateExecutionResult> ExecuteCandidatesWithContextAsync(ContextExecutionRequest request,
        CandidateExecutionBounds bounds, ICandidateHost host, IRestrictedContextSink? contextSink = null,
        IProgress<AgentProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(bounds);
        ArgumentNullException.ThrowIfNull(host);
        var current = request.Request;
        var unsupported = current.RequiredCapabilities & ~SupportedCapabilities;
        if (unsupported != 0 || cancellationToken.IsCancellationRequested)
        {
            var outcome = new AgentOutcome(current.ExecutionId, unsupported != 0 ? AgentTerminationReason.UnsupportedCapability : AgentTerminationReason.Cancelled,
                0, unsupported, usage: new AgentRunUsage(current.ExecutionId, UsageInventoryCoverage.Complete, []));
            return new(new(current.ExecutionId, request.Intent, ContextAdmission.NotAttempted, outcome,
                captureStatus: contextSink is null ? ContextCaptureStatus.NotRequested : ContextCaptureStatus.Unavailable),
                new(outcome, unsupported != 0 ? CandidateStopReason.UnsupportedCapability : CandidateStopReason.Cancelled, []));
        }
        RestoredOrdinary? restored = null;
        if (request.Context is not null)
        {
            try
            {
                restored = OrdinaryContextCodec.Decode(request, configuration, options, candidateMode: true);
                if (configuration.ContextAuthority is null || !configuration.ContextAuthority.TryClaim(request.Context, request.RoundGrant!))
                    return Reject(ContextRejectionCode.InvalidRunTransition);
                restored = OrdinaryContextCodec.Materialize(request, restored, configuration, options);
                OrdinaryContextCodec.AdmitProvider(restored, configuration);
            }
            catch (ContextRejected rejection) { return Reject(rejection.Code); }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            { return Reject(ContextRejectionCode.InvalidContext); }
        }
        RunState? completed = null; SavedCandidates? ledger = null; SavedCandidateRound? round = null;
        var candidates = await CandidateExecutionDriver.ExecuteAsync(configuration, options, new(current, bounds), host, progress,
            cancellationToken, restored, request.Intent == ContextExecutionIntent.NewRunFromContext,
            (state, selected, snapshot, facts) =>
            { state.FreezeContext(selected.Outcome); completed = state; ledger = snapshot; round = facts; }).ConfigureAwait(false);
        var capture = contextSink is null ? ContextCaptureStatus.NotRequested : ContextCaptureStatus.Unavailable;
        ContextCheckpointInfo? checkpoint = null;
        if (contextSink is not null && completed is not null)
        {
            AgentContextEnvelope? envelope = null;
            try { envelope = completed.CaptureOrdinary(candidates.Outcome, out checkpoint, ledger, round); }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException)) { checkpoint = null; }
            if (envelope is not null)
            {
                try { contextSink.Capture(envelope); capture = ContextCaptureStatus.Delivered; }
                catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException)) { capture = ContextCaptureStatus.Failed; }
            }
        }
        return new(new(current.ExecutionId, request.Intent, restored is null ? ContextAdmission.Fresh : ContextAdmission.Supplied,
            candidates.Outcome, captureStatus: capture, checkpoint: checkpoint, history: completed?.ContextHistory), candidates);

        ContextCandidateExecutionResult Reject(ContextRejectionCode code) => new(new(current.ExecutionId, request.Intent, ContextAdmission.Rejected,
            rejectionCode: code, captureStatus: contextSink is null ? ContextCaptureStatus.NotRequested : ContextCaptureStatus.Unavailable));
    }
}

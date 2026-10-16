using System.Text;
using System.Text.Json;
using CustomTools;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.Runtime.Context;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Candidates;

public sealed partial class CandidatePartialRecoveryTests
{
    private static string MaximumEscapingCorrection => new('\0', CandidateFeedback.MaximumCorrectionBytes);

    // Independent prospective ledger construction, using the actual codec serializer only to measure bytes.
    // Positive envelopes are always captured by the public runtime, never constructed here.
    private static int CorrectionCapacity(Source source, ContextExecutionRequest request, string? futureCorrection)
    {
        var saved = JsonSerializer.Deserialize<OrdinaryCheckpoint>(source.Envelope.CopyRestrictedPayload())!;
        static int Bytes(string? text) => Encoding.UTF8.GetByteCount(text ?? "");
        var definitions = source.Tools.Sum(t => Bytes(t.Descriptor.Name) + Bytes(t.Descriptor.Description) + Bytes(t.Descriptor.CapabilityId)
            + Bytes(t.Descriptor.InputSchema.NormalizedJson) + Bytes(t.Descriptor.ResultSchema.NormalizedJson));
        var remaining = saved.Members.Select((m, i) => (m, i)).Where(p => p.m.State != SolusAgent.Runtime.Tools.ToolMemberState.Succeeded).ToArray();
        var recovery = remaining.Sum(p => OrdinaryContextCodec.RecoveryReservationBytes(p.i, p.m.Call,
            source.Tools.Single(t => t.Descriptor.Name == p.m.Call.ToolName).Descriptor));
        var finalIndex = saved.Records.Length + remaining.Count(p => p.m.Result is null);
        var id = Guid.NewGuid(); var ledger = saved.Candidates!;
        var prospective = ledger with
        {
            Submissions = [.. ledger.Submissions, new(ledger.Episodes[^1].Id, request.Request.ExecutionId, id, finalIndex, true, request.RoundGrant!.RoundId,
                new(request.Request.ExecutionId, id, CandidateAcknowledgement.Acknowledged, CandidateDecision.Reject, CandidateContinuation.Continue), futureCorrection)],
            Episodes = [.. ledger.Episodes, new(Guid.NewGuid(), ledger.Episodes[^1].WorkId, id, null, null, null)]
        };
        return source.Envelope.PayloadByteCount + definitions + recovery + Bounds.MaximumResponseBytes
            + OrdinaryContextCodec.CandidateBytes(prospective) - OrdinaryContextCodec.CandidateBytes(ledger);
    }

    [Fact]
    public async Task NullOnlyCapacityRejectsBeforeTheMaximumCorrectionCanDestroyCapture()
    {
        var source = await Capture(); var request = CandidateContextFixture.Request(limits: new(maximumToolInvocations: 2));
        var resume = CandidateContextFixture.Resume(source.Result, source.Envelope, request);
        var capacity = CorrectionCapacity(source, resume, null);
        var authority = new SelectedAuthority(source.Envelope, source.Result.Context.Checkpoint!);
        var provider = new PersistentProvider(tools: true) { Respond = ToolContextFixture.Final };
        var host = CandidateContextFixture.Host(CandidateDecision.Reject, CandidateContinuation.Continue, MaximumEscapingCorrection);
        var sink = new RestrictedContextHost();
        var result = await Agent(provider, source.Tools, authority, new(maximumRetainedBytes: capacity, requireContinuation: true))
            .ExecuteCandidatesWithContextAsync(resume, new(1, 0, 0), host, sink);
        // Print only closed observations on regression failure, never the restricted correction.
        Assert.True(result.Context.Admission == ContextAdmission.Rejected,
            $"admission={result.Context.Admission}, capture={result.Context.CaptureStatus}, claim={authority.Claims}, import={provider.Imports}, query={source.Recovery.Queries}, provider={provider.Effects}, host={host.Submissions.Count}, counterEffects={source.Counter.Effects}");
        Assert.Equal(0, authority.Claims); Assert.Equal(0, provider.Imports); Assert.Equal(0, source.Recovery.Queries);
        Assert.Equal(0, provider.Effects); Assert.Empty(host.Submissions); Assert.Equal(1, source.Counter.Effects); Assert.Equal(1, source.Recovery.Effects);
    }

    [Fact]
    public async Task FreshCombinedCandidateReservesCorrectionBeforeInvokingHost()
    {
        var provider = new PersistentProvider(); var host = CandidateContextFixture.Host(correction: MaximumEscapingCorrection);
        var sink = new RestrictedContextHost();
        var result = await CandidateContextFixture.Agent(provider, options: new(maximumRetainedBytes: 96_000, requireContinuation: true))
            .ExecuteCandidatesWithContextAsync(new(CandidateContextFixture.Request(), ContextExecutionIntent.Fresh), new(1, 0, 0), host, sink);
        Assert.Equal(CandidateStopReason.RuntimeLimit, result.Candidates!.StopReason);
        Assert.Equal(1, provider.Effects); Assert.Empty(host.Submissions); Assert.Empty(result.Candidates.Receipts);
        Assert.Equal(ContextCaptureStatus.Delivered, result.Context.CaptureStatus);
    }

    [Fact]
    public void MaximumCorrectionUsesTheActualUtf8AndJsonEscapingExtremum()
    {
        var maximum = MaximumEscapingCorrection;
        _ = new CandidateFeedback(Guid.NewGuid(), Guid.NewGuid(), CandidateAcknowledgement.Acknowledged,
            CandidateDecision.Accept, CandidateContinuation.End, maximum);
        Assert.Equal(CandidateFeedback.MaximumCorrectionBytes, Encoding.UTF8.GetByteCount(maximum));
        Assert.Equal("\"\\u0000\"", JsonSerializer.Serialize("\0"));
        Assert.Equal(6 * CandidateFeedback.MaximumCorrectionBytes + 2, JsonSerializer.SerializeToUtf8Bytes(maximum).Length);
        // All single-byte scalars plus every width/surrogate boundary exercise the configured encoder rule.
        var samples = Enumerable.Range(0, 128).Select(char.ConvertFromUtf32)
            .Concat(new[] { "\u0080", "\u07ff", "\u0800", "\ud7ff", "\ue000", "\uffff", "\U00010000", "\U0010ffff" });
        foreach (var sample in samples)
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(sample).Length - 2 <= 6 * Encoding.UTF8.GetByteCount(sample));
        Assert.Throws<ArgumentException>(() => new CandidateFeedback(Guid.NewGuid(), Guid.NewGuid(), CandidateAcknowledgement.Acknowledged,
            CandidateDecision.Reject, CandidateContinuation.Continue, maximum + "x"));
        Assert.Throws<ArgumentException>(() => new CandidateFeedback(Guid.NewGuid(), Guid.NewGuid(), CandidateAcknowledgement.Unknown,
            correctionText: maximum));
    }

    [Theory]
    [InlineData(-1, ToolRecoveryGuarantee.QueryOutcome)] [InlineData(0, ToolRecoveryGuarantee.QueryOutcome)] [InlineData(1, ToolRecoveryGuarantee.QueryOutcome)]
    [InlineData(-1, ToolRecoveryGuarantee.ConcurrentSafeRepeat)] [InlineData(0, ToolRecoveryGuarantee.ConcurrentSafeRepeat)] [InlineData(1, ToolRecoveryGuarantee.ConcurrentSafeRepeat)]
    public async Task MaximumEscapedCorrectionFitsExactCapacityAndRejectsOneUnderBeforeEveryEffect(int delta, ToolRecoveryGuarantee guarantee)
    {
        var source = await Capture(guarantee); var request = CandidateContextFixture.Request(limits: new(maximumToolInvocations: 2));
        var resume = CandidateContextFixture.Resume(source.Result, source.Envelope, request);
        var capacity = CorrectionCapacity(source, resume, MaximumEscapingCorrection);
        Assert.Equal(6 * CandidateFeedback.MaximumCorrectionBytes - 2, capacity - CorrectionCapacity(source, resume, null));
        var authority = new SelectedAuthority(source.Envelope, source.Result.Context.Checkpoint!);
        var provider = new PersistentProvider(tools: true) { Respond = ToolContextFixture.Final };
        var host = CandidateContextFixture.Host(CandidateDecision.Reject, CandidateContinuation.Continue, MaximumEscapingCorrection);
        var sink = new RestrictedContextHost();
        var ordinary = new CorrectionTool(source.Tools[0].Tool);
        var recovered = new CorrectionRecoveryTool((IRecoverableFunctionTool)source.Tools[1].Tool);
        RuntimeToolRegistration[] tools = [new(ordinary, source.Tools[0].Capability), new(recovered, source.Tools[1].Capability, source.Tools[1].Recovery)];
        var result = await Agent(provider, tools, authority, new(maximumRetainedBytes: capacity + delta, requireContinuation: true))
            .ExecuteCandidatesWithContextAsync(resume, new(1, 0, 0), host, sink);
        if (delta < 0)
        {
            Assert.Equal(ContextAdmission.Rejected, result.Context.Admission); Assert.Equal(0, authority.Claims); Assert.Equal(0, provider.Imports);
            Assert.Equal(0, ordinary.Entries); Assert.Equal(0, recovered.Ordinary); Assert.Equal(0, recovered.Operations); Assert.Equal(0, recovered.Queries);
            Assert.Equal(0, provider.Effects); Assert.Empty(host.Submissions); Assert.Equal(0, sink.CaptureCount);
            Assert.Equal(1, source.Counter.Effects); Assert.Equal(1, source.Recovery.Effects); return;
        }
        Assert.Equal(ContextAdmission.Supplied, result.Context.Admission); Assert.Equal(ContextCaptureStatus.Delivered, result.Context.CaptureStatus);
        Assert.Equal(CandidateStopReason.SubmissionLimit, result.Candidates!.StopReason); Assert.Equal(1, authority.Claims); Assert.Equal(1, provider.Imports);
        Assert.Equal(1, ordinary.Entries); Assert.Equal(0, recovered.Ordinary);
        Assert.Equal(guarantee == ToolRecoveryGuarantee.QueryOutcome ? 1 : 0, recovered.Queries);
        Assert.Equal(guarantee == ToolRecoveryGuarantee.QueryOutcome ? 0 : 1, recovered.Operations);
        Assert.Equal(1, provider.Effects); Assert.Single(host.Submissions); Assert.Equal(1, sink.CaptureCount);
        Assert.Equal(2, source.Counter.Effects); Assert.Equal(1, source.Recovery.Effects);
        Assert.Equal(0, result.Candidates.RepairsAdmitted); Assert.Single(result.Candidates.HistoricalReceipts);
        Assert.Equal(source.Host.Submissions[0].SubmissionId, host.Submissions[0].RepairsSubmissionId);
        Assert.Single(provider.LastRequest!.Inputs, i => i.Text == Correction);
        var saved = JsonSerializer.Deserialize<OrdinaryCheckpoint>(sink.CopyRestrictedContext().CopyRestrictedPayload())!;
        Assert.Equal(MaximumEscapingCorrection, saved.Candidates!.Submissions[^1].Correction);
        Assert.Null(saved.Candidates.Episodes[^1].CorrectionIndex); Assert.Equal(1, saved.Rounds.Sum(r => r.Candidates!.Repairs));
        // No new repair allowance: preserve the uncommitted maximum correction across a genuine zero-work recapture.
        var recaptured = await Resume(source with { Result = result, Envelope = sink.CopyRestrictedContext() });
        Assert.Equal(ContextCaptureStatus.Delivered, recaptured.Result.Context.CaptureStatus);
        Assert.Equal(CandidateStopReason.RepairLimit, recaptured.Result.Candidates!.StopReason);
        Assert.Equal(0, recaptured.Provider.Effects); Assert.Empty(recaptured.Host.Submissions);
        Assert.Equal(0, recaptured.Result.Context.Outcome!.Usage!.ToolInvocations!.Invoked);
        Assert.Equal(MaximumEscapingCorrection, JsonSerializer.Deserialize<OrdinaryCheckpoint>(recaptured.Sink.CopyRestrictedContext().CopyRestrictedPayload())!
            .Candidates!.Submissions[^1].Correction);
    }

    [Theory]
    [InlineData("null", CandidateDecision.Accept, CandidateContinuation.End)]
    [InlineData("empty", CandidateDecision.Reject, CandidateContinuation.End)]
    [InlineData("plain", CandidateDecision.Reject, CandidateContinuation.Continue)]
    [InlineData("ascii-max", CandidateDecision.Accept, CandidateContinuation.Continue)]
    [InlineData("escaped-max", CandidateDecision.Accept, CandidateContinuation.End)]
    [InlineData("unicode-max", CandidateDecision.Reject, CandidateContinuation.End)]
    public async Task LegalFeedbackClassesKeepActualCorrectionAndNeverPersistTheSizingCarrier(string kind, CandidateDecision decision, CandidateContinuation continuation)
    {
        var correction = kind switch { "null" => null, "empty" => "", "plain" => "next-correction", "ascii-max" => new string('x', CandidateFeedback.MaximumCorrectionBytes),
            "escaped-max" => MaximumEscapingCorrection, _ => string.Concat(Enumerable.Repeat("\U0001f600", CandidateFeedback.MaximumCorrectionBytes / 4)) };
        var source = await Capture(); var request = CandidateContextFixture.Request(limits: new(maximumToolInvocations: 2));
        var resume = CandidateContextFixture.Resume(source.Result, source.Envelope, request);
        var provider = new PersistentProvider(tools: true) { Respond = ToolContextFixture.Final };
        var host = CandidateContextFixture.Host(decision, continuation, correction); var sink = new RestrictedContextHost();
        var result = await Agent(provider, source.Tools, new SelectedAuthority(source.Envelope, source.Result.Context.Checkpoint!),
            new(maximumRetainedBytes: CorrectionCapacity(source, resume, MaximumEscapingCorrection), requireContinuation: true))
            .ExecuteCandidatesWithContextAsync(resume, new(1, 0, 0), host, sink);
        Assert.Equal(ContextCaptureStatus.Delivered, result.Context.CaptureStatus); Assert.Equal(1, sink.CaptureCount);
        Assert.Equal(continuation == CandidateContinuation.Continue
                ? CandidateStopReason.SubmissionLimit
                : decision == CandidateDecision.Accept ? CandidateStopReason.Completed : CandidateStopReason.HostEnded,
            result.Candidates!.StopReason);
        var saved = JsonSerializer.Deserialize<OrdinaryCheckpoint>(sink.CopyRestrictedContext().CopyRestrictedPayload())!;
        Assert.Equal(correction, saved.Candidates!.Submissions[^1].Correction);
        Assert.Equal(0, result.Candidates.RepairsAdmitted); Assert.Equal(0, result.Candidates.ContinuationsAdmitted);
        Assert.Equal(source.Host.Submissions[0].SubmissionId, Assert.Single(host.Submissions).RepairsSubmissionId);
        Assert.Single(provider.LastRequest!.Inputs, i => i.Text == Correction);
        if (kind == "plain")
        {
            var nextProvider = new PersistentProvider(tools: true) { Respond = ToolContextFixture.Final };
            var nextHost = CandidateContextFixture.Host(); var nextSink = new RestrictedContextHost();
            var nextRequest = CandidateContextFixture.Request(limits: new(maximumToolInvocations: 1)); var envelope = sink.CopyRestrictedContext();
            var next = await Agent(nextProvider, source.Tools, new SelectedAuthority(envelope, result.Context.Checkpoint!))
                .ExecuteCandidatesWithContextAsync(CandidateContextFixture.Resume(result, envelope, nextRequest), new(1, 1, 0), nextHost, nextSink);
            Assert.Equal(ContextCaptureStatus.Delivered, next.Context.CaptureStatus); Assert.Equal(CandidateStopReason.Completed, next.Candidates!.StopReason);
            Assert.Equal(1, next.Candidates.RepairsAdmitted); Assert.Equal(1, next.Context.History.Sum(r => r.CompletedWorkUnits) - 2);
            Assert.Single(nextProvider.LastRequest!.Inputs, i => i.Text == correction); Assert.Single(nextProvider.LastRequest.Inputs, i => i.Text == Correction);
            Assert.Equal(host.Submissions[0].SubmissionId, Assert.Single(nextHost.Submissions).RepairsSubmissionId);
            Assert.Equal(0, next.Context.Outcome!.Usage!.ToolInvocations!.Invoked);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RecoveryOnlyRecaptureStillReservesMaximumFeedbackInTheLaterRound(bool beforeEntry)
    {
        var source = await Capture(); using var cancel = new CancellationTokenSource();
        var authority = new AtClaim(new SelectedAuthority(source.Envelope, source.Result.Context.Checkpoint!), beforeEntry ? cancel.Cancel : null);
        var middle = await Resume(source, progress: beforeEntry ? null : new ThrowingProgress(), token: cancel.Token, authority: authority);
        Assert.Equal(ContextCaptureStatus.Delivered, middle.Result.Context.CaptureStatus); Assert.Equal(0, middle.Provider.Effects);
        var retained = source with { Result = middle.Result, Envelope = middle.Sink.CopyRestrictedContext() };
        var request = CandidateContextFixture.Request(limits: new(maximumToolInvocations: 2));
        var resume = CandidateContextFixture.Resume(retained.Result, retained.Envelope, request);
        var host = CandidateContextFixture.Host(correction: MaximumEscapingCorrection); var sink = new RestrictedContextHost();
        var provider = new PersistentProvider(tools: true) { Respond = ToolContextFixture.Final };
        var last = await Agent(provider, source.Tools, new SelectedAuthority(retained.Envelope, retained.Result.Context.Checkpoint!),
            new(maximumRetainedBytes: CorrectionCapacity(retained, resume, MaximumEscapingCorrection), requireContinuation: true))
            .ExecuteCandidatesWithContextAsync(resume, new(1, 0, 0), host, sink);
        Assert.Equal(ContextCaptureStatus.Delivered, last.Context.CaptureStatus); Assert.Equal(CandidateStopReason.Completed, last.Candidates!.StopReason);
        Assert.Equal(0, last.Candidates.RepairsAdmitted); Assert.Equal(1, source.Recovery.Queries); Assert.Equal(2, source.Counter.Effects);
        Assert.Single(provider.LastRequest!.Inputs, i => i.Text == Correction);
        Assert.Equal(MaximumEscapingCorrection, JsonSerializer.Deserialize<OrdinaryCheckpoint>(sink.CopyRestrictedContext().CopyRestrictedPayload())!.Candidates!.Submissions[^1].Correction);
    }

    [Fact]
    public async Task RestoredUnsubmittedFinalChecksCorrectionRoomBeforeItsFirstHostInvocation()
    {
        var source = await CandidateContextFixture.Capture(progress: new ThrowingProgress());
        Assert.Empty(source.Host.Submissions);
        var authority = new SelectedAuthority(source.Envelope, source.Result.Context.Checkpoint!);
        var provider = new PersistentProvider(); var host = CandidateContextFixture.Host(correction: MaximumEscapingCorrection);
        var sink = new RestrictedContextHost();
        var result = await CandidateContextFixture.Agent(provider, authority, new(maximumRetainedBytes: 96_000, requireContinuation: true))
            .ExecuteCandidatesWithContextAsync(CandidateContextFixture.Resume(source.Result, source.Envelope, CandidateContextFixture.Request()), new(1, 0, 0), host, sink);
        // This cursor has no recovery/production preflight: the existing claim/import precedes pre-Host reservation.
        Assert.Equal(1, authority.Claims); Assert.Equal(1, provider.Imports); Assert.Equal(0, provider.Effects); Assert.Empty(host.Submissions);
        Assert.Equal(CandidateStopReason.RuntimeLimit, result.Candidates!.StopReason); Assert.Equal(ContextCaptureStatus.Delivered, result.Context.CaptureStatus);
        var laterEnvelope = sink.CopyRestrictedContext(); var laterProvider = new PersistentProvider(); var laterHost = CandidateContextFixture.Host(correction: MaximumEscapingCorrection);
        var later = await CandidateContextFixture.Agent(laterProvider, new SelectedAuthority(laterEnvelope, result.Context.Checkpoint!))
            .ExecuteCandidatesWithContextAsync(CandidateContextFixture.Resume(result, laterEnvelope, CandidateContextFixture.Request()), new(1, 0, 0), laterHost, new RestrictedContextHost());
        Assert.Equal(ContextCaptureStatus.Delivered, later.Context.CaptureStatus); Assert.Equal(CandidateStopReason.Completed, later.Candidates!.StopReason);
        Assert.Equal(0, laterProvider.Effects);
        Assert.Equal(source.Result.Context.ExecutionId, Assert.Single(laterHost.Submissions).ExecutionId);
    }

    [Fact]
    public async Task MaximumAcknowledgedCorrectionAtHostCutSurvivesCaptureAndKnownEndRecapture()
    {
        var source = await Capture(); using var cancel = new CancellationTokenSource();
        var request = CandidateContextFixture.Request(limits: new(maximumToolInvocations: 2));
        var resume = CandidateContextFixture.Resume(source.Result, source.Envelope, request);
        var host = new ScriptedCandidateHost((s, _) => { cancel.Cancel(); return ValueTask.FromResult<CandidateFeedback?>(
            new(s.ExecutionId, s.SubmissionId, CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept, CandidateContinuation.End, MaximumEscapingCorrection)); });
        var provider = new PersistentProvider(tools: true) { Respond = ToolContextFixture.Final }; var sink = new RestrictedContextHost();
        var result = await Agent(provider, source.Tools, new SelectedAuthority(source.Envelope, source.Result.Context.Checkpoint!),
            new(maximumRetainedBytes: CorrectionCapacity(source, resume, MaximumEscapingCorrection), requireContinuation: true))
            .ExecuteCandidatesWithContextAsync(resume, new(1, 0, 0), host, sink, cancellationToken: cancel.Token);
        Assert.Equal(CandidateStopReason.Cancelled, result.Candidates!.StopReason); Assert.Equal(ContextCaptureStatus.Delivered, result.Context.CaptureStatus);
        Assert.True(Assert.Single(result.Candidates.Receipts).IsAccepted);
        var next = await Resume(source with { Result = result, Envelope = sink.CopyRestrictedContext() });
        Assert.Equal(CandidateStopReason.Completed, next.Result.Candidates!.StopReason); Assert.Equal(ContextCaptureStatus.Delivered, next.Result.Context.CaptureStatus);
        Assert.Equal(0, next.Provider.Effects); Assert.Empty(next.Host.Submissions); Assert.Equal(0, next.Result.Context.Outcome!.Usage!.ToolInvocations!.Invoked);
        Assert.Equal(MaximumEscapingCorrection, JsonSerializer.Deserialize<OrdinaryCheckpoint>(next.Sink.CopyRestrictedContext().CopyRestrictedPayload())!.Candidates!.Submissions[^1].Correction);
    }

    [Fact]
    public async Task UnknownFeedbackStoresNoSizingCorrectionAndNeverReinvokesHost()
    {
        var source = await Capture(); var provider = new PersistentProvider(tools: true) { Respond = ToolContextFixture.Final };
        var sink = new RestrictedContextHost();
        var host = new ScriptedCandidateHost((s, _) => ValueTask.FromResult<CandidateFeedback?>(new(s.ExecutionId, s.SubmissionId, CandidateAcknowledgement.Unknown)));
        var result = await Agent(provider, source.Tools, new SelectedAuthority(source.Envelope, source.Result.Context.Checkpoint!))
            .ExecuteCandidatesWithContextAsync(CandidateContextFixture.Resume(source.Result, source.Envelope, CandidateContextFixture.Request()), new(1, 0, 0), host, sink);
        Assert.Equal(ContextCaptureStatus.Delivered, result.Context.CaptureStatus); Assert.Equal(CandidateStopReason.UnknownAcknowledgement, result.Candidates!.StopReason);
        Assert.Null(JsonSerializer.Deserialize<OrdinaryCheckpoint>(sink.CopyRestrictedContext().CopyRestrictedPayload())!.Candidates!.Submissions[^1].Correction);
        var next = await Resume(source with { Result = result, Envelope = sink.CopyRestrictedContext() });
        Assert.Equal(CandidateStopReason.UnknownAcknowledgement, next.Result.Candidates!.StopReason); Assert.Empty(next.Host.Submissions); Assert.Equal(0, next.Provider.Effects);
    }

    private sealed class CorrectionTool(IFunctionTool inner) : IFunctionTool
    {
        internal int Entries { get; private set; }
        public ToolDescriptor Descriptor => inner.Descriptor;
        public ToolPreparation Prepare(ToolCall call) => inner.Prepare(call);
        public ToolError ValidateCapability(IToolCapability? capability) => inner.ValidateCapability(capability);
        public ToolError ValidateInvocation(PreparedToolInvocation p, ToolCall c, IToolCapability? cap) => inner.ValidateInvocation(p, c, cap);
        public ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation p, ToolCall c, IToolCapability? cap, CancellationToken token = default)
        { Entries++; return inner.InvokeAsync(p, c, cap, token); }
    }
    private sealed class CorrectionRecoveryTool(IRecoverableFunctionTool inner) : IRecoverableFunctionTool
    {
        internal int Ordinary { get; private set; } internal int Operations { get; private set; } internal int Queries { get; private set; }
        public ToolDescriptor Descriptor => inner.Descriptor;
        public ToolPreparation Prepare(ToolCall call) => inner.Prepare(call);
        public ToolError ValidateCapability(IToolCapability? capability) => inner.ValidateCapability(capability);
        public ToolError ValidateInvocation(PreparedToolInvocation p, ToolCall c, IToolCapability? cap) => inner.ValidateInvocation(p, c, cap);
        public ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation p, ToolCall c, IToolCapability? cap, CancellationToken token = default)
        { Ordinary++; return inner.InvokeAsync(p, c, cap, token); }
        public ToolError ValidateRecoveryBinding(ToolRecoveryBinding binding, IToolCapability? capability) => inner.ValidateRecoveryBinding(binding, capability);
        public ToolError ValidateOperation(ToolOperation op, IToolCapability? cap) => inner.ValidateOperation(op, cap);
        public ValueTask<ToolResult> InvokeOperationAsync(PreparedToolInvocation p, ToolOperation op, IToolCapability? cap, CancellationToken token = default)
        { Operations++; return inner.InvokeOperationAsync(p, op, cap, token); }
        public ValueTask<ToolRecoveryReceipt> QueryOutcomeAsync(ToolOperation op, IToolCapability? cap, CancellationToken token = default)
        { Queries++; return inner.QueryOutcomeAsync(op, cap, token); }
    }
}

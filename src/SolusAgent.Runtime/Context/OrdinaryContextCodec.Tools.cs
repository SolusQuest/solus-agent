using System.Text;
using System.Text.Json;
using SolusAgent.Api.Context;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Runtime.Tools;
using SolusAgent.Tools.Api;

namespace SolusAgent.Runtime.Context;

internal static partial class OrdinaryContextCodec
{
    private static bool SameAttempt(ProviderAttempt? a, ProviderAttempt? b) => a is null ? b is null : b is not null && a.Matches(b);
    private static int Bytes(string? text) => text is null ? 0 : Encoding.UTF8.GetByteCount(text);
    private static int ScopeBytes(ProviderScope scope) => checked(Bytes(scope.Provider) + Bytes(scope.Model));
    private static int CallBytes(ToolCall call) => checked(Bytes(call.CallId) + Bytes(call.ToolName) + Bytes(call.ArgumentsJson));
    private static int DefinitionBytes(RuntimeConfiguration config) => checked(config.Tools.Sum(t => checked(Bytes(t.Descriptor.Name)
        + Bytes(t.Descriptor.Description) + Bytes(t.Descriptor.CapabilityId) + Bytes(t.Descriptor.InputSchema.NormalizedJson)
        + Bytes(t.Descriptor.ResultSchema.NormalizedJson)))) + (config.ToolArgumentCorrection?.PayloadByteCount ?? 0);
    private static ProviderResponse Candidate(SavedInput input, RuntimeConfiguration config, RuntimeOptions options) => input.Final is { } final
        ? new(final.Scope, final.Attempt, ProviderFinish.Final, final.Text, [], final.Continuation?.Restore())
        : input.ToolModel is { } tool
            ? new(tool.Scope, tool.Attempt, ProviderFinish.ToolCalls, tool.Text, tool.Calls, tool.Continuation?.Restore())
            : input.CorrectionModel is { } correction ? CorrectionCandidate(correction, config, options) : throw new JsonException();

    // Validate committed chronology separately from ProviderRequest's identity-based result collection contract.
    private static Dictionary<Guid, bool> ValidateToolRecords(OrdinaryCheckpoint saved, RuntimeConfiguration config, RuntimeOptions options)
    {
        if (saved.Members.Length > options.MaximumRecords) throw new JsonException();
        var registry = config.Tools.ToDictionary(t => t.Descriptor.Name, StringComparer.Ordinal);
        var completed = new Dictionary<Guid, bool>();
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        var memberIndex = 0; var resultIndex = 0; var returned = new HashSet<int>();
        SavedToolModel? pending = null;
        SavedCorrectionModel? pendingCorrection = null; var correctionOrdinal = 0;
        foreach (var entry in saved.Records)
        {
            if (!Enum.IsDefined(entry.Kind) || pendingCorrection is not null && entry.Kind != ProviderInputKind.ToolCorrectionFeedbackData) throw new JsonException();
            if (entry.Kind is ProviderInputKind.HostInstruction or ProviderInputKind.InputData)
            {
                if (entry.Text is null || entry.Final is not null || entry.ToolModel is not null || entry.ToolMember is not null
                    || entry.CorrectionModel is not null || entry.CorrectionFeedback is not null
                    || pending is not null && !completed[pending.Attempt.PhysicalAttemptId]) throw new JsonException();
                _ = entry.Kind == ProviderInputKind.HostInstruction ? ProviderInput.Instruction(entry.Text) : ProviderInput.Data(entry.Text);
                if (Bytes(entry.Text) > config.Bounds.MaximumTextBytes) throw new JsonException();
            }
            else if (entry.Kind == ProviderInputKind.ModelData)
            {
                if (entry.Text is not null || entry.ToolMember is not null || entry.CorrectionFeedback is not null
                    || (entry.Final is null ? 0 : 1) + (entry.ToolModel is null ? 0 : 1) + (entry.CorrectionModel is null ? 0 : 1) != 1
                    || pending is not null && !completed[pending.Attempt.PhysicalAttemptId]) throw new JsonException();
                var candidate = Candidate(entry, config, options);
                var bounds = ModelBounds(entry)!;
                if (!candidate.Scope.Matches(saved.Scope) || candidate.PayloadByteCount > bounds.MaximumResponseBytes
                    || candidate.Text is { } text && Bytes(text) > bounds.MaximumTextBytes
                    || (candidate.Correction?.Members.Count ?? candidate.Calls.Count) > bounds.MaximumToolCalls
                    || candidate.Continuation?.ByteCount > bounds.MaximumContinuationBytes) throw new JsonException();
                pending = entry.ToolModel;
                if (entry.CorrectionModel is { } correction)
                {
                    pendingCorrection = correction; correctionOrdinal = 0;
                    foreach (var member in correction.Members)
                    {
                        if (!callIds.Add(member.Call.CallId) || !registry.TryGetValue(member.Call.ToolName, out var binding)
                            || SavedDescriptor.From(binding.Tool.Descriptor) != SavedDescriptor.From(binding.Descriptor)
                            || binding.Capability.CapabilityId != binding.Descriptor.CapabilityId) throw new JsonException();
                        if (correction.Origin == ToolCorrectionOrigin.HostArguments && member.ErrorCode != ToolArgumentCorrectionPolicy.BatchNotExecuted
                            && (binding.ArgumentCorrection is null || !binding.ArgumentCorrection.AllowedErrorKeys.Contains(member.ErrorCode, StringComparer.Ordinal)))
                            throw new JsonException();
                    }
                    completed.Add(correction.Attempt.PhysicalAttemptId, false);
                    continue;
                }
                if (pending is null) continue;
                resultIndex = 0;
                var batchStart = memberIndex;
                foreach (var call in pending.Calls)
                {
                    if (!callIds.Add(call.CallId) || !registry.TryGetValue(call.ToolName, out var registration)
                        || SavedDescriptor.From(registration.Tool.Descriptor) != SavedDescriptor.From(registration.Descriptor)
                        || registration.Capability.CapabilityId != registration.Descriptor.CapabilityId
                        || registration.Descriptor.InputSchema.Validate(call.ArgumentsJson, registration.Descriptor.MaximumArgumentBytes) != ToolError.None)
                        throw new JsonException();
                    if (memberIndex >= saved.Members.Length || !saved.Members[memberIndex].ModelAttempt.Matches(pending.Attempt)) continue;
                    var member = saved.Members[memberIndex++];
                    if (member.Ordinal != memberIndex - batchStart || !member.Call.Matches(call)
                        || member.Descriptor != SavedDescriptor.From(registration.Descriptor)
                        || !Enum.IsDefined(member.State) || !Enum.IsDefined(member.Error) || member.ReservationHeld) throw new JsonException();
                    _ = member.Descriptor.Restore();
                    if (member.Result is { } result)
                    {
                        var expected = result.Outcome switch
                        {
                            ToolOutcome.Succeeded => ToolMemberState.Succeeded,
                            ToolOutcome.Cancelled => ToolMemberState.Cancelled,
                            ToolOutcome.Failed => ToolMemberState.Failed,
                            ToolOutcome.Rejected => ToolMemberState.Rejected,
                            _ => throw new JsonException()
                        };
                        if (member.State != expected || !result.Call.Matches(call) || result.Error != member.Error
                            || !Enum.IsDefined(result.Error)
                            || (result.Outcome == ToolOutcome.Succeeded
                                ? result.Error != ToolError.None || !result.InvocationStarted || result.Json is null
                                    || registration.Descriptor.ResultSchema.Validate(result.Json, registration.Descriptor.MaximumResultBytes) != ToolError.None
                                : result.Error == ToolError.None || result.Json is not null
                                    || result.Outcome == ToolOutcome.Cancelled && result.Error != ToolError.Cancelled)) throw new JsonException();
                    }
                    else if (member.State is ToolMemberState.Succeeded or ToolMemberState.Cancelled
                        || member.State is ToolMemberState.Unstarted or ToolMemberState.InvokedUnknown && member.Error != ToolError.None
                        || member.State is ToolMemberState.Failed or ToolMemberState.Rejected && member.Error == ToolError.None) throw new JsonException();
                }
                var count = memberIndex - batchStart;
                if (count != 0 && count != pending.Calls.Length) throw new JsonException();
                completed.Add(pending.Attempt.PhysicalAttemptId, false);
            }
            else if (entry.Kind == ProviderInputKind.ToolResultData)
            {
                if (entry.Text is not null || entry.Final is not null || entry.ToolModel is not null || entry.CorrectionModel is not null
                    || entry.CorrectionFeedback is not null || entry.ToolMember is not { } index
                    || pending is null || index < 0 || index >= saved.Members.Length || !returned.Add(index)
                    || resultIndex >= pending.Calls.Length) throw new JsonException();
                var member = saved.Members[index];
                if (member.Result is null || !member.ModelAttempt.Matches(pending.Attempt) || member.Ordinal != ++resultIndex
                    || !member.Call.Matches(pending.Calls[resultIndex - 1])
                    || resultIndex > 1 && saved.Members[index - 1].State != ToolMemberState.Succeeded) throw new JsonException();
                if (resultIndex == pending.Calls.Length)
                    completed[pending.Attempt.PhysicalAttemptId] = saved.Members.Where(m => m.ModelAttempt.Matches(pending.Attempt))
                        .All(m => m.State == ToolMemberState.Succeeded && m.Result is not null);
            }
            else if (entry.Kind == ProviderInputKind.ToolCorrectionFeedbackData)
            {
                if (entry.Text is not null || entry.Final is not null || entry.ToolModel is not null || entry.ToolMember is not null
                    || entry.CorrectionModel is not null || entry.CorrectionFeedback is not { } feedback || pendingCorrection is null
                    || !feedback.ModelAttempt.Matches(pendingCorrection.Attempt) || feedback.Ordinal != ++correctionOrdinal
                    || correctionOrdinal > pendingCorrection.Members.Length) throw new JsonException();
                if (correctionOrdinal == pendingCorrection.Members.Length)
                { completed[pendingCorrection.Attempt.PhysicalAttemptId] = true; pendingCorrection = null; }
            }
            else throw new JsonException();
        }
        if (pendingCorrection is not null) throw new JsonException();
        if (memberIndex != saved.Members.Length || returned.Count != saved.Members.Count(m => m.Result is not null)) throw new JsonException();
        return completed;
    }

    // The same immutable admitted facts drive checked preclaim accounting and postclaim materialization.
    private static int ProspectivePayload(ContextExecutionRequest request, OrdinaryCheckpoint saved, RuntimeConfiguration config, RuntimeOptions options)
    {
        var continuing = request.Intent == ContextExecutionIntent.ContinueRun;
        var total = checked(ScopeBytes(config.Scope) + DefinitionBytes(config));
        var effective = EffectiveMembers(saved, config, options, out _);
        foreach (var entry in saved.Records)
        {
            var count = entry.Kind switch
            {
                ProviderInputKind.HostInstruction => Bytes(continuing ? entry.Text : request.Request.Instructions),
                ProviderInputKind.InputData => Bytes(entry.Text),
                ProviderInputKind.ModelData => Candidate(entry, config, options).PayloadByteCount,
                ProviderInputKind.ToolCorrectionFeedbackData => CorrectionFeedbackBytes(entry.CorrectionFeedback!, saved, config, options),
                ProviderInputKind.ToolResultData => checked(CallBytes(effective[entry.ToolMember!.Value].Result!.Call)
                    + Bytes(effective[entry.ToolMember.Value].Result!.Json)),
                _ => throw new JsonException()
            };
            total = checked(total + count);
        }
        if (!continuing) total = checked(total + request.Request.Data.Sum(d => Bytes(d.Text)));
        var latest = saved.Records.LastOrDefault(r => ModelAttempt(r) is not null);
        var continuation = latest is null ? null : ModelContinuation(latest);
        if (continuation is not null) total = checked(total + continuation.Bytes.Length + ScopeBytes(continuation.Scope));
        return total;
    }

    private static RestoredOrdinary DecodeTools(ContextExecutionRequest request, OrdinaryCheckpoint saved,
        RuntimeConfiguration config, RuntimeOptions options, int encodedBytes)
    {
        var continuing = request.Intent == ContextExecutionIntent.ContinueRun;
        var pending = continuing ? saved.Pending : null;
        var bounds = pending is null ? config.Bounds : saved.OriginalBounds!;
        var count = checked(saved.Records.Length + (continuing ? 0 : request.Request.Data.Count));
        // Validate added Host data before its bytes participate in admission.
        _ = ProviderInput.Instruction(request.Request.Instructions);
        foreach (var data in request.Request.Data) _ = ProviderInput.Data(data.Text);
        if (Bytes(request.Request.Instructions) > bounds.MaximumTextBytes
            || request.Request.Data.Any(d => Bytes(d.Text) > bounds.MaximumTextBytes)) throw new JsonException();
        var payload = ProspectivePayload(request, saved, config, options);
        var retained = Math.Max(payload, checked(encodedBytes + DefinitionBytes(config)
            + (continuing ? 0 : Bytes(request.Request.Instructions) + request.Request.Data.Sum(d => Bytes(d.Text)))));
        var effective = EffectiveMembers(saved, config, options, out _);
        var remaining = continuing && saved.ToolCursor is not null
            ? effective.Select((m, i) => (Member: m, Index: i)).Where(p => p.Member.ModelAttempt.Matches(saved.ToolCursor)
                && p.Member.State != ToolMemberState.Succeeded).ToArray() : [];
        long recoveryBytes = 0; long futurePayload = payload;
        foreach (var (member, index) in remaining)
        {
            var descriptor = config.Tools.Single(t => t.Descriptor.Name == member.Call.ToolName).Descriptor;
            recoveryBytes += RecoveryReservationBytes(index, member.Call, descriptor);
            futurePayload += CallBytes(member.Call) + descriptor.MaximumResultBytes;
            if (member.Result is { } old) futurePayload -= CallBytes(old.Call) + Bytes(old.Json);
        }
        var actions = saved.Rounds.Sum(r => r.RecoveryActions?.Length ?? 0);
        var candidateRecords = saved.Candidates is { } ledger ? ledger.Episodes.Length + ledger.Submissions.Length : 0;
        var candidateProduction = saved.Candidates?.Cursor == CandidateCursor.ProducingAdmittedEpisode && saved.ToolCursor is not null;
        var reserveResponse = continuing && (saved.Candidates is null || saved.Pending is not null || candidateProduction)
            || !continuing && config.ToolArgumentCorrection is not null;
        var projectedInputs = checked(count + remaining.Count(p => p.Member.Result is null));
        var productionBytes = candidateProduction ? CandidateProductionGrowth(saved.Candidates!, projectedInputs, request) : 0;
        // One possible model record, its submission and the prospective Host follow-on episode.
        var productionRecords = candidateProduction ? 3 : 0;
        if (projectedInputs + remaining.Length + actions + candidateRecords + productionRecords > options.MaximumRecords
            || projectedInputs > bounds.MaximumInputs
            || saved.Members.Length + actions + remaining.Length + candidateRecords > options.MaximumRecords
            || futurePayload > bounds.MaximumRequestBytes || recoveryBytes + productionBytes > options.MaximumRetainedBytes - retained
            || reserveResponse
                && bounds.MaximumResponseBytes > options.MaximumRetainedBytes - retained - recoveryBytes - productionBytes) throw new JsonException();
        if (count >= options.MaximumRecords || count > bounds.MaximumInputs || config.Tools.Count > bounds.MaximumTools
            || payload > bounds.MaximumRequestBytes || (saved.Candidates is null || candidateProduction || !continuing && config.ToolArgumentCorrection is not null) && saved.Rounds.Sum(r => r.Usage.Attempts.Count) >= options.MaximumAttempts
            || retained >= options.MaximumRetainedBytes
            || reserveResponse && bounds.MaximumResponseBytes > options.MaximumRetainedBytes - retained) throw new JsonException();
        var last = saved.Rounds.SelectMany(r => r.Facts).LastOrDefault();
        if (last is null && saved.Candidates is not null)
            return new(saved, [], null, pending, continuing ? saved.Info.LogicalWorkId : Guid.NewGuid(), request.RoundGrant!.RoundId,
                retained, payload, Deferred: true);
        if (saved.Provider is not { } provider || config.Provider is not IProviderContextPersistence || last is null)
            throw new ContextRejected(ContextRejectionCode.UnsupportedContext);
        if (!provider.Scope.Matches(config.Scope) || !provider.Origin.Matches(last.Attempt)) throw new ContextRejected(ContextRejectionCode.IncompatibleContext);
        _ = new ProviderSavedState(provider.Scope, provider.Origin, provider.FormatVersion, provider.Bytes);
        return new(saved, [], null, pending, continuing ? saved.Info.LogicalWorkId : Guid.NewGuid(), request.RoundGrant!.RoundId,
            retained, payload, Deferred: true);
    }

    internal static RestoredOrdinary Materialize(ContextExecutionRequest request, RestoredOrdinary restored,
        RuntimeConfiguration config, RuntimeOptions options)
    {
        if (!restored.Deferred) return restored;
        var saved = restored.Checkpoint;
        ToolExecutionRecord RestoreMember(SavedMember m) => new(m.ModelAttempt, m.Ordinal, m.Call, m.State,
            m.Result is { } result ? ToolResult.RestoreHistorical(config.Tools.Single(t => t.Descriptor.Name == m.Call.ToolName).Descriptor,
                m.Call, result.Call, result.Outcome, result.Error, result.InvocationStarted, result.Json) : null, m.Error, m.ReservationHeld, m.Operation);
        var originals = saved.Members.Select(RestoreMember).ToArray();
        var members = EffectiveMembers(saved, config, options, out _).Select(RestoreMember).ToArray();
        var records = new List<ProviderInput>(); ProviderContinuation? continuation = null;
        foreach (var entry in saved.Records)
        {
            if (entry.Kind is ProviderInputKind.HostInstruction or ProviderInputKind.InputData)
                records.Add(entry.Kind == ProviderInputKind.HostInstruction ? ProviderInput.Instruction(entry.Text!) : ProviderInput.Data(entry.Text!));
            else if (entry.Kind == ProviderInputKind.ToolResultData) records.Add(ProviderInput.FromTool(members[entry.ToolMember!.Value].Result!));
            else if (entry.Kind == ProviderInputKind.ToolCorrectionFeedbackData)
            {
                var feedback = entry.CorrectionFeedback!;
                var model = records.Single(r => r.Model?.Attempt.Matches(feedback.ModelAttempt) == true).Model!;
                records.Add(ProviderInput.FromCorrectionFeedback(model, feedback.Ordinal - 1));
            }
            else
            {
                var candidate = Candidate(entry, config, options);
                var original = new ProviderRequest(config.Scope, candidate.Attempt, records, config.Tools.Select(t => t.Descriptor).ToArray(), continuation,
                    options.RequireContinuation ? ProviderCapabilities.Continuation : ProviderCapabilities.None,
                    ModelBounds(entry), new(records.Where(r => r.Model is not null).Select(r => r.Model!.Attempt).ToArray()), config.ToolArgumentCorrection);
                var accepted = entry.CorrectionModel is { } correction ? RestoreCorrection(correction, original)
                    : candidate.Finish == ProviderFinish.Final ? ProviderResponse.RestoreFinal(original, candidate)
                    : ProviderResponse.RestoreToolCalls(original, candidate);
                records.Add(ProviderInput.FromModel(accepted)); continuation = accepted.Continuation;
            }
        }
        if (request.Intent != ContextExecutionIntent.ContinueRun)
        {
            records[0] = ProviderInput.Instruction(request.Request.Instructions);
            records.AddRange(request.Request.Data.Select(d => ProviderInput.Data(d.Text)));
        }
        var pending = restored.Pending;
        if (request.Intent == ContextExecutionIntent.ContinueRun && saved.ToolCursor is not null
            && members.Any(m => m.ModelAttempt.Matches(saved.ToolCursor) && m.State != ToolMemberState.Succeeded))
            return restored with { Records = records, Continuation = continuation, Members = members, OriginalMembers = originals, Deferred = false };
        var next = new ProviderRequest(config.Scope, new(request.Request.ExecutionId, pending?.LogicalCallId ?? Guid.NewGuid(), Guid.NewGuid(),
            pending is null ? 1 : checked(pending.AttemptNumber + 1)), records, config.Tools.Select(t => t.Descriptor).ToArray(), continuation,
            options.RequireContinuation ? ProviderCapabilities.Continuation : ProviderCapabilities.None,
            pending is null ? config.Bounds : saved.OriginalBounds,
            new(records.Where(r => r.Model is not null).Select(r => r.Model!.Attempt).ToArray(), pending), config.ToolArgumentCorrection);
        if (next.PayloadByteCount != restored.ProspectiveBytes || next.PayloadByteCount != ProspectivePayload(request, saved, config, options)) throw new JsonException();
        return restored with { Records = records, Continuation = continuation, Members = members, OriginalMembers = originals, Deferred = false };
    }
}

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
        + Bytes(t.Descriptor.ResultSchema.NormalizedJson))));
    private static ProviderResponse Candidate(SavedInput input) => input.Final is { } final
        ? new(final.Scope, final.Attempt, ProviderFinish.Final, final.Text, [], final.Continuation?.Restore())
        : input.ToolModel is { } tool
            ? new(tool.Scope, tool.Attempt, ProviderFinish.ToolCalls, tool.Text, tool.Calls, tool.Continuation?.Restore())
            : throw new JsonException();

    // Validate committed chronology separately from ProviderRequest's identity-based result collection contract.
    private static Dictionary<Guid, bool> ValidateToolRecords(OrdinaryCheckpoint saved, RuntimeConfiguration config, RuntimeOptions options)
    {
        if (saved.Members.Length > options.MaximumRecords) throw new JsonException();
        var registry = config.Tools.ToDictionary(t => t.Descriptor.Name, StringComparer.Ordinal);
        var completed = new Dictionary<Guid, bool>();
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        var memberIndex = 0; var resultIndex = 0; var returned = new HashSet<int>();
        SavedToolModel? pending = null;
        foreach (var entry in saved.Records)
        {
            if (!Enum.IsDefined(entry.Kind)) throw new JsonException();
            if (entry.Kind is ProviderInputKind.HostInstruction or ProviderInputKind.InputData)
            {
                if (entry.Text is null || entry.Final is not null || entry.ToolModel is not null || entry.ToolMember is not null
                    || pending is not null && !completed[pending.Attempt.PhysicalAttemptId]) throw new JsonException();
                _ = entry.Kind == ProviderInputKind.HostInstruction ? ProviderInput.Instruction(entry.Text) : ProviderInput.Data(entry.Text);
            }
            else if (entry.Kind == ProviderInputKind.ModelData)
            {
                if (entry.Text is not null || entry.ToolMember is not null || (entry.Final is null) == (entry.ToolModel is null)
                    || pending is not null && !completed[pending.Attempt.PhysicalAttemptId]) throw new JsonException();
                var candidate = Candidate(entry);
                var bounds = entry.Final?.Bounds ?? entry.ToolModel!.Bounds;
                if (!candidate.Scope.Matches(saved.Scope) || candidate.PayloadByteCount > bounds.MaximumResponseBytes
                    || candidate.Calls.Count > bounds.MaximumToolCalls
                    || candidate.Continuation?.ByteCount > bounds.MaximumContinuationBytes) throw new JsonException();
                pending = entry.ToolModel;
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
                if (entry.Text is not null || entry.Final is not null || entry.ToolModel is not null || entry.ToolMember is not { } index
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
            else throw new JsonException();
        }
        if (memberIndex != saved.Members.Length || returned.Count != saved.Members.Count(m => m.Result is not null)) throw new JsonException();
        return completed;
    }

    // The same immutable admitted facts drive checked preclaim accounting and postclaim materialization.
    private static int ProspectivePayload(ContextExecutionRequest request, OrdinaryCheckpoint saved, RuntimeConfiguration config)
    {
        var continuing = request.Intent == ContextExecutionIntent.ContinueRun;
        var total = checked(ScopeBytes(config.Scope) + DefinitionBytes(config));
        foreach (var entry in saved.Records)
        {
            var count = entry.Kind switch
            {
                ProviderInputKind.HostInstruction => Bytes(continuing ? entry.Text : request.Request.Instructions),
                ProviderInputKind.InputData => Bytes(entry.Text),
                ProviderInputKind.ModelData => Candidate(entry).PayloadByteCount,
                ProviderInputKind.ToolResultData => checked(CallBytes(saved.Members[entry.ToolMember!.Value].Result!.Call)
                    + Bytes(saved.Members[entry.ToolMember.Value].Result!.Json)),
                _ => throw new JsonException()
            };
            total = checked(total + count);
        }
        if (!continuing) total = checked(total + request.Request.Data.Sum(d => Bytes(d.Text)));
        var latest = saved.Records.LastOrDefault(r => r.Final is not null || r.ToolModel is not null);
        var continuation = latest?.Final?.Continuation ?? latest?.ToolModel?.Continuation;
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
        var payload = ProspectivePayload(request, saved, config);
        var retained = Math.Max(payload, checked(encodedBytes + DefinitionBytes(config)
            + (continuing ? 0 : Bytes(request.Request.Instructions) + request.Request.Data.Sum(d => Bytes(d.Text)))));
        if (count >= options.MaximumRecords || count > bounds.MaximumInputs || config.Tools.Count > bounds.MaximumTools
            || payload > bounds.MaximumRequestBytes || saved.Rounds.Sum(r => r.Usage.Attempts.Count) >= options.MaximumAttempts
            || retained >= options.MaximumRetainedBytes
            || continuing && bounds.MaximumResponseBytes > options.MaximumRetainedBytes - retained) throw new JsonException();
        var last = saved.Rounds.SelectMany(r => r.Facts).LastOrDefault();
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
        var members = saved.Members.Select(m => new ToolExecutionRecord(m.ModelAttempt, m.Ordinal, m.Call, m.State,
            m.Result is { } result ? ToolResult.RestoreHistorical(config.Tools.Single(t => t.Descriptor.Name == m.Call.ToolName).Descriptor,
                m.Call, result.Call, result.Outcome, result.Error, result.InvocationStarted, result.Json) : null, m.Error, m.ReservationHeld)).ToArray();
        var records = new List<ProviderInput>(); ProviderContinuation? continuation = null;
        foreach (var entry in saved.Records)
        {
            if (entry.Kind is ProviderInputKind.HostInstruction or ProviderInputKind.InputData)
                records.Add(entry.Kind == ProviderInputKind.HostInstruction ? ProviderInput.Instruction(entry.Text!) : ProviderInput.Data(entry.Text!));
            else if (entry.Kind == ProviderInputKind.ToolResultData) records.Add(ProviderInput.FromTool(members[entry.ToolMember!.Value].Result!));
            else
            {
                var candidate = Candidate(entry);
                var original = new ProviderRequest(config.Scope, candidate.Attempt, records, config.Tools.Select(t => t.Descriptor).ToArray(), continuation,
                    options.RequireContinuation ? ProviderCapabilities.Continuation : ProviderCapabilities.None,
                    entry.Final?.Bounds ?? entry.ToolModel!.Bounds, new(records.Where(r => r.Model is not null).Select(r => r.Model!.Attempt).ToArray()));
                var accepted = candidate.Finish == ProviderFinish.Final ? ProviderResponse.RestoreFinal(original, candidate)
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
        var next = new ProviderRequest(config.Scope, new(request.Request.ExecutionId, pending?.LogicalCallId ?? Guid.NewGuid(), Guid.NewGuid(),
            pending is null ? 1 : checked(pending.AttemptNumber + 1)), records, config.Tools.Select(t => t.Descriptor).ToArray(), continuation,
            options.RequireContinuation ? ProviderCapabilities.Continuation : ProviderCapabilities.None,
            pending is null ? config.Bounds : saved.OriginalBounds,
            new(records.Where(r => r.Model is not null).Select(r => r.Model!.Attempt).ToArray(), pending));
        if (next.PayloadByteCount != restored.ProspectiveBytes || next.PayloadByteCount != ProspectivePayload(request, saved, config)) throw new JsonException();
        return restored with { Records = records, Continuation = continuation, Members = members, Deferred = false };
    }
}

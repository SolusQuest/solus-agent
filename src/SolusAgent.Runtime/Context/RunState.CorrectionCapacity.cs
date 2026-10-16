using System.Text;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Context;

namespace SolusAgent.Runtime.Execution;

internal sealed partial class RunState
{
    private SavedInput[] SaveInputs(IReadOnlyList<ProviderInput> inputs, IReadOnlyList<SavedAttempt> facts)
    {
        return inputs.Select(entry =>
        {
            var model = entry.Model;
            var bounds = model is null ? null : facts.Single(f => f.Attempt.Matches(model.Attempt)).Bounds;
            var member = entry.ToolResult is null ? -1 : toolRecords.FindIndex(m => m.Call.Matches(entry.ToolResult.Call));
            if (entry.ToolResult is not null && member < 0) throw new InvalidOperationException();
            var correction = model?.Correction;
            return new SavedInput(entry.Kind, entry.Text, model?.Finish == ProviderFinish.Final
                ? new(model.Scope, model.Attempt, model.Text!, SavedContinuation.From(model.Continuation), bounds!) : null,
                model?.Finish == ProviderFinish.ToolCalls
                    ? new(model.Scope, model.Attempt, model.Text, model.Calls.ToArray(), SavedContinuation.From(model.Continuation), bounds!) : null,
                member < 0 ? null : member,
                correction is null ? null : new(model!.Scope, model.Attempt, model.Text, SavedContinuation.From(model.Continuation), bounds!, correction.Origin,
                    correction.Members.Select((m, i) => new SavedCorrectionMember(i + 1, m.Call, m.OriginalArgumentBytes, m.ErrorCode, m.SchemaError)).ToArray(),
                    correction.OriginalResponseBytes, correction.DefinitionFingerprint),
                entry.CorrectionFeedback is not { } feedback ? null : new(feedback.Attempt, feedback.Ordinal + 1));
        }).ToArray();
    }

    private SavedMember[] SaveMembers() => originalToolRecords.Select(m => new SavedMember(m.ModelAttempt, m.Ordinal, m.Call,
        SavedDescriptor.From(Configuration.Tools.Single(t => t.Descriptor.Name == m.Call.ToolName).Descriptor),
        m.State, SavedResult.From(m.Result), m.Error, m.ReservationHeld, m.Operation!)).ToArray();

    private int CorrectionCheckpointBytes(ProviderInput[] prospective, SavedAttempt[] facts, int nextAdmissions, SavedCandidates? candidates)
    {
        if (!ContextMode) return 0;
        var current = Usage();
        var usage = new AgentRunUsage(current.ExecutionId, current.Coverage, current.Attempts, current.ToolInvocations,
            current.Accounting, current.ContinuedCalls, new(nextAdmissions));
        var round = new SavedRound(logicalWork, contextRound, usage, AgentTerminationReason.ResourceLimit, Completed,
            Enum.GetValues<RuntimeStop>().Max(), facts, null,
            recoveryActions.Select(a => new SavedRecoveryAction(a.ActionId, a.MemberIndex, a.Kind, a.State, SavedResult.From(a.Result), a.Error)).ToArray(),
            recoveryReleased.ToArray(), candidates is null ? null : new(Enum.GetValues<CandidateStopReason>().Max(), int.MaxValue, int.MaxValue, int.MaxValue));
        // Bound opaque provider state by its existing maximum, without calling export or installing a fictitious state.
        // These placeholders are used only for serializer length, never accepted or emitted as an envelope.
        var provider = Configuration.Provider is IProviderContextPersistence
            ? new SavedProvider(Configuration.Scope, latestRequest!.Attempt, int.MaxValue, Enumerable.Repeat((byte)0xfb, ProviderLimits.ContinuationBytes).ToArray()) : null;
        var allFacts = facts.Concat(restored?.Checkpoint.Rounds.SelectMany(r => r.Facts) ?? []).ToArray();
        var checkpoint = new OrdinaryCheckpoint(new(Guid.NewGuid(), logicalWork, contextRound, Request.ExecutionId), Configuration.Scope,
            OrdinaryContextCodec.Binding(Configuration, Options), SaveInputs(prospective, allFacts),
            [.. restored?.Checkpoint.Rounds ?? [], round], latestRequest!.Bounds, provider, false, false, null, TimeSpan.MaxValue,
            SaveMembers(), null, candidates);
        var definitions = Configuration.Tools.Sum(t => Encoding.UTF8.GetByteCount(t.Descriptor.Name) + Encoding.UTF8.GetByteCount(t.Descriptor.Description)
            + Encoding.UTF8.GetByteCount(t.Descriptor.CapabilityId) + Encoding.UTF8.GetByteCount(t.Descriptor.InputSchema.NormalizedJson)
            + Encoding.UTF8.GetByteCount(t.Descriptor.ResultSchema.NormalizedJson)) + (Configuration.ToolArgumentCorrection?.PayloadByteCount ?? 0);
        return checked(OrdinaryContextCodec.Measure(checkpoint) + definitions);
    }
}

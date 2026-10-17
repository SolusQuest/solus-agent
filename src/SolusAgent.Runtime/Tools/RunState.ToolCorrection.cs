using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Context;

namespace SolusAgent.Runtime.Execution;

internal sealed record PreparedToolCorrection(ProviderResponse Model, ProviderInput[] Entries, int FactIndex,
    SavedAttempt ExpectedFact, SavedAttempt Fact, int ExpectedRecords, int ExpectedBytes, int ExpectedAdmissions,
    int NextBytes, int NextAdmissions);

internal sealed partial class RunState
{
    internal PreparedToolCorrection? PrepareToolCorrection(ProviderRequest request, ProviderResponse response, SavedCandidates? candidates)
    {
        if (!CanContinue) return null;
        response.ValidateFor(request);
        if (response is not { Accepted: true, Finish: ProviderFinish.ToolCorrection, Correction: not null }
            || latestRequest?.Attempt.Matches(response.Attempt) != true || !producedResponses.ContainsKey(response.Attempt.PhysicalAttemptId)
            || CommittedFinish(response.Attempt) is not null)
            throw new ProviderContractException(ProviderError.InvalidAssociation);
        var factIndex = contextFacts.FindIndex(f => f.Attempt.Matches(response.Attempt));
        if (factIndex < 0) throw new ProviderContractException(ProviderError.InvalidAssociation);
        var original = contextFacts[factIndex];
        if (original.Stop != RuntimeStop.None || original.SettlementStop != RuntimeStop.None
            || Configuration.Hooks is not null && !original.ClosureAcknowledged)
            throw new ProviderContractException(ProviderError.InvalidAssociation);
        // Correction is useful only if the same current run can admit its next ordinary model production.
        if (!PreflightTurn()) return null;
        var count = response.Correction.Members.Count;
        if (records.Count + candidateMetadataRecords + RecoveryRecordCount > Options.MaximumRecords - count - 2
            || Request.UsageLimits?.MaximumToolCallAdmissions is { } limit && count > limit - admittedToolCalls)
        { Close(RuntimeStop.ResourceLimit); return null; }
        var entries = new ProviderInput[count + 1];
        entries[0] = ProviderInput.FromModel(response);
        for (var i = 0; i < count; i++) entries[i + 1] = ProviderInput.FromCorrectionFeedback(response, i);
        var prospective = records.Concat(entries).ToArray();
        // A probe has no attempt/dispatch reservation and never enters the observation ledger.
        var probeAttempt = new ProviderAttempt(Request.ExecutionId, Guid.NewGuid(), Guid.NewGuid(), 1);
        var probe = new ProviderRequest(Configuration.Scope, probeAttempt, prospective, Configuration.Tools.Select(t => t.Descriptor).ToArray(),
            response.Continuation, Options.RequireContinuation ? ProviderCapabilities.Continuation : ProviderCapabilities.None,
            Configuration.Bounds, RequestHistory(probeAttempt), Configuration.ToolArgumentCorrection);
        var nextAdmissions = checked(admittedToolCalls + count);
        var nextBytes = Math.Max(checked(retainedBytes + entries.Sum(e => e.PayloadByteCount)), probe.PayloadByteCount);
        var fact = original with { AcceptedCorrection = true, AcceptedTools = false, AcceptedFinal = false };
        var facts = contextFacts.ToArray(); facts[factIndex] = fact;
        var measured = Math.Max(nextBytes, CorrectionCheckpointBytes(prospective, facts, nextAdmissions, candidates));
        if (measured > Options.MaximumRetainedBytes - Configuration.Bounds.MaximumResponseBytes)
        { Close(RuntimeStop.ResourceLimit); return null; }
        var error = ((IProviderRequestValidation)Configuration.Provider).ValidateRequest(probe);
        if (!Enum.IsDefined(error)) error = ProviderError.InvalidInput;
        if (error != ProviderError.None) throw new ProviderContractException(error);
        if (!CanContinue) return null;
        records.EnsureCapacity(prospective.Length);
        return new(response, entries, factIndex, original, fact, records.Count, retainedBytes, admittedToolCalls, nextBytes, nextAdmissions);
    }

    internal bool CommitToolCorrection(PreparedToolCorrection value)
    {
        if (records.Count != value.ExpectedRecords || retainedBytes != value.ExpectedBytes || admittedToolCalls != value.ExpectedAdmissions
            || !ReferenceEquals(contextFacts[value.FactIndex], value.ExpectedFact)) return false;
        records.AddRange(value.Entries);
        contextFacts[value.FactIndex] = value.Fact;
        retainedBytes = value.NextBytes; admittedToolCalls = value.NextAdmissions;
        latestResponse = value.Model; continuation = value.Model.Continuation; toolBatchClaimed = true;
        return true;
    }
}

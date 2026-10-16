using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Context;
using SolusAgent.Runtime.Tools;
using SolusAgent.Tools.Api;

namespace SolusAgent.Runtime.Execution;

internal sealed record PreparedToolBatch(ProviderResponse Response, ToolExecutionRecord[] Members, ProviderInput? Model,
    int First, int ExpectedRecords, int ExpectedBytes, int ExpectedAdmissions, int ExpectedReserved,
    int NextBytes, int NextAdmissions, int NextReserved, int FactIndex, SavedAttempt? Fact);

internal sealed partial class RunState
{
    // Production work and retained history are distinct. No raw response is stored by this numeric commit.
    internal void RecordProduction(ProviderRequest request, ProviderResponse response)
    {
        if (!response.Accepted) throw new ProviderContractException(ProviderError.InvalidResponse);
        response.ValidateFor(request);
        if (!producedResponses.TryAdd(request.Attempt.PhysicalAttemptId, response.Finish))
            throw new ProviderContractException(ProviderError.InvalidAssociation);
        Completed++;
    }

    private ProviderFinish? CommittedFinish(ProviderAttempt attempt) => records.SingleOrDefault(r => r.Model?.Attempt.Matches(attempt) == true)?.Model?.Finish;

    internal PreparedToolBatch? PrepareToolBatch(ProviderResponse response, IReadOnlyList<RuntimeToolRegistration> bindings,
        IReadOnlyList<ToolOperation> operations)
    {
        var count = response.Calls.Count;
        var deferred = !records.Any(r => ReferenceEquals(r.Model, response));
        if (deferred && (Configuration.ToolArgumentCorrection is null || latestRequest?.Attempt.Matches(response.Attempt) != true
            || !producedResponses.ContainsKey(response.Attempt.PhysicalAttemptId)))
        { Close(RuntimeStop.InvalidAssociation); return null; }
        if (count == 0 || count != bindings.Count || count != operations.Count || count > Configuration.Bounds.MaximumToolCalls
            || records.Count + candidateMetadataRecords + RecoveryRecordCount + (deferred ? 1 : 0) > Options.MaximumRecords - count
            || toolRecords.Count + candidateMetadataRecords + RecoveryRecordCount > Options.MaximumRecords - count
            || Request.UsageLimits?.MaximumToolCallAdmissions is { } admissions && count > admissions - admittedToolCalls)
        { Close(RuntimeStop.ResourceLimit); return null; }
        var modelBytes = deferred ? response.PayloadByteCount : 0;
        long reservation = modelBytes;
        for (var i = 0; i < count; i++) reservation += ToolCallBytes(response.Calls[i]) + bindings[i].Descriptor.MaximumResultBytes;
        if (reservation > Options.MaximumRetainedBytes - retainedBytes)
        { Close(RuntimeStop.ResourceLimit); return null; }
        lock (toolUsageGate)
        {
            if (Request.UsageLimits?.MaximumToolInvocations is { } limit && count > limit - invokedTools - reservedTools)
            { Close(RuntimeStop.ResourceLimit); return null; }
            var first = toolRecords.Count;
            var members = response.Calls.Select((call, i) => new ToolExecutionRecord(response.Attempt, i + 1, call,
                ToolMemberState.Unstarted, ReservationHeld: true, Operation: operations[i])).ToArray();
            var factIndex = deferred ? contextFacts.FindIndex(f => f.Attempt.Matches(response.Attempt)) : -1;
            if (deferred && factIndex < 0) { Close(RuntimeStop.InvalidAssociation); return null; }
            var fact = factIndex < 0 ? null : contextFacts[factIndex] with { AcceptedTools = true, AcceptedCorrection = false };
            records.EnsureCapacity(records.Count + (deferred ? 1 : 0));
            toolRecords.EnsureCapacity(first + count); originalToolRecords.EnsureCapacity(first + count);
            return new(response, members, deferred ? ProviderInput.FromModel(response) : null,
                first, records.Count, retainedBytes, admittedToolCalls, reservedTools,
                checked(retainedBytes + modelBytes), checked(admittedToolCalls + count), checked(reservedTools + count), factIndex, fact);
        }
    }

    // Every allocation/factory/check happened before the gate. Stamp mismatch fails before any mutation.
    internal bool CommitToolBatch(PreparedToolBatch value)
    {
        lock (toolUsageGate)
        {
            if (toolRecords.Count != value.First || records.Count != value.ExpectedRecords || retainedBytes != value.ExpectedBytes
                || admittedToolCalls != value.ExpectedAdmissions || reservedTools != value.ExpectedReserved) return false;
            if (value.Model is not null)
            {
                records.Add(value.Model); latestResponse = value.Response; continuation = value.Response.Continuation;
                contextFacts[value.FactIndex] = value.Fact!;
            }
            toolRecords.AddRange(value.Members); originalToolRecords.AddRange(value.Members);
            retainedBytes = value.NextBytes; admittedToolCalls = value.NextAdmissions; reservedTools = value.NextReserved;
            toolBatchClaimed = true;
            return true;
        }
    }
}

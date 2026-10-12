using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Execution;
using SolusAgent.Tools.Api;

namespace SolusAgent.Runtime.Tools;

// One bounded recovery phase, before any new provider production. No query-to-retry fallthrough.
internal static class PartialToolBatchOperation
{
    private sealed record Pending(int Index, RuntimeToolRegistration Binding, ToolOperation Operation,
        RecoveryActionKind Kind, PreparedToolInvocation? Prepared);
    private sealed record Completion(ToolResult? Result = null, ToolRecoveryReceipt? Receipt = null);

    internal static async ValueTask<bool> ResumeAsync(RunState state)
    {
        var cursor = state.RestoredToolCursor;
        if (cursor is null) return true;
        var cut = state.Cut;
        var pending = new List<Pending>();
        var members = state.ToolRecords;
        try
        {
            for (var index = 0; index < members.Count; index++)
            {
                var member = members[index];
                if (!member.ModelAttempt.Matches(cursor) || member.State == ToolMemberState.Succeeded) continue;
                if (!state.CanContinue) return false;
                var binding = state.Configuration.Tools.Single(t => t.Descriptor.Name == member.Call.ToolName);
                var operation = member.Operation ?? throw new ToolContractException(ToolError.InvalidMetadata);
                var kind = member.State == ToolMemberState.Unstarted ? RecoveryActionKind.Start
                    : operation.Recovery?.Guarantee == ToolRecoveryGuarantee.ConcurrentSafeRepeat ? RecoveryActionKind.Repeat : RecoveryActionKind.Query;
                if (kind != RecoveryActionKind.Start && operation.Recovery is null) return Block();
                if (!Current(binding, operation)) return Block();
                PreparedToolInvocation? handle = null;
                if (kind != RecoveryActionKind.Query)
                {
                    var preparation = binding.Tool.Prepare(member.Call);
                    handle = preparation?.Prepared;
                    if (handle is null || preparation!.Error != ToolError.None
                        || !handle.Call.Matches(member.Call)
                        || binding.Tool.ValidateInvocation(handle, member.Call, binding.Capability) != ToolError.None) return Block();
                }
                pending.Add(new(index, binding, operation, kind, handle));
            }
        }
        catch (Exception exception) when (Recoverable(exception)) { return Block(); }
        if (pending.Count == 0) return true;
        var indices = pending.Select(p => p.Index).ToArray();
        var reserved = false;
        if (!cut.TryCommit(() => reserved = state.ReserveRecoveryBatch(indices)) || !reserved) return false;
        try
        {
            foreach (var item in pending)
            {
                if (!state.CanContinue) return false;
                var action = -1;
                try
                {
                    if (!cut.TryStart(() =>
                    {
                        if (!Current(item.Binding, item.Operation)
                            || item.Prepared is not null && item.Binding.Tool.ValidateInvocation(item.Prepared, item.Operation.Call, item.Binding.Capability) != ToolError.None)
                            throw new ToolContractException(ToolError.UnsupportedCapability);
                        action = state.EnterRecovery(item.Index, item.Kind);
                        return Invoke(item, cut.Token);
                    }, out var work)) return false;
                    var completed = await cut.WaitAsync(work!).ConfigureAwait(false);
                    if (!completed.Obtained) return false;
                    if (!Current(item.Binding, item.Operation)) throw new ToolContractException(ToolError.UnsupportedCapability);
                    ToolResult result;
                    if (item.Kind == RecoveryActionKind.Query)
                    {
                        var receipt = completed.Value!.Receipt;
                        if (receipt is null || !item.Operation.Matches(receipt.Operation))
                            throw new ToolContractException(ToolError.ResultMismatch);
                        if (receipt.RecordedCall is null)
                        {
                            if (!cut.TryCommit(() => state.FinishRecovery(action, RecoveryActionState.Unresolved))) return false;
                            return Block();
                        }
                        result = ToolResult.RestoreHistorical(item.Binding.Descriptor, item.Operation.Call, receipt.RecordedCall,
                            receipt.Outcome!.Value, receipt.Error!.Value, receipt.InvocationStarted!.Value, receipt.Json);
                    }
                    else
                    {
                        result = completed.Value!.Result!;
                        var error = ToolBatchOperation.ValidateResult(result, item.Operation.Call, item.Binding.Descriptor);
                        if (error != ToolError.None) throw new ToolContractException(error);
                    }
                    if (!cut.TryCommit(() => state.FinishRecovery(action, RecoveryActionState.Returned, result, result.Error))) return false;
                    if (result.Outcome != ToolOutcome.Succeeded) return Block();
                }
                catch (Exception exception) when (Recoverable(exception))
                {
                    if (cut.Check() != RuntimeStop.None) return false;
                    if (action >= 0 && !cut.TryCommit(() => state.FinishRecovery(action,
                        exception is ToolContractException ? RecoveryActionState.Rejected : RecoveryActionState.Failed,
                        error: exception is ToolContractException contract ? contract.Error : ToolError.InvocationFailed))) return false;
                    return Block();
                }
            }
            return true;
        }
        finally { state.ReleaseRecoveryBatch(indices); }

        bool Block() { state.Close(RuntimeStop.InvalidAssociation); return false; }
    }
    private static bool Current(RuntimeToolRegistration binding, ToolOperation operation) =>
        ToolBatchOperation.SameDescriptor(binding.Descriptor, binding.Tool.Descriptor)
        && binding.Capability.CapabilityId == binding.Descriptor.CapabilityId
        && binding.Descriptor.InputSchema.Validate(operation.Call.ArgumentsJson, binding.Descriptor.MaximumArgumentBytes) == ToolError.None
        && (operation.Recovery is null ? binding.Recovery is null
            : operation.Recovery.Matches(binding.Recovery) && binding.Tool is IRecoverableFunctionTool recovery
                && recovery.ValidateOperation(operation, binding.Capability) == ToolError.None);
    private static async Task<Completion> Invoke(Pending item, CancellationToken token)
    {
        if (item.Kind == RecoveryActionKind.Query)
            return new(Receipt: await ((IRecoverableFunctionTool)item.Binding.Tool).QueryOutcomeAsync(item.Operation, item.Binding.Capability, token).ConfigureAwait(false));
        return new(Result: item.Operation.Recovery is null
            ? await item.Binding.Tool.InvokeAsync(item.Prepared!, item.Operation.Call, item.Binding.Capability, token).ConfigureAwait(false)
            : await ((IRecoverableFunctionTool)item.Binding.Tool).InvokeOperationAsync(item.Prepared!, item.Operation, item.Binding.Capability, token).ConfigureAwait(false));
    }
    private static bool Recoverable(Exception exception) => exception is not (OutOfMemoryException or StackOverflowException);
}

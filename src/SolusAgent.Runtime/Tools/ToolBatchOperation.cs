using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Execution;
using SolusAgent.Tools.Api;

namespace SolusAgent.Runtime.Tools;

internal sealed record ToolBatchExecution(ToolError Error, RuntimeStop Stop);

// One metadata-driven adapter for all compatible interfaces; no tool name grants authority.
internal static class ToolBatchOperation
{
    public static async ValueTask<ToolBatchExecution> ExecuteAsync(RunState state, ProviderRequest request, ProviderResponse response)
    {
        var cut = state.Cut;
        if (!state.CanContinue) return Stopped();
        var claimed = false;
        try
        {
            if (!response.Accepted) return Fail(ToolError.CallMismatch);
            response.ValidateFor(request);
            if (!cut.TryCommit(() => claimed = state.ClaimToolBatch(response))) return Stopped();
            if (!claimed) return Fail(ToolError.AlreadyInvoked);
        }
        catch (ProviderContractException exception) { return Fail(exception.Error == ProviderError.LimitExceeded ? ToolError.LimitExceeded : ToolError.CallMismatch); }

        var registry = state.Configuration.Tools.ToDictionary(binding => binding.Descriptor.Name, StringComparer.Ordinal);
        var bindings = new List<RuntimeToolRegistration>();
        var prepared = new List<PreparedToolInvocation>();
        try
        {
            foreach (var call in response.Calls)
            {
                if (!state.CanContinue) return Stopped();
                if (!registry.TryGetValue(call.ToolName, out var binding)) return Fail(ToolError.CallMismatch);
                var descriptor = binding.Descriptor;
                if (!SameDescriptor(descriptor, binding.Tool.Descriptor)) return Fail(ToolError.InvalidMetadata);
                if (descriptor.InputSchema.Profile != "closed_scalar_object" || descriptor.ResultSchema.Profile != "closed_scalar_object")
                    return Fail(ToolError.UnsupportedSchema);
                var argumentError = descriptor.InputSchema.Validate(call.ArgumentsJson, descriptor.MaximumArgumentBytes);
                if (argumentError != ToolError.None) return Fail(argumentError);
                var preparation = binding.Tool.Prepare(call);
                if (preparation?.Prepared is not { } handle) return Fail(preparation?.Error ?? ToolError.DomainRejected);
                if (preparation.Error != ToolError.None || !handle.Call.Matches(call)) return Fail(ToolError.CallMismatch);
                var admission = binding.Tool.ValidateInvocation(handle, call, binding.Capability);
                if (admission != ToolError.None) return Fail(admission);
                bindings.Add(binding); prepared.Add(handle);
            }
        }
        catch (ToolContractException exception) { return Fail(exception.Error); }
        catch (Exception exception) when (Recoverable(exception)) { return Fail(ToolError.InvocationFailed); }

        var first = -1;
        if (!cut.TryCommit(() => first = state.ReserveToolBatch(response, bindings))) return Stopped();
        if (first < 0) return Fail(ToolError.LimitExceeded);
        try
        {
            for (var i = 0; i < prepared.Count; i++)
            {
                if (!state.CanContinue) return Stopped();
                var index = first + i;
                var binding = bindings[i]; var call = response.Calls[i];
                Task<ToolResult>? pending = null;
                try
                {
                    // Authority is checked again by the tool guard; preflight never grants lasting permission.
                    if (!cut.TryStart(() =>
                    {
                        state.EnterTool(index);
                        return binding.Tool.InvokeAsync(prepared[i], call, binding.Capability, cut.Token).AsTask();
                    }, out pending)) return Stopped();
                    var completion = await cut.WaitAsync(pending!).ConfigureAwait(false);
                    if (!completion.Obtained) return Stopped();
                    var result = completion.Value;
                    var error = ValidateResult(result, call, binding.Descriptor);
                    if (error != ToolError.None)
                    {
                        if (!cut.TryCommit(() => state.RejectToolResult(index, error))) return Stopped();
                        return Fail(error);
                    }
                    if (!cut.TryCommit(() => state.CompleteTool(index, result!))) return Stopped();
                    if (result!.Outcome != ToolOutcome.Succeeded) return Fail(result.Error);
                }
                catch (Exception exception) when (Recoverable(exception))
                {
                    if (cut.Check() != RuntimeStop.None) return Stopped();
                    if (!cut.TryCommit(() => state.FailToolInvocation(index))) return Stopped();
                    return Fail(ToolError.InvocationFailed);
                }
            }
            return new(ToolError.None, RuntimeStop.None);
        }
        finally { state.ReleaseToolBatch(first, prepared.Count); }

        ToolBatchExecution Fail(ToolError error)
        {
            if (!Enum.IsDefined(error) || error == ToolError.None) error = ToolError.InvocationFailed;
            if (cut.Check() != RuntimeStop.None) return Stopped();
            state.Close(error == ToolError.LimitExceeded ? RuntimeStop.ResourceLimit : RuntimeStop.InvalidAssociation);
            return new(error, state.AdmissionStop);
        }
        ToolBatchExecution Stopped()
        {
            var stop = cut.Check();
            if (stop == RuntimeStop.None) stop = state.AdmissionStop;
            if (stop != RuntimeStop.None) state.Close(stop);
            return new(ToolError.None, stop);
        }
    }

    private static ToolError ValidateResult(ToolResult? result, ToolCall call, ToolDescriptor descriptor)
    {
        if (result is null) return ToolError.InvalidResult;
        if (!result.Call.Matches(call)) return ToolError.ResultMismatch;
        if (!Enum.IsDefined(result.Outcome) || !Enum.IsDefined(result.Error)) return ToolError.InvalidResult;
        if (result.Outcome == ToolOutcome.Succeeded)
            return result.Error == ToolError.None && result.InvocationStarted && result.Json is not null
                && descriptor.ResultSchema.Validate(result.Json, descriptor.MaximumResultBytes) == ToolError.None
                ? ToolError.None : ToolError.InvalidResult;
        return result.Error != ToolError.None && result.Json is null
            && (result.Outcome != ToolOutcome.Cancelled || result.Error == ToolError.Cancelled)
            ? ToolError.None : ToolError.InvalidResult;
    }
    private static bool SameDescriptor(ToolDescriptor expected, ToolDescriptor? actual) => actual is not null
        && expected.Name == actual.Name && expected.Description == actual.Description && expected.CapabilityId == actual.CapabilityId
        && expected.Effect == actual.Effect && expected.MaximumArgumentBytes == actual.MaximumArgumentBytes
        && expected.MaximumResultBytes == actual.MaximumResultBytes && expected.InputSchema.Profile == actual.InputSchema.Profile
        && expected.ResultSchema.Profile == actual.ResultSchema.Profile && expected.InputSchema.NormalizedJson == actual.InputSchema.NormalizedJson
        && expected.ResultSchema.NormalizedJson == actual.ResultSchema.NormalizedJson;
    private static bool Recoverable(Exception exception) => exception is not (OutOfMemoryException or StackOverflowException);
}

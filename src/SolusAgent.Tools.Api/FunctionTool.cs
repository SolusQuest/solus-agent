using System.Text.Json;

namespace SolusAgent.Tools.Api;

/// <summary>
/// A guarded reusable implementation seam with effect-free preparation, explicit narrow capability
/// admission, exact association and a single atomic dispatch per prepared handle. This is not a sandbox,
/// scheduler, retry engine, transaction, or distributed exactly-once guarantee.
/// </summary>
/// <typeparam name="TCapability">The concrete host-owned narrow capability required by this tool.</typeparam>
public abstract class FunctionTool<TCapability> : IFunctionTool where TCapability : class, IToolCapability
{
    /// <summary>Constructs a tool with immutable supported metadata.</summary>
    protected FunctionTool(ToolDescriptor descriptor) => Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
    /// <inheritdoc />
    public ToolDescriptor Descriptor { get; }

    /// <inheritdoc />
    public ToolPreparation Prepare(ToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.ToolName != Descriptor.Name) { return new(null, ToolError.CallMismatch); }
        var error = Descriptor.InputSchema.Validate(call.ArgumentsJson, Descriptor.MaximumArgumentBytes);
        if (error != ToolError.None) { return new(null, error); }
        try
        {
            using var document = ToolBoundary.Json(call.ArgumentsJson, Descriptor.MaximumArgumentBytes);
            return ValidateArguments(document.RootElement)
                ? new(new(this, call), ToolError.None) : new(null, ToolError.DomainRejected);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return new(null, ToolError.DomainRejected);
        }
    }

    /// <summary>Optional bounded, effect-free domain validation. Do not retain the transient JsonElement or invoke effects.</summary>
    protected virtual bool ValidateArguments(JsonElement arguments) => true;

    /// <inheritdoc />
    public async ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation prepared, ToolCall expectedCall,
        IToolCapability? capability, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(expectedCall);
        if (!ReferenceEquals(prepared.Owner, this)) { return Reject(expectedCall, ToolError.PreparedMismatch); }
        if (!prepared.Call.Matches(expectedCall)) { return Reject(expectedCall, ToolError.CallMismatch); }
        try
        {
            if (capability is not TCapability typed || typed.CapabilityId != Descriptor.CapabilityId)
            { return Reject(expectedCall, ToolError.UnsupportedCapability); }
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        { return Reject(expectedCall, ToolError.UnsupportedCapability); }
        if (cancellationToken.IsCancellationRequested) { return new(expectedCall, ToolOutcome.Cancelled, ToolError.Cancelled, false); }
        if (!prepared.TryClaim()) { return Reject(expectedCall, ToolError.AlreadyInvoked); }
        var started = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            started = true;
            var output = await InvokeCoreAsync(prepared.Call, (TCapability)capability!, cancellationToken).ConfigureAwait(false);
            if (output is null) { return Reject(expectedCall, ToolError.InvalidResult, true); }
            if (!output.Call.Matches(expectedCall)) { return Reject(expectedCall, ToolError.ResultMismatch, true); }
            if (output.Error != ToolError.None) { return new(expectedCall, ToolOutcome.Failed, output.Error, true); }
            if (output.Json is null || Descriptor.ResultSchema.Validate(output.Json, Descriptor.MaximumResultBytes) != ToolError.None)
            { return Reject(expectedCall, ToolError.InvalidResult, true); }
            return new(expectedCall, ToolOutcome.Succeeded, ToolError.None, true, output.Json);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested && exception.CancellationToken == cancellationToken)
        { return new(expectedCall, ToolOutcome.Cancelled, ToolError.Cancelled, started); }
        catch (ToolContractException) { return Reject(expectedCall, ToolError.InvalidResult, started); }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        { return new(expectedCall, ToolOutcome.Failed, ToolError.InvocationFailed, started); }
    }

    /// <summary>Invokes only after explicit admission. Return the original call association and pass cancellation to the actual effect seam.</summary>
    protected abstract ValueTask<ToolOutput> InvokeCoreAsync(ToolCall call, TCapability capability, CancellationToken cancellationToken);

    private static ToolResult Reject(ToolCall call, ToolError error, bool started = false) => new(call, ToolOutcome.Rejected, error, started);
}

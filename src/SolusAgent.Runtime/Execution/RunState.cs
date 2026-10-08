using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Runtime.Tools;
using SolusAgent.Tools.Api;
using System.Text;

namespace SolusAgent.Runtime.Execution;

internal sealed class RunState(AgentRequest request, RuntimeConfiguration configuration, RuntimeOptions options, RunCut cut)
{
    private readonly List<ProviderInput> records = [];
    private readonly List<UsageAttemptObservation> attempts = [];
    private int retainedBytes;
    private ProviderContinuation? continuation;
    private ProviderResponse? latestResponse;
    private bool toolBatchClaimed;
    private readonly List<ToolExecutionRecord> toolRecords = [];
    public AgentRequest Request { get; } = request;
    public RuntimeConfiguration Configuration { get; } = configuration;
    public RuntimeOptions Options { get; } = options;
    public RunCut Cut { get; } = cut;
    public int Completed { get; private set; }
    public RuntimeStop AdmissionStop { get; private set; }
    public bool CanContinue => AdmissionStop == RuntimeStop.None && Cut.Check() == RuntimeStop.None;
    internal IReadOnlyList<ProviderInput> Records => records.ToArray();
    internal IReadOnlyList<ToolExecutionRecord> ToolRecords => toolRecords.ToArray();

    public void Initialize()
    {
        if (Request.Data.Count >= Configuration.Bounds.MaximumInputs || Request.Data.Count >= Options.MaximumRecords)
            throw new ProviderContractException(ProviderError.LimitExceeded);
        records.Add(ProviderInput.Instruction(Request.Instructions));
        foreach (var input in Request.Data) records.Add(ProviderInput.Data(input.Text));
    }
    public ProviderRequest? AdmitTurn()
    {
        if (!CanContinue) return null;
        if (Completed >= Request.Bounds.MaximumWorkUnits || attempts.Count >= Options.MaximumAttempts
            || records.Count >= Options.MaximumRecords)
        { Close(RuntimeStop.ResourceLimit); return null; }
        var attempt = new ProviderAttempt(Request.ExecutionId, Guid.NewGuid(), Guid.NewGuid());
        var required = Options.RequireContinuation ? ProviderCapabilities.Continuation : ProviderCapabilities.None;
        var original = Configuration.CreateRequest(attempt, records, continuation, required);
        if (attempts.Count == 0) retainedBytes = original.PayloadByteCount;
        var available = Options.MaximumRetainedBytes - retainedBytes;
        if (available <= 0) { Close(RuntimeStop.ResourceLimit); return null; }
        var b = Configuration.Bounds;
        var bounded = new ProviderExchangeBounds(b.MaximumInputs, b.MaximumTools, b.MaximumToolCalls, b.MaximumRequestBytes,
            Math.Min(b.MaximumResponseBytes, available), b.MaximumContinuationBytes);
        var admitted = new ProviderRequest(original.Scope, attempt, records, original.Tools, continuation, required, bounded);
        if ((admitted.RequiredCapabilities & ~Configuration.Provider.Capabilities) != 0)
            throw new ProviderContractException(ProviderError.UnsupportedCapability);
        attempts.Add(new(attempt.ExecutionId, attempt.LogicalCallId, attempt.PhysicalAttemptId, 1, DispatchExposure.NotDispatched, new()));
        return admitted;
    }
    public void Retain(ProviderAttempt attempt, UsageAttemptObservation observation)
    {
        if (!Matches(attempt, observation)) throw new ProviderContractException(ProviderError.InvalidAssociation);
        var index = attempts.FindIndex(item => item.PhysicalAttemptId == attempt.PhysicalAttemptId);
        if (index < 0) throw new ProviderContractException(ProviderError.InvalidAssociation);
        attempts[index] = observation;
    }
    // Host correction is data, charged once to retained state; reserve the next accepted response slot.
    public void AppendCandidateCorrection(string text)
    {
        var data = ProviderInput.Data(text);
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (records.Count >= Options.MaximumRecords - 1 || records.Count >= Configuration.Bounds.MaximumInputs
            || bytes > Options.MaximumRetainedBytes - retainedBytes)
            throw new ProviderContractException(ProviderError.LimitExceeded);
        records.Add(data);
        retainedBytes += bytes;
    }
    public void Accept(ProviderRequest providerRequest, ProviderResponse response)
    {
        if (!response.Accepted) throw new ProviderContractException(ProviderError.InvalidResponse);
        response.ValidateFor(providerRequest);
        if (records.Count >= Options.MaximumRecords || response.PayloadByteCount > Options.MaximumRetainedBytes - retainedBytes)
            throw new ProviderContractException(ProviderError.LimitExceeded);
        records.Add(ProviderInput.FromModel(response));
        retainedBytes += response.PayloadByteCount; continuation = response.Continuation; Completed++;
        latestResponse = response; toolBatchClaimed = false;
    }
    public bool ClaimToolBatch(ProviderResponse response)
    {
        if (!ReferenceEquals(latestResponse, response) || toolBatchClaimed || response.Finish != ProviderFinish.ToolCalls) return false;
        toolBatchClaimed = true;
        return true;
    }
    public int ReserveToolBatch(ProviderResponse response, IReadOnlyList<RuntimeToolRegistration> bindings)
    {
        var count = response.Calls.Count;
        if (count == 0 || count != bindings.Count || count > Configuration.Bounds.MaximumToolCalls
            || records.Count > Options.MaximumRecords - count || toolRecords.Count > Options.MaximumRecords - count)
        { Close(RuntimeStop.ResourceLimit); return -1; }
        long reservation = 0;
        for (var i = 0; i < count; i++) reservation += ToolCallBytes(response.Calls[i]) + bindings[i].Descriptor.MaximumResultBytes;
        if (reservation > Options.MaximumRetainedBytes - retainedBytes)
        { Close(RuntimeStop.ResourceLimit); return -1; }
        var first = toolRecords.Count;
        for (var i = 0; i < count; i++) toolRecords.Add(new(response.Attempt, i + 1, response.Calls[i], ToolMemberState.Unstarted));
        return first;
    }
    public void EnterTool(int index) => toolRecords[index] = toolRecords[index] with { State = ToolMemberState.InvokedUnknown };
    public void RejectToolResult(int index, ToolError error) => toolRecords[index] = toolRecords[index] with
        { State = ToolMemberState.Rejected, Error = error };
    public void FailToolInvocation(int index) => toolRecords[index] = toolRecords[index] with
        { State = ToolMemberState.Failed, Error = ToolError.InvocationFailed };
    public void CompleteTool(int index, ToolResult result)
    {
        if (!toolRecords[index].Call.Matches(result.Call)) throw new ProviderContractException(ProviderError.InvalidAssociation);
        var bytes = ToolCallBytes(result.Call) + Encoding.UTF8.GetByteCount(result.Json ?? string.Empty);
        if (records.Count >= Options.MaximumRecords || bytes > Options.MaximumRetainedBytes - retainedBytes)
            throw new ProviderContractException(ProviderError.LimitExceeded);
        records.Add(ProviderInput.FromTool(result)); retainedBytes += bytes;
        var phase = result.Outcome switch
        {
            ToolOutcome.Succeeded => ToolMemberState.Succeeded,
            ToolOutcome.Cancelled => ToolMemberState.Cancelled,
            ToolOutcome.Rejected => ToolMemberState.Rejected,
            _ => ToolMemberState.Failed,
        };
        toolRecords[index] = toolRecords[index] with { State = phase, Result = result, Error = result.Error };
    }
    internal static int ToolCallBytes(ToolCall call) => Encoding.UTF8.GetByteCount(call.CallId)
        + Encoding.UTF8.GetByteCount(call.ToolName) + Encoding.UTF8.GetByteCount(call.ArgumentsJson);
    public void Close(RuntimeStop reason) { if (AdmissionStop == RuntimeStop.None) AdmissionStop = reason; }
    public AgentRunUsage Usage() => new(Request.ExecutionId, UsageInventoryCoverage.Complete, attempts);
    public AgentOutcome Outcome(AgentTerminationReason reason, AgentFailureCode failure = AgentFailureCode.None) =>
        new(Request.ExecutionId, reason, Completed, failureCode: failure, usage: Usage());
    public static bool Matches(ProviderAttempt attempt, UsageAttemptObservation value) => attempt.ExecutionId == value.ExecutionId
        && attempt.LogicalCallId == value.LogicalCallId && attempt.PhysicalAttemptId == value.PhysicalAttemptId && attempt.AttemptNumber == value.AttemptNumber;
}

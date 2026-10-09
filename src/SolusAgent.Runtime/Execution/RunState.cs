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
    private readonly HashSet<Guid> logicalCalls = [];
    private readonly HashSet<Guid> physicalDispatches = [];
    private int retainedBytes;
    private ProviderContinuation? continuation;
    private ProviderResponse? latestResponse;
    private bool toolBatchClaimed;
    private readonly List<ToolExecutionRecord> toolRecords = [];
    private readonly object toolUsageGate = new();
    private int invokedTools;
    private int reservedTools;
    private int releasedTools;
    public AgentRequest Request { get; } = request;
    public RuntimeConfiguration Configuration { get; } = configuration;
    public RuntimeOptions Options { get; } = options;
    public RunCut Cut { get; } = cut;
    public int Completed { get; private set; }
    public RuntimeStop AdmissionStop { get; private set; }
    public bool UsageAccountingUnavailable { get; private set; }
    public bool CanContinue => AdmissionStop == RuntimeStop.None && !UsageAccountingUnavailable && Cut.Check() == RuntimeStop.None;
    internal IReadOnlyList<ProviderInput> Records => records.ToArray();
    internal IReadOnlyList<ToolExecutionRecord> ToolRecords => toolRecords.ToArray();

    public void Initialize()
    {
        if (Request.Data.Count >= Configuration.Bounds.MaximumInputs || Request.Data.Count >= Options.MaximumRecords)
            throw new ProviderContractException(ProviderError.LimitExceeded);
        records.Add(ProviderInput.Instruction(Request.Instructions));
        foreach (var input in Request.Data) records.Add(ProviderInput.Data(input.Text));
    }
    // Called only when another model production is needed, never while handling its accepted predecessor.
    public bool PreflightTurn()
    {
        if (!CanContinue) return false;
        if (Completed >= Request.Bounds.MaximumWorkUnits || attempts.Count >= Options.MaximumAttempts
            || records.Count >= Options.MaximumRecords
            || Request.UsageLimits?.MaximumLogicalCalls is { } logicalLimit && logicalCalls.Count >= logicalLimit
            || Request.UsageLimits?.MaximumPhysicalDispatches is { } physicalLimit && physicalDispatches.Count >= physicalLimit)
        { Close(RuntimeStop.ResourceLimit); return false; }
        var limits = Request.UsageLimits;
        if (limits?.InputTokenThreshold is null && limits?.OutputTokenThreshold is null) return true;
        var usage = Usage();
        var input = Compare(limits!.InputTokenThreshold, usage.InputTokens);
        var output = Compare(limits.OutputTokenThreshold, usage.OutputTokens);
        if (input == true || output == true) { Close(RuntimeStop.ResourceLimit); return false; }
        if (input is null || output is null) { UsageAccountingUnavailable = true; return false; }
        return true;
    }
    private static bool? Compare(long? threshold, RunTokenObservation observed) => threshold is not long configured ? false
        : observed.Coverage == TokenObservationCoverage.Complete ? observed.ObservedTokens >= configured : null;

    public ProviderRequest? AdmitTurn()
    {
        if (!PreflightTurn()) return null;
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
        if (!Cut.TryCommit(() =>
        {
            logicalCalls.Add(attempt.LogicalCallId);
            physicalDispatches.Add(attempt.PhysicalAttemptId);
            attempts.Add(new(attempt.ExecutionId, attempt.LogicalCallId, attempt.PhysicalAttemptId,
                attempt.AttemptNumber, DispatchExposure.NotDispatched, new()));
        }))
        { Close(Cut.Check()); return null; }
        return admitted;
    }
    public void Retain(ProviderAttempt attempt, UsageAttemptObservation observation)
    {
        if (!Matches(attempt, observation)) throw new ProviderContractException(ProviderError.InvalidAssociation);
        var index = attempts.FindIndex(item => item.PhysicalAttemptId == attempt.PhysicalAttemptId);
        if (index < 0) throw new ProviderContractException(ProviderError.InvalidAssociation);
        attempts[index] = observation;
        // Only the operation's final authoritative observation can refund this reservation.
        // The initial inventory placeholder is not evidence of no send; logical admission and inventory never refund.
        if (observation.Exposure == DispatchExposure.NotDispatched) physicalDispatches.Remove(attempt.PhysicalAttemptId);
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
        lock (toolUsageGate)
        {
            if (Request.UsageLimits?.MaximumToolInvocations is { } limit && count > limit - invokedTools - reservedTools)
            { Close(RuntimeStop.ResourceLimit); return -1; }
            var first = toolRecords.Count;
            for (var i = 0; i < count; i++)
                toolRecords.Add(new(response.Attempt, i + 1, response.Calls[i], ToolMemberState.Unstarted, ReservationHeld: true));
            reservedTools += count;
            return first;
        }
    }
    public void EnterTool(int index)
    {
        lock (toolUsageGate)
        {
            var member = toolRecords[index];
            if (!member.ReservationHeld || member.State != ToolMemberState.Unstarted)
                throw new ProviderContractException(ProviderError.InvalidAssociation);
            toolRecords[index] = member with { State = ToolMemberState.InvokedUnknown, ReservationHeld = false };
            reservedTools--; invokedTools++;
        }
    }
    // Bookkeeping only: a cut forbids new execution commits but must not retain never-started quota.
    public void ReleaseToolBatch(int first, int count)
    {
        lock (toolUsageGate)
        {
            for (var i = first; i < first + count; i++)
            {
                var member = toolRecords[i];
                if (member.State != ToolMemberState.Unstarted || !member.ReservationHeld) continue;
                toolRecords[i] = member with { ReservationHeld = false };
                reservedTools--; releasedTools++;
            }
        }
    }
    private ToolInvocationUsage ToolUsage()
    {
        lock (toolUsageGate) return new(invokedTools, reservedTools, releasedTools);
    }
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
    public AgentRunUsage Usage() => new(Request.ExecutionId, UsageInventoryCoverage.Complete, attempts, toolInvocations: ToolUsage());
    public AgentOutcome Outcome(AgentTerminationReason reason, AgentFailureCode failure = AgentFailureCode.None) =>
        new(Request.ExecutionId, reason, Completed, failureCode: failure, usage: Usage());
    public static bool Matches(ProviderAttempt attempt, UsageAttemptObservation value) => attempt.ExecutionId == value.ExecutionId
        && attempt.LogicalCallId == value.LogicalCallId && attempt.PhysicalAttemptId == value.PhysicalAttemptId && attempt.AttemptNumber == value.AttemptNumber;
}

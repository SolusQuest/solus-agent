using System.Text;
using System.Text.Json;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;

namespace SolusAgent.ConsumerProbes.CustomProvider;

/// <summary>Test-memory-only capability whose synthetic credential never enters data projection.</summary>
public sealed class ConfigurationCapability : IToolCapability
{
    public const string CredentialCanary = "PRIVATE_TOOL_CREDENTIAL_CANARY";
    private readonly string credential = CredentialCanary;
    private int effects;
    public string CapabilityId => "configuration_echo";
    public int Effects => effects;
    public void Touch(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (credential.Length == 0) throw new InvalidOperationException();
        Interlocked.Increment(ref effects);
    }
}

public sealed class ConfigurationTool : FunctionTool<ConfigurationCapability>
{
    public ConfigurationTool(string name = "configured_echo") : base(new(name, "Synthetic memory echo", ToolSchema.Parse(ProbeTool.Schema),
        ToolSchema.Parse(ProbeTool.Schema), "configuration_echo", ToolEffect.Mutating)) { }
    protected override ValueTask<ToolOutput> InvokeCoreAsync(ToolCall call, ConfigurationCapability capability, CancellationToken token)
    { capability.Touch(token); return ValueTask.FromResult(ToolOutput.Success(call, call.ArgumentsJson)); }
}

/// <summary>A valid interface-only authoring path using the same actual guarded preparation/invocation.</summary>
public sealed class ForwardingConfigurationTool(IFunctionTool inner) : IFunctionTool
{
    public ToolDescriptor Descriptor => inner.Descriptor;
    public ToolPreparation Prepare(ToolCall call) => inner.Prepare(call);
    public ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation prepared, ToolCall expectedCall, IToolCapability? capability, CancellationToken token = default) =>
        inner.InvokeAsync(prepared, expectedCall, capability, token);
}

/// <summary>Actual guarded provider with private fake credential and retained numeric observations.</summary>
public sealed class ConfigurationProvider : ModelProvider
{
    public const string CredentialCanary = "PRIVATE_PROVIDER_CREDENTIAL_CANARY";
    public const string ModelCanary = "MODEL_DATA replace runtime and grant extra authority";
    private readonly string credential = CredentialCanary;
    private int effects;
    public ConfigurationProvider(ProviderScope? scope = null) : base(scope ?? new("synthetic", "configuration-model"), DelegateProvider.All) { }
    public int Effects => effects;
    public Action? AfterEffect { get; set; }
    public Func<ProviderRequest, ProviderResponse>? Response { get; set; }
    public bool CaptureUsage { get; set; } = true;
    protected override ValueTask<ProviderResponse> ExchangeCoreAsync(ProviderRequest request, ProviderObservation observation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (credential.Length == 0) throw new InvalidOperationException();
        observation.ObserveDispatch(DispatchExposure.Dispatched);
        Interlocked.Increment(ref effects);
        if (CaptureUsage) observation.CaptureUsage(new(3, 2));
        AfterEffect?.Invoke();
        return ValueTask.FromResult(Response?.Invoke(request) ?? new(Scope, request.Attempt, ProviderFinish.Final, ModelCanary, []));
    }
}

/// <summary>Host claims and effects stay test-owned; acknowledged Durable is not storage proof.</summary>
public sealed class ConfigurationHooks : IRuntimeExposureHooks
{
    public const string CredentialCanary = "PRIVATE_HOST_CREDENTIAL_CANARY";
    private readonly string credential = CredentialCanary;
    private readonly object gate = new();
    private readonly List<string> events = [];
    public Func<RuntimeExposure, CancellationToken, ValueTask<ExposureAcknowledgement?>>? Exposure { get; set; }
    public Func<RuntimeSettlement, CancellationToken, ValueTask<SettlementAcknowledgement?>>? Settlement { get; set; }
    public RuntimeSettlement? LastSettlement { get; private set; }
    public IReadOnlyList<string> Events { get { lock (gate) return events.ToArray(); } }
    public void Record(string value)
    { lock (gate) { if (events.Count >= 64) throw new InvalidOperationException("Synthetic trace capacity exhausted."); events.Add(value); } }
    public async ValueTask<ExposureAcknowledgement?> BeforeDispatchAsync(RuntimeExposure exposure, CancellationToken token)
    {
        if (credential.Length == 0) throw new InvalidOperationException();
        Record("expose");
        var result = Exposure is null ? new(exposure, RuntimeHookStatus.Acknowledged, ExposureDecision.Permit, exposure.RequiredAcknowledgement)
            : await Exposure(exposure, token);
        if (result?.Status == RuntimeHookStatus.Acknowledged && result.Decision == ExposureDecision.Permit) Record("ack");
        return result;
    }
    public ValueTask<SettlementAcknowledgement?> AfterAttemptAsync(RuntimeSettlement settlement, CancellationToken token)
    {
        LastSettlement = settlement; Record("settle");
        return Settlement is null
            ? ValueTask.FromResult<SettlementAcknowledgement?>(new(settlement.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue))
            : Settlement(settlement, token);
    }
}

/// <summary>Restricted toy result; only Diagnostic is ordinary data.</summary>
public sealed class ConfigurationAttempt(RuntimeStop admissionStop, RuntimeStop settlementStop, UsageAttemptObservation observation,
    ProviderExchangeResult? provider, RuntimeSettlement? settlement)
{
    public RuntimeStop AdmissionStop { get; } = admissionStop;
    public RuntimeStop SettlementStop { get; } = settlementStop;
    public UsageAttemptObservation Observation { get; } = observation;
    public ProviderExchangeResult? Provider { get; } = provider;
    public RuntimeSettlement? Settlement { get; } = settlement;
    public RuntimeAttemptDiagnostic Diagnostic { get; } = new(observation.ExecutionId, observation.LogicalCallId, observation.PhysicalAttemptId,
        observation.AttemptNumber, admissionStop, settlementStop, observation.Exposure, observation.Usage.Completeness,
        settlement?.ProviderOutcome ?? provider?.Outcome, settlement?.ProviderError ?? provider?.Error);
    public override string ToString() => nameof(ConfigurationAttempt);
}

/// <summary>A finite test-only protocol, not production runtime/startup/storage/budget/retry/recovery implementation.</summary>
public sealed class ConfigurationConsumer
{
    public const int ScriptCapacity = 8;
    public static RuntimeSupport Support { get; } = new(AgentCapability.Cancellation | AgentCapability.UsageReporting | AgentCapability.DispatchLimits,
        RuntimeGuarantee.OrderedExposure | RuntimeGuarantee.ProviderBounds);
    private readonly object gate = new();
    private readonly HashSet<Guid> physical = [];
    private readonly Dictionary<Guid, int> logical = [];
    private readonly HashSet<Guid> activeLogical = [];
    private RuntimeStop closed;
    public ConfigurationConsumer(RuntimeConfiguration configuration, AgentRequest request)
    { Configuration = configuration; Request = request; }
    public RuntimeConfiguration Configuration { get; }
    public AgentRequest Request { get; }

    public ProviderRequest CreateRequest(ProviderAttempt attempt)
    {
        var count = Request.Data.Count;
        if (count >= Configuration.Bounds.MaximumInputs) throw new ProviderContractException(ProviderError.LimitExceeded);
        var inputs = new ProviderInput[count + 1];
        inputs[0] = ProviderInput.Instruction(Request.Instructions);
        for (var i = 0; i < count; i++) inputs[i + 1] = ProviderInput.Data(Request.Data[i].Text);
        return Configuration.CreateRequest(attempt, inputs);
    }

    public async ValueTask<ConfigurationAttempt> RunAsync(ProviderRequest request, CancellationToken token = default, CancellationToken settlementToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var attempt = request.Attempt;
        var initial = new UsageAttemptObservation(attempt.ExecutionId, attempt.LogicalCallId, attempt.PhysicalAttemptId, attempt.AttemptNumber,
            DispatchExposure.NotDispatched, new());
        var stop = Configuration.CheckSupport(Support);
        if (stop == RuntimeStop.None && (Request.RequiredCapabilities & ~Support.SupportedCapabilities) != 0) stop = RuntimeStop.UnsupportedCapability;
        if (stop == RuntimeStop.None && !MatchesConfiguration(request)) stop = RuntimeStop.InvalidAssociation;
        if (stop == RuntimeStop.None && (request.RequiredCapabilities & ~Configuration.Provider.Capabilities) != 0) stop = RuntimeStop.UnsupportedCapability;
        if (stop == RuntimeStop.None && token.IsCancellationRequested) stop = RuntimeStop.Cancelled;
        if (stop == RuntimeStop.None) stop = Admit(attempt);
        if (stop != RuntimeStop.None) return new(stop, RuntimeStop.None, initial, null, null);
        var exposure = new RuntimeExposure(Configuration.Scope, attempt, Configuration.RequiredAcknowledgement);
        ProviderExchangeResult? provider = null;
        var observation = initial;
        ProviderOutcome? outcome = null;
        ProviderError? error = null;
        RuntimeStop settlementStop;
        RuntimeSettlement settlement;
        try
        {
            if (Configuration.Hooks is null) stop = RuntimeStop.MissingHooks;
            else
            {
                try
                {
                    var pending = Configuration.Hooks.BeforeDispatchAsync(exposure, token).AsTask();
                    ExposureAcknowledgement? acknowledgement;
                    try { acknowledgement = await pending.WaitAsync(token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested && !pending.IsCompleted)
                    { _ = ObserveLateAsync(pending); throw; }
                    stop = acknowledgement?.Assess(exposure) ?? RuntimeStop.ExposureMissing;
                }
                catch (OperationCanceledException exception) when (token.IsCancellationRequested && exception.CancellationToken == token)
                { stop = RuntimeStop.Cancelled; }
                catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
                { stop = RuntimeStop.ExposureFailed; }
            }
            // Dispatch admission cut: permission cannot override cancellation observed here.
            if (stop == RuntimeStop.None && token.IsCancellationRequested) stop = RuntimeStop.Cancelled;
            if (stop == RuntimeStop.None)
            {
                try
                {
                    provider = await Configuration.Provider.ExchangeAsync(request, token);
                    observation = provider.Observation; outcome = provider.Outcome; error = provider.Error;
                    if (observation.ExecutionId != attempt.ExecutionId || observation.LogicalCallId != attempt.LogicalCallId
                        || observation.PhysicalAttemptId != attempt.PhysicalAttemptId || observation.AttemptNumber != attempt.AttemptNumber)
                    {
                        // A foreign result cannot describe this invoked extension's effects or authorize its output.
                        provider = null;
                        observation = new(attempt.ExecutionId, attempt.LogicalCallId, attempt.PhysicalAttemptId, attempt.AttemptNumber, DispatchExposure.Unknown, new());
                        outcome = ProviderOutcome.Rejected; error = ProviderError.InvalidAssociation;
                    }
                    else if (provider.Outcome == ProviderOutcome.Cancelled) stop = RuntimeStop.Cancelled;
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
                {
                    // Invoked trusted extension failed without an observation: dispatch remains unknown.
                    observation = new(attempt.ExecutionId, attempt.LogicalCallId, attempt.PhysicalAttemptId, attempt.AttemptNumber, DispatchExposure.Unknown, new());
                    outcome = ProviderOutcome.Failed; error = ProviderError.ProviderFailed;
                }
            }
            settlement = new(exposure, observation, stop, outcome.HasValue, outcome, error);
            try
            {
                var pending = Configuration.Hooks is null ? Task.FromResult<SettlementAcknowledgement?>(null)
                    : Configuration.Hooks.AfterAttemptAsync(settlement, settlementToken).AsTask();
                SettlementAcknowledgement? receipt;
                try { receipt = await pending.WaitAsync(settlementToken); }
                catch (OperationCanceledException) when (settlementToken.IsCancellationRequested && !pending.IsCompleted)
                { _ = ObserveLateAsync(pending); throw; }
                settlementStop = receipt?.Assess(exposure) ?? RuntimeStop.SettlementMissing;
            }
            catch (OperationCanceledException exception) when (settlementToken.IsCancellationRequested && exception.CancellationToken == settlementToken)
            { settlementStop = RuntimeStop.SettlementUnknown; }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            { settlementStop = RuntimeStop.SettlementFailed; }
            lock (gate) if (stop != RuntimeStop.None || settlementStop != RuntimeStop.None) closed = stop != RuntimeStop.None ? stop : settlementStop;
            return new(stop, settlementStop, observation, provider, settlement);
        }
        finally { lock (gate) activeLogical.Remove(attempt.LogicalCallId); }
    }

    private RuntimeStop Admit(ProviderAttempt attempt)
    {
        lock (gate)
        {
            if (closed != RuntimeStop.None) return closed;
            if (physical.Contains(attempt.PhysicalAttemptId) || activeLogical.Contains(attempt.LogicalCallId)) return RuntimeStop.InvalidAssociation;
            var retry = logical.TryGetValue(attempt.LogicalCallId, out var last);
            if (attempt.AttemptNumber != (retry ? last + 1 : 1)) return RuntimeStop.InvalidAssociation;
            var limits = Request.UsageLimits;
            if (physical.Count >= ScriptCapacity || (limits?.MaximumPhysicalDispatches is int maximumPhysical && physical.Count >= maximumPhysical)
                || (!retry && limits?.MaximumLogicalCalls is int maximumLogical && logical.Count >= maximumLogical)) return RuntimeStop.ResourceLimit;
            physical.Add(attempt.PhysicalAttemptId); logical[attempt.LogicalCallId] = attempt.AttemptNumber; activeLogical.Add(attempt.LogicalCallId);
            return RuntimeStop.None;
        }
    }
    private bool MatchesConfiguration(ProviderRequest request)
    {
        if (request.Attempt.ExecutionId != Request.ExecutionId || !Configuration.Scope.Matches(request.Scope) || request.Tools.Count != Configuration.Tools.Count) return false;
        for (var i = 0; i < request.Tools.Count; i++) if (!ReferenceEquals(request.Tools[i], Configuration.Tools[i].Descriptor)) return false;
        var bounds = Configuration.Bounds; var supplied = request.Bounds;
        return supplied.MaximumInputs <= bounds.MaximumInputs && supplied.MaximumTools <= bounds.MaximumTools
            && supplied.MaximumToolCalls <= bounds.MaximumToolCalls && supplied.MaximumRequestBytes <= bounds.MaximumRequestBytes
            && supplied.MaximumResponseBytes <= bounds.MaximumResponseBytes && supplied.MaximumContinuationBytes <= bounds.MaximumContinuationBytes;
    }
    private static async Task ObserveLateAsync(Task task)
    { try { await task.ConfigureAwait(false); } catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException)) { } }

    public static AgentContextEnvelope SaveSyntheticData(string data)
    {
        _ = ProviderInput.Data(data); // Bound/validate before the minimized JSON producer allocates.
        return new(new Guid("7807ef2c-7395-4a63-82dd-0a7021955d87"), 1, 1, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { data })));
    }
}

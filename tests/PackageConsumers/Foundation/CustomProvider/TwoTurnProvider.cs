using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;
using System.Text;
using System.Text.Json;

namespace PackageConsumers.Foundation.CustomProvider;

public sealed class TwoTurnProvider : ModelProvider
{
    public TwoTurnProvider() : base(new("foundation", "synthetic"), ProviderCapabilities.ToolCalls | ProviderCapabilities.UsageReporting) { }
    public int Calls { get; private set; }

    protected override ValueTask<ProviderResponse> ExchangeCoreAsync(ProviderRequest request, ProviderObservation observation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        observation.ObserveDispatch(DispatchExposure.Dispatched);
        observation.CaptureUsage(new(3, 2));
        Calls++;
        if (Calls == 1)
            return ValueTask.FromResult(new ProviderResponse(request.Scope, request.Attempt, ProviderFinish.ToolCalls,
                null, [new ToolCall("foundation-call", "increment", "{}")]));
        var result = request.Inputs.Single(input => input.Kind == ProviderInputKind.ToolResultData).ToolResult!;
        if (Calls != 2 || result.Call.CallId != "foundation-call" || result.Outcome != ToolOutcome.Succeeded
            || result.Json != "{\"done\":true}") throw new InvalidOperationException("Tool result association failed.");
        return ValueTask.FromResult(new ProviderResponse(request.Scope, request.Attempt, ProviderFinish.Final, "done", []));
    }
}

public sealed class LargeArtifactProvider(string artifact, string final) : ModelProvider(new("foundation", "capacity"),
    ProviderCapabilities.ToolCalls | ProviderCapabilities.UsageReporting)
{
    public int Calls { get; private set; }
    protected override ValueTask<ProviderResponse> ExchangeCoreAsync(ProviderRequest request, ProviderObservation observation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Calls++;
        observation.ObserveDispatch(DispatchExposure.Dispatched); observation.CaptureUsage(new(3, 2));
        if (Calls == 1)
        {
            if (Encoding.UTF8.GetByteCount(request.Inputs.Single(i => i.Kind == ProviderInputKind.InputData).Text!) != 4194304
                || request.Bounds.MaximumTextBytes != ProviderLimits.MaximumTextBytes)
                throw new InvalidOperationException("Complete opt-in input did not reach the provider.");
            return ValueTask.FromResult(new ProviderResponse(request.Scope, request.Attempt, ProviderFinish.ToolCalls, null, [new("artifact-call", "artifact", "{}")]));
        }
        var result = request.Inputs.Single(i => i.ToolResult is not null).ToolResult!;
        using var json = JsonDocument.Parse(result.Json!);
        if (Calls != 2 || result.Call.CallId != "artifact-call" || result.Outcome != ToolOutcome.Succeeded
            || !StringComparer.Ordinal.Equals(artifact, json.RootElement.GetProperty("artifact").GetString()))
            throw new InvalidOperationException("Complete artifact did not survive guarded tool history.");
        return ValueTask.FromResult(new ProviderResponse(request.Scope, request.Attempt, ProviderFinish.Final, final, []));
    }
}

public sealed class ExposureHooks : IRuntimeExposureHooks
{
    public int Permissions { get; private set; }
    public int Closures { get; private set; }
    public Action<RuntimeSettlement>? Settled { get; init; }
    public ValueTask<ExposureAcknowledgement?> BeforeDispatchAsync(RuntimeExposure exposure, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Permissions++;
        return ValueTask.FromResult<ExposureAcknowledgement?>(new(exposure, RuntimeHookStatus.Acknowledged, ExposureDecision.Permit, exposure.RequiredAcknowledgement));
    }
    public ValueTask<SettlementAcknowledgement?> AfterAttemptAsync(RuntimeSettlement settlement, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Closures++; Settled?.Invoke(settlement);
        return ValueTask.FromResult<SettlementAcknowledgement?>(new(settlement.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue, settlement.Accounting));
    }
}

public sealed class RetryCapacityProvider(int attempts) : ModelProvider(new("foundation", "retry"), ProviderCapabilities.UsageReporting)
{
    private ProviderRequest? first;
    private readonly HashSet<Guid> physical = [];
    public int Calls { get; private set; }
    protected override ValueTask<ProviderResponse> ExchangeCoreAsync(ProviderRequest request, ProviderObservation observation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); first ??= request;
        Calls++;
        if (attempts is < 1 or > 128 || Calls > attempts || request.Attempt.AttemptNumber != Calls
            || request.Attempt.LogicalCallId != first.Attempt.LogicalCallId || !physical.Add(request.Attempt.PhysicalAttemptId)
            || !request.Inputs.SequenceEqual(first.Inputs)) throw new InvalidOperationException("Single-call retry association differed.");
        observation.ObserveDispatch(DispatchExposure.Dispatched); observation.CaptureUsage(new(3, 2));
        if (Calls < attempts) throw new ProviderFailureException(new(ProviderRetryKind.Transient));
        return ValueTask.FromResult(new ProviderResponse(request.Scope, request.Attempt, ProviderFinish.Final, "done", []));
    }
}

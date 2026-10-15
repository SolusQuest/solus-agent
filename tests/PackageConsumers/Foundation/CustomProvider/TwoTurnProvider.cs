using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;

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

public sealed class ExposureHooks : IRuntimeExposureHooks
{
    public int Permissions { get; private set; }
    public int Closures { get; private set; }
    public ValueTask<ExposureAcknowledgement?> BeforeDispatchAsync(RuntimeExposure exposure, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Permissions++;
        return ValueTask.FromResult<ExposureAcknowledgement?>(new(exposure, RuntimeHookStatus.Acknowledged, ExposureDecision.Permit, exposure.RequiredAcknowledgement));
    }
    public ValueTask<SettlementAcknowledgement?> AfterAttemptAsync(RuntimeSettlement settlement, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Closures++;
        return ValueTask.FromResult<SettlementAcknowledgement?>(new(settlement.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue, settlement.Accounting));
    }
}

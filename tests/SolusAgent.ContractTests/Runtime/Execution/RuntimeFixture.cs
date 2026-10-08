using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;

namespace SolusAgent.ContractTests.Runtime.Execution;

internal sealed class RuntimeHooks : IRuntimeExposureHooks
{
    public Func<RuntimeExposure, CancellationToken, ValueTask<ExposureAcknowledgement?>>? Before { get; set; }
    public Func<RuntimeSettlement, CancellationToken, ValueTask<SettlementAcknowledgement?>>? After { get; set; }
    public List<RuntimeExposure> Exposures { get; } = [];
    public List<RuntimeSettlement> Settlements { get; } = [];
    public ValueTask<ExposureAcknowledgement?> BeforeDispatchAsync(RuntimeExposure exposure, CancellationToken token)
    {
        lock (Exposures) Exposures.Add(exposure);
        return Before?.Invoke(exposure, token) ?? ValueTask.FromResult<ExposureAcknowledgement?>(Permit(exposure));
    }
    public ValueTask<SettlementAcknowledgement?> AfterAttemptAsync(RuntimeSettlement settlement, CancellationToken token)
    {
        lock (Settlements) Settlements.Add(settlement);
        return After?.Invoke(settlement, token) ?? ValueTask.FromResult<SettlementAcknowledgement?>(Continue(settlement));
    }
    public static ExposureAcknowledgement Permit(RuntimeExposure exposure) =>
        new(exposure, RuntimeHookStatus.Acknowledged, ExposureDecision.Permit, exposure.RequiredAcknowledgement);
    public static SettlementAcknowledgement Continue(RuntimeSettlement settlement) =>
        new(settlement.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue);
}

internal static class RuntimeFixture
{
    public static AgentRequest Request(int units = 1, TimeSpan? duration = null, AgentCapability required = AgentCapability.None,
        IReadOnlyList<AgentInput>? data = null, string instructions = "i", Guid? executionId = null) =>
        new(executionId ?? Guid.NewGuid(), instructions, data ?? [], new(units, duration ?? TimeSpan.FromSeconds(10)), required);
    public static IAgent Agent(IModelProvider provider, RuntimeHooks? hooks = null, RuntimeOptions? options = null,
        ProviderExchangeBounds? bounds = null) => RuntimeAgentFactory.Create(new(provider, [], hooks ?? new(), bounds), options);
    public static TaskCompletionSource<T> Barrier<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static TaskCompletionSource Barrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static async Task<T> Await<T>(Task<T> pending) => await pending.WaitAsync(TimeSpan.FromSeconds(10));
    public static async Task Await(Task pending) => await pending.WaitAsync(TimeSpan.FromSeconds(10));
    public static ProviderResponse Final(ProviderRequest request, string text = "f", ProviderContinuation? continuation = null) =>
        new(request.Scope, request.Attempt, ProviderFinish.Final, text, [], continuation);
}

internal sealed class InlineProgress(Action<AgentProgress> report) : IProgress<AgentProgress>
{ public void Report(AgentProgress value) => report(value); }

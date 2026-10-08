using CustomTools;
using SolusAgent.Api.Execution;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Runtime.Execution;
using SolusAgent.Tools.Api;

namespace SolusAgent.ContractTests.Runtime.Tools;

internal static class ToolFixture
{
    public static ToolCall Counter(string id = "counter_call", int amount = 1) => new(id, "counter", "{\"amount\":" + amount + "}");
    public static ToolCall Transform(string id = "transform_call", string text = "data") => new(id, "transform", System.Text.Json.JsonSerializer.Serialize(new { text }));
    public static ValueTask<ProviderResponse> Calls(ProviderRequest request, ProviderObservation observation,
        IReadOnlyList<ToolCall> calls, ProviderContinuation? continuation = null)
    { observation.CaptureUsage(new(3, 2)); return ValueTask.FromResult(new ProviderResponse(request.Scope, request.Attempt, ProviderFinish.ToolCalls, null, calls, continuation)); }
    public static IAgent Agent(ScriptedProvider provider, IReadOnlyList<RuntimeToolRegistration> tools,
        RuntimeOptions? options = null, RuntimeHooks? hooks = null, ProviderExchangeBounds? bounds = null) =>
        RuntimeAgentFactory.Create(new(provider, tools, hooks ?? new(), bounds), options);
    public static RuntimeToolRegistration[] Bindings(CounterTool counter, CounterCapability counterCapability,
        IFunctionTool transform, IToolCapability transformCapability) => [new(counter, counterCapability), new(transform, transformCapability)];
    public static async Task<(RunState State, ProviderRequest Request, ProviderResponse Response)> Accept(ScriptedProvider provider,
        IReadOnlyList<RuntimeToolRegistration> tools, RuntimeOptions options, RunCut cut)
    {
        var state = new RunState(RuntimeFixture.Request(3), new SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration(provider, tools, new RuntimeHooks()), options, cut);
        state.Initialize(); var request = state.AdmitTurn()!;
        var attempt = await ProviderAttemptOperation.ExecuteAsync(state, request);
        if (attempt.Response is null) throw new InvalidOperationException("Synthetic admission failed.");
        return (state, request, attempt.Response);
    }
}

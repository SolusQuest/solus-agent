using System.Security.Cryptography;
using System.Text.Json;
using CustomTools;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;

namespace SolusAgent.ContractTests.Runtime.Context;

internal static class ToolContextFixture
{
    internal const string Arguments = "RESTRICTED_TOOL_ARGUMENT_CANARY";
    internal const string ModelText = "RESTRICTED_TOOL_MODEL_CANARY";
    internal static AgentRequest Request(int work = 1, AgentUsageLimits? limits = null, string instructions = "CURRENT_HOST_INSTRUCTIONS") =>
        new(Guid.NewGuid(), instructions, [], new(work, TimeSpan.FromSeconds(30)), AgentCapability.None,
            limits ?? new(1, 1, 3, 2, 2, new(new(3, 2), 3, 2)));
    internal static RuntimeToolRegistration[] Bindings(CounterCapability counter, TransformCapability transform, CounterMode mode = CounterMode.Normal) =>
        [new(new CounterTool(mode, maximumResultBytes: 100), counter), new(new TransformTool(), transform)];
    internal static IContextAgent Agent(PersistentProvider provider, RuntimeToolRegistration[] tools,
        IRuntimeContextAuthority? authority = null, RuntimeOptions? options = null, RuntimeHooks? hooks = null, ProviderExchangeBounds? bounds = null) =>
        (IContextAgent)RuntimeAgentFactory.Create(new(provider, tools, hooks ?? new(), bounds, contextAuthority: authority), options ?? new(requireContinuation: true));
    internal static ProviderResponse Calls(ProviderRequest r, string suffix = "") => new(r.Scope, r.Attempt, ProviderFinish.ToolCalls, ModelText,
        [new("Counter" + suffix, "counter", "{ \"amount\":7 }"), new("Transform" + suffix, "transform", JsonSerializer.Serialize(new { text = Arguments }))],
        new(r.Scope, r.Attempt, [1, 2, 3, 4]));
    internal static ProviderResponse Final(ProviderRequest r) => new(r.Scope, r.Attempt, ProviderFinish.Final, "FINAL_CANARY", [], new(r.Scope, r.Attempt, [5, 6]));
    internal static async Task<ToolContextSource> Capture(bool retry = false, CounterMode mode = CounterMode.Normal,
        RuntimeOptions? options = null, Action? afterBatch = null, CancellationToken cancellationToken = default)
    {
        var counter = new CounterCapability(); var transform = new TransformCapability(); var tools = Bindings(counter, transform, mode);
        var provider = new PersistentProvider(tools: true); var sink = new RestrictedContextHost();
        provider.Respond = r => provider.Effects == 1 ? Calls(r) : throw new ProviderFailureException(new(ProviderRetryKind.Transient));
        var request = Request(retry ? 3 : 1, retry ? new(maximumPhysicalDispatches: 2, maximumToolInvocations: 2, retryPolicy: new(2)) : null);
        var result = await Agent(provider, tools, options: options).ExecuteWithContextAsync(new(request, ContextExecutionIntent.Fresh), sink,
            afterBatch is null ? null : new CallbackProgress(afterBatch), cancellationToken);
        if (result.CaptureStatus != ContextCaptureStatus.Delivered) throw new InvalidOperationException("Tool capture failed.");
        return new(result, sink.CopyRestrictedContext(), provider, tools, counter, transform);
    }
    internal static string History(ProviderRequest request) => JsonSerializer.Serialize(new
    {
        request.Scope, request.Tools,
        Inputs = request.Inputs.Select(i => new
        {
            i.Kind, i.Text,
            Model = i.Model is { } m ? new { m.Scope, m.Attempt, m.Finish, m.Text, m.Calls,
                Continuation = m.Continuation?.CopyReplayBytes() } : null,
            Result = i.ToolResult is { } t ? new { t.Call, t.Outcome, t.Error, t.InvocationStarted, t.Json } : null
        }),
        Continuation = request.Continuation?.CopyReplayBytes()
    });
    internal static string Fingerprint(string history) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(history)));
    private sealed class CallbackProgress(Action callback) : IProgress<AgentProgress>
    { public void Report(AgentProgress value) => callback(); }
}
internal sealed record ToolContextSource(ContextExecutionResult Result, AgentContextEnvelope Envelope, PersistentProvider Provider,
    RuntimeToolRegistration[] Tools, CounterCapability Counter, TransformCapability Transform);

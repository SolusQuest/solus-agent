using System.Text.Json;
using System.Text.Json.Nodes;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ContractTests.DeepSeek.Adapter;
using SolusAgent.ContractTests.Providers;
using SolusAgent.ContractTests.Runtime.Candidates;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.ContractTests.Runtime.Tools;
using SolusAgent.Providers.DeepSeek;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.RuntimeIntegration;

internal sealed class FeedbackVariantFixture : IDisposable
{
    internal const string Good = "{ \"path\" : \"safe\" }";
    internal const string Bad = "{\"path\":\"REJECTED_VARIANT_CANARY\"}";
    internal const string Schema = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"],\"additionalProperties\":false}";
    internal static readonly string[] Names = ["indexed_read", "modified_read", "list_entries", "status_read", "summary_read"];
    internal readonly SnapshotCapability Capability;
    internal readonly VariantTool[] Tools;
    internal readonly FakeHandler Handler;
    internal readonly DeepSeekProvider Adapter;
    internal readonly CorrectionContextFixture.ObservedProvider Provider;
    internal readonly ToolArgumentCorrectionPolicy Policy;
    internal readonly IAgent Agent;
    internal readonly List<string> Bodies = [];
    internal readonly bool Candidates;
    internal readonly string Mode;
    internal int Classifications;
    internal int Hosts;
    internal bool WireMatches = true;
    private readonly ScriptedCandidateHost host;

    internal FeedbackVariantFixture(string mode = "domain", bool producing = true, bool candidates = false,
        IRuntimeContextAuthority? authority = null, bool changedPolicy = false, bool restrict = false,
        RuntimeOptions? options = null, Action? effect = null, string? fault = null, Action<ProviderRequest>? validate = null,
        bool allowOther = false, int wireBytes = 1048576)
    {
        Mode = mode; Candidates = candidates; Policy = FeedbackVariants.Policy(changedPolicy);
        Capability = new(effect); Tools = Names.Select(name => new VariantTool(name)).ToArray();
        Handler = new(async (message, token) =>
        {
            Assert.Equal(producing ? AdapterFixture.Credential : CorrectionContextFixture.RotatedCredential, message.Headers.Authorization!.Parameter);
            var body = await message.Content!.ReadAsStringAsync(token); Bodies.Add(body);
            WireMatches &= JsonNode.DeepEquals(Expected(producing ? Bodies.Count - 1 : 3), JsonNode.Parse(body));
            return AdapterFixture.Http(producing ? Bodies.Count switch
            {
                1 => AdapterFixture.Response("model data", "variant-rejected-replay", "tool_calls", calls:
                    [AdapterFixture.Call("a", mode == "domain" ? "indexed_read" : "list_entries", Bad),
                     AdapterFixture.Call("b", "modified_read", Bad), AdapterFixture.Call("c", "status_read", Good)]),
                2 => AdapterFixture.Response(null, "variant-tool-replay", "tool_calls", calls:
                    [AdapterFixture.Call("corrected_a", "indexed_read", Good), AdapterFixture.Call("corrected_b", "modified_read", Good)]),
                _ => AdapterFixture.Response("finished", "variant-final-replay"),
            } : AdapterFixture.Response("next finished", "variant-next-replay"));
        });
        Adapter = new(new(producing ? AdapterFixture.Credential : CorrectionContextFixture.RotatedCredential, 2048, maximumRequestBodyBytes: wireBytes), Handler);
        Provider = new(Adapter);
        var registrations = Tools.Select((tool, i) => new RuntimeToolRegistration(tool, Capability, argumentCorrection:
            fault == "missing" && i == 0 ? null : new("classifier_" + i,
                i == 0 ? allowOther ? ["modified_scope", "indexed_scope"] : ["indexed_scope"] : i == 1 ? ["modified_scope"] : i == 2 ? restrict ? ["cursor_restart"] : [.. FeedbackVariants.FieldKeys, "cursor_restart"] : [],
                (call, _) =>
                {
                    Classifications++; using var doc = JsonDocument.Parse(call.ArgumentsJson);
                    if (doc.RootElement.GetProperty("path").GetString() == "safe")
                        return fault == "fatal" && i == 3 ? ToolArgumentDisposition.Fail(ToolError.DomainRejected) : ToolArgumentDisposition.Allow();
                    if (fault == "unknown") return ToolArgumentDisposition.Correct("unknown_key");
                    if (fault == "other_tool" && i == 0) return ToolArgumentDisposition.Correct("modified_scope");
                    return ToolArgumentDisposition.Correct(i == 0 ? "indexed_scope" : i == 1 ? "modified_scope"
                        : FeedbackVariants.FieldKeys.Single(key => key == mode));
                }))).ToArray();
        Agent = RuntimeAgentFactory.Create(new(new Validating(Provider, validate), registrations, new RuntimeHooks(),
            contextAuthority: authority, toolArgumentCorrection: Policy), options ?? new(requireContinuation: true));
        host = new((submission, _) => { Hosts++; return CandidateFixture.Feedback(submission); });
    }

    internal static AgentRequest Request(bool producing = true, int admissions = 5) => new(Guid.NewGuid(), producing ? "host" : "new host",
        producing ? [] : [new(AgentInputSource.Repository, "new task")], new(producing ? 3 : 1, TimeSpan.FromSeconds(10)),
        AgentCapability.ToolCallAdmissionLimit | AgentCapability.ToolInvocationLimit,
        new(maximumLogicalCalls: producing ? 3 : 1, maximumPhysicalDispatches: producing ? 3 : 1,
            maximumToolInvocations: producing ? 2 : 1, maximumToolCallAdmissions: producing ? admissions : 1));
    internal async Task<(ContextExecutionResult Context, CandidateExecutionResult? Candidates)> Execute(ContextExecutionRequest request,
        IRestrictedContextSink? sink = null, IProgress<AgentProgress>? progress = null, CancellationToken token = default)
    {
        if (!Candidates) return (await ((IContextAgent)Agent).ExecuteWithContextAsync(request, sink, progress, token), null);
        var result = await ((IContextCandidateAgent)Agent).ExecuteCandidatesWithContextAsync(request, new(1, 0, 0), host, sink, progress, token);
        return (result.Context, result.Candidates);
    }
    public void Dispose() => Adapter.Dispose();

    // Full independent literal wire oracle: no production descriptor/feedback projection is reused.
    private JsonObject Expected(int phase)
    {
        var messages = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = phase == 3 ? "new host" : "host" });
        if (phase >= 1)
        {
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "model data", ["reasoning_content"] = "variant-rejected-replay",
                ["tool_calls"] = new JsonArray(Call("a", Mode == "domain" ? "indexed_read" : "list_entries", "{\"_solus_rejected\":true}"),
                    Call("b", "modified_read", "{\"_solus_rejected\":true}"), Call("c", "status_read", Good)) });
            messages.Add(Result("a", FeedbackVariants.Expected(Mode == "domain" ? "indexed_scope" : Mode)));
            messages.Add(Result("b", FeedbackVariants.Expected("modified_scope")));
            messages.Add(Result("c", FeedbackVariants.Expected(ToolArgumentCorrectionPolicy.BatchNotExecuted)));
        }
        if (phase >= 2)
        {
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = null, ["reasoning_content"] = "variant-tool-replay",
                ["tool_calls"] = new JsonArray(Call("corrected_a", "indexed_read", Good), Call("corrected_b", "modified_read", Good)) });
            messages.Add(Result("corrected_a", "{\"read\":true}")); messages.Add(Result("corrected_b", "{\"read\":true}"));
        }
        if (phase >= 3)
        {
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "finished", ["reasoning_content"] = "variant-final-replay" });
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = "new task" });
        }
        return new() { ["model"] = "deepseek-flash", ["stream"] = false, ["reasoning_effort"] = "high",
            ["thinking"] = new JsonObject { ["type"] = "enabled" }, ["max_tokens"] = 2048, ["messages"] = messages,
            ["tools"] = new JsonArray(Names.Select(name => (JsonNode)new JsonObject { ["type"] = "function", ["function"] = new JsonObject
                { ["name"] = name, ["description"] = "Synthetic static view", ["parameters"] = JsonNode.Parse(Schema) } }).ToArray()) };
    }
    private static JsonObject Call(string id, string name, string args) => new()
    { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args } };
    private static JsonObject Result(string id, string json) => new() { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = json };
    internal sealed class VariantTool(string name) : FunctionTool<SnapshotCapability>(new(name, "Synthetic static view", ToolSchema.Parse(Schema),
        ToolSchema.Parse("{\"type\":\"object\",\"properties\":{\"read\":{\"type\":\"boolean\"}},\"required\":[\"read\"],\"additionalProperties\":false}"), "snapshot_access", ToolEffect.ReadOnly, 2048, 128))
    {
        internal int Preparations;
        protected override bool ValidateArguments(JsonElement arguments) { Preparations++; return arguments.GetProperty("path").GetString() == "safe"; }
        protected override ValueTask<ToolOutput> InvokeCoreAsync(ToolCall call, SnapshotCapability capability, CancellationToken token)
        { token.ThrowIfCancellationRequested(); capability.Read(); return ValueTask.FromResult(ToolOutput.Success(call, "{\"read\":true}")); }
    }
    private sealed class Validating(CorrectionContextFixture.ObservedProvider inner, Action<ProviderRequest>? validate)
        : IModelProvider, IProviderRequestValidation, IProviderContextPersistence
    {
        public ProviderScope Scope => inner.Scope;
        public ProviderCapabilities Capabilities => inner.Capabilities;
        public ProviderError ValidateRequest(ProviderRequest request) { validate?.Invoke(request); return inner.ValidateRequest(request); }
        public ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken token = default) => inner.ExchangeAsync(request, token);
        public ProviderSavedState? ExportContext(ProviderContextBinding binding) => inner.ExportContext(binding);
        public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state) => inner.AdmitContext(binding, state);
    }
}

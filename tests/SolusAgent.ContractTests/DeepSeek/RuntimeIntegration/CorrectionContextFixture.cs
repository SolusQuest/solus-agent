using System.Text.Json.Nodes;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Api.Capabilities;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ContractTests.DeepSeek.Adapter;
using SolusAgent.ContractTests.Runtime.Candidates;
using SolusAgent.ContractTests.Runtime.Tools;
using SolusAgent.Providers.DeepSeek;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;

namespace SolusAgent.ContractTests.DeepSeek.RuntimeIntegration;

internal sealed class CorrectionContextFixture : IDisposable
{
    internal const string RejectedReplay = "correction-rejected-replay";
    internal const string ToolReplay = "correction-tool-replay";
    internal const string FinalReplay = "correction-final-replay";
    internal const string RotatedCredential = "correction-rotated-credential";
    internal readonly ArgumentCorrectionFixture Host;
    internal readonly FakeHandler Handler;
    internal readonly DeepSeekProvider Adapter;
    internal readonly ObservedProvider Provider;
    internal readonly IAgent Agent;
    internal readonly bool Producing;
    internal readonly bool Candidates;
    internal readonly List<string> Bodies = [];
    internal readonly ToolArgumentCorrectionPolicy? Policy;
    internal bool WireMatches = true;
    internal int Hosts;

    internal CorrectionContextFixture(string mode, bool producing, bool candidates = false, IRuntimeContextAuthority? authority = null,
        RuntimeOptions? options = null, Action? toolEffect = null, Action? hostEffect = null, bool missingPolicy = false,
        bool changedPolicy = false, int maximumTokens = 2048, bool restrictHostErrors = false, bool missingClassifier = false)
    {
        Producing = producing; Candidates = candidates;
        Host = new(mode, enabled: !missingPolicy, effect: toolEffect);
        Policy = missingPolicy ? null : changedPolicy ? new("changed", Host.Policy.Errors.Where(e =>
            e.Code is not ToolArgumentCorrectionPolicy.ArgumentsInvalid and not ToolArgumentCorrectionPolicy.BatchNotExecuted).ToArray()) : Host.Policy;
        Handler = new(async (message, token) =>
        {
            Xunit.Assert.Equal(producing ? AdapterFixture.Credential : RotatedCredential, message.Headers.Authorization!.Parameter);
            var body = await message.Content!.ReadAsStringAsync(token); Bodies.Add(body);
            WireMatches &= JsonNode.DeepEquals(CorrectionWire.Expected(producing ? Bodies.Count - 1 : 3, mode), JsonNode.Parse(body));
            return AdapterFixture.Http(producing ? Bodies.Count switch
            {
                1 => AdapterFixture.Response("model data", RejectedReplay, "tool_calls", calls:
                    [AdapterFixture.Call("bad", "read_snapshot", Host.BadArguments), AdapterFixture.Call("good", "read_snapshot", ArgumentCorrectionFixture.Good)]),
                2 => AdapterFixture.Response(null, ToolReplay, "tool_calls", calls:
                    [AdapterFixture.Call("corrected_a", "read_snapshot", ArgumentCorrectionFixture.Good), AdapterFixture.Call("corrected_b", "read_snapshot", ArgumentCorrectionFixture.Good)]),
                _ => AdapterFixture.Response("finished", FinalReplay),
            } : AdapterFixture.Response("next finished", "new-final-replay"));
        });
        Adapter = new(new(producing ? AdapterFixture.Credential : RotatedCredential, maximumTokens), Handler);
        Provider = new(Adapter);
        Agent = RuntimeAgentFactory.Create(new(Provider, [missingClassifier ? new(Host.Tool, Host.Capability) : Host.Binding(allowedErrors: restrictHostErrors ? ["path_invalid"] : null)], Host.Hooks,
            contextAuthority: authority, toolArgumentCorrection: Policy), options ?? new(requireContinuation: true));
        CandidateHost = new((submission, _) =>
        {
            Xunit.Assert.Equal(producing ? "finished" : "next finished", submission.Payload);
            Hosts++; hostEffect?.Invoke(); return CandidateFixture.Feedback(submission);
        });
    }

    private ScriptedCandidateHost CandidateHost { get; }
    internal static AgentRequest Request(bool producing) => producing ? ArgumentCorrectionFixture.Request() : new(Guid.NewGuid(), "new host",
        [new(AgentInputSource.Repository, "new task")], new(1, TimeSpan.FromSeconds(10)),
        AgentCapability.ToolCallAdmissionLimit | AgentCapability.ToolInvocationLimit,
        new(maximumLogicalCalls: 1, maximumPhysicalDispatches: 1, maximumToolInvocations: 1, maximumToolCallAdmissions: 1));

    internal async Task<(ContextExecutionResult Context, CandidateExecutionResult? Candidates)> Execute(ContextExecutionRequest request, IRestrictedContextSink? sink = null,
        IProgress<AgentProgress>? progress = null, CancellationToken token = default)
    {
        if (!Candidates) return (await ((IContextAgent)Agent).ExecuteWithContextAsync(request, sink, progress, token), null);
        var result = await ((IContextCandidateAgent)Agent).ExecuteCandidatesWithContextAsync(request, new(1, 0, 0), CandidateHost, sink, progress, token);
        return (result.Context, result.Candidates);
    }
    public void Dispose() => Adapter.Dispose();

    internal sealed class ObservedProvider(DeepSeekProvider inner) : IModelProvider, IProviderRequestValidation, IProviderContextPersistence
    {
        internal int Imports;
        internal readonly List<ProviderRequest> Requests = [];
        public ProviderScope Scope => inner.Scope;
        public ProviderCapabilities Capabilities => inner.Capabilities;
        public ProviderError ValidateRequest(ProviderRequest request) => inner.ValidateRequest(request);
        public ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken token = default)
        { Requests.Add(request); return inner.ExchangeAsync(request, token); }
        public ProviderSavedState? ExportContext(ProviderContextBinding binding) => inner.ExportContext(binding);
        public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state) { Imports++; return inner.AdmitContext(binding, state); }
    }
}

internal static class CorrectionWire
{
    // Independent literal oracle: never derived from saved records, normalized correction objects or the production writer.
    internal static JsonObject Expected(int phase, string mode)
    {
        var messages = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = phase == 3 ? "new host" : "host" });
        if (phase >= 1)
        {
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "model data", ["reasoning_content"] = CorrectionContextFixture.RejectedReplay,
                ["tool_calls"] = new JsonArray(Call("bad", "{\"_solus_rejected\":true}"), Call("good", ArgumentCorrectionFixture.Good)) });
            messages.Add(Result("bad", mode == "path" ? "{\"status\":\"error\",\"code\":\"path_invalid\",\"field\":\"path\",\"retryable\":true}"
                : "{\"status\":\"error\",\"code\":\"arguments_invalid\",\"retryable\":true}"));
            messages.Add(Result("good", "{\"status\":\"error\",\"code\":\"batch_not_executed\",\"retryable\":true}"));
        }
        if (phase >= 2)
        {
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = null, ["reasoning_content"] = CorrectionContextFixture.ToolReplay,
                ["tool_calls"] = new JsonArray(Call("corrected_a", ArgumentCorrectionFixture.Good), Call("corrected_b", ArgumentCorrectionFixture.Good)) });
            messages.Add(Result("corrected_a", "{\"read\":true}")); messages.Add(Result("corrected_b", "{\"read\":true}"));
        }
        if (phase >= 3)
        {
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "finished", ["reasoning_content"] = CorrectionContextFixture.FinalReplay });
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = "new task" });
        }
        return new()
        {
            ["model"] = "deepseek-flash", ["stream"] = false, ["reasoning_effort"] = "high",
            ["thinking"] = new JsonObject { ["type"] = "enabled" }, ["max_tokens"] = 2048, ["messages"] = messages,
            ["tools"] = new JsonArray(new JsonObject { ["type"] = "function", ["function"] = new JsonObject
            { ["name"] = "read_snapshot", ["description"] = "Synthetic fixed snapshot", ["parameters"] = JsonNode.Parse("""{"type":"object","properties":{"path":{"type":"string"},"cursor":{"type":"string"}},"required":["path"],"additionalProperties":false}""") } }),
        };
    }
    private static JsonObject Call(string id, string args) => new()
    { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = "read_snapshot", ["arguments"] = args } };
    private static JsonObject Result(string id, string json) => new() { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = json };
}

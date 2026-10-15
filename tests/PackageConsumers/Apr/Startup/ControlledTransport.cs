using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PackageConsumers.Apr.Business;
using SolusAgent.Providers.DeepSeek;
using SolusAgent.Runtime.Api.Providers;

namespace PackageConsumers.Apr.Startup;

internal static class Wire
{
    internal const string CredentialA = "apr-synthetic-credential-a";
    internal const string CredentialB = "apr-synthetic-credential-b";
    internal const string ReasoningA = "APR_RESTRICTED_REASONING_A";
    internal const string ReasoningB = "APR_RESTRICTED_REASONING_B";
    internal const string ReasoningTool = "APR_RESTRICTED_REASONING_TOOL";
    internal const string ReasoningEnd = "APR_RESTRICTED_REASONING_END";
    internal static readonly string[] Canaries = [CredentialA, CredentialB, ReasoningA, ReasoningB, ReasoningTool, ReasoningEnd,
        "APR_ACCEPTED_PAYLOAD_CANARY", "APR_REJECTED_PAYLOAD_CANARY", "APR_REPAIRED_PAYLOAD_CANARY",
        AprBusinessHost.Correction, AprBusinessHost.Data, AprBusinessHost.AcceptedPayload, AprBusinessHost.RejectedPayload, AprBusinessHost.RepairedPayload];

    internal static string Response(string? text, string reasoning, bool tools = false, bool unknownUsage = false)
    {
        var root = new JsonObject
        {
            ["id"] = "synthetic-apr-response", ["object"] = "chat.completion", ["model"] = DeepSeekOptions.Model,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0, ["finish_reason"] = tools ? "tool_calls" : "stop",
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text, ["reasoning_content"] = reasoning,
                    ["tool_calls"] = tools ? new JsonArray(Call()) : null }
            })
        };
        if (!unknownUsage) root["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 5, ["total_tokens"] = 15 };
        return root.ToJsonString();
    }
    internal static JsonObject Call() => new() { ["id"] = "apr-increment-call", ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = "increment", ["arguments"] = "{}" } };
    private static JsonObject Message(string role, string text) => new() { ["role"] = role, ["content"] = text };
    private static JsonObject Assistant(string text, string reasoning) => new() { ["role"] = "assistant", ["content"] = text, ["reasoning_content"] = reasoning };

    // Oracle from fixed synthetic facts, never a captured request or a decoded runtime transcript.
    internal static JsonObject Expected(string scenario, int phase)
    {
        var messages = new JsonArray(Message("system", AprBusinessHost.Instruction), Message("user", AprBusinessHost.Data));
        if (phase >= 1) messages.Add(Assistant(AprBusinessHost.AcceptedPayload, ReasoningA));
        if (scenario == "mixed" && phase >= 2)
        {
            messages.Add(Assistant(AprBusinessHost.RejectedPayload, ReasoningB));
            messages.Add(Message("user", AprBusinessHost.Correction));
        }
        if (scenario == "mixed" && phase >= 3)
        {
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = null, ["reasoning_content"] = ReasoningTool,
                ["tool_calls"] = new JsonArray(Call()) });
            messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = "apr-increment-call", ["content"] = "{\"done\":true}" });
        }
        var schema = JsonNode.Parse("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");
        return new JsonObject
        {
            ["model"] = DeepSeekOptions.Model, ["stream"] = false, ["reasoning_effort"] = "high",
            ["thinking"] = new JsonObject { ["type"] = "enabled" }, ["max_tokens"] = 2048, ["messages"] = messages,
            ["tools"] = new JsonArray(new JsonObject { ["type"] = "function", ["function"] = new JsonObject
                { ["name"] = "increment", ["description"] = "Increment a synthetic counter once.", ["parameters"] = schema } })
        };
    }
}

internal sealed class ControlledTransport(string scenario, bool producing) : HttpMessageHandler
{
    internal int Sends { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Check.That(request.RequestUri?.AbsoluteUri == "https://api.deepseek.com/chat/completions", "Unexpected endpoint.");
        Check.That(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == (producing ? Wire.CredentialA : Wire.CredentialB), "Transport credential binding failed.");
        var phase = producing ? Sends : scenario == "mixed" ? 3 : 1;
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        Check.That(JsonNode.DeepEquals(JsonNode.Parse(body), Wire.Expected(scenario, phase)), "Exact continuation wire mismatch.");
        Sends++;
        Check.That(Sends <= (producing ? scenario == "mixed" ? 3 : 2 : 1), "Unexpected extra dispatch.");
        if (producing && scenario == "retry" && Sends == 2)
            return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        var response = producing && Sends == 1 ? Wire.Response(AprBusinessHost.AcceptedPayload, Wire.ReasoningA)
            : producing && scenario == "mixed" && Sends == 2 ? Wire.Response(AprBusinessHost.RejectedPayload, Wire.ReasoningB)
            : producing && scenario == "mixed" ? Wire.Response(null, Wire.ReasoningTool, tools: true)
            : Wire.Response(AprBusinessHost.RepairedPayload, Wire.ReasoningEnd, unknownUsage: scenario == "retry");
        return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
    }
}

internal sealed class ObservedProvider(DeepSeekProvider inner) : IModelProvider, IProviderContextPersistence
{
    internal int Imports { get; private set; }
    internal List<ProviderRequest> Requests { get; } = [];
    public ProviderScope Scope => inner.Scope;
    public ProviderCapabilities Capabilities => inner.Capabilities;
    public ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken cancellationToken = default)
    { Requests.Add(request); return inner.ExchangeAsync(request, cancellationToken); }
    public ProviderSavedState? ExportContext(ProviderContextBinding binding) => inner.ExportContext(binding);
    public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state) { Imports++; return inner.AdmitContext(binding, state); }
}

internal static class Check
{
    internal static void That(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static void Confined(string text)
    {
        foreach (var canary in Wire.Canaries) That(!text.Contains(canary, StringComparison.Ordinal), "Restricted data escaped an ordinary surface.");
    }
}

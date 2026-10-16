using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SolusAgent.Api.Usage;
using SolusAgent.Providers.DeepSeek;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;

namespace PackageConsumers.LiveSmoke;

internal static class WireObservationChecks
{
    internal const string Canary = "synthetic-wire-private-canary";
    internal static ProviderRequest Request() => new(new("deepseek", "deepseek-flash"),
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), [ProviderInput.Instruction("synthetic instruction")],
        [new("smoke_increment", "synthetic tool", ToolSchema.Parse("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"),
            ToolSchema.Parse("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"), "synthetic", ToolEffect.ReadOnly, 128, 128)],
        requiredCapabilities: ProviderCapabilities.ToolCalls | ProviderCapabilities.Continuation | ProviderCapabilities.UsageReporting);
    internal static IEnumerable<(string Name, string Body, WireObservations Expected, ProviderError Error)> Cases()
    {
        var normal = new WireObservations(true, true, 0, 1, 1, true, 0, 0);
        foreach (var name in new[] { "normal", "call-index", "call-index-batch", "call-index-unknown", "call-index-null", "call-index-string", "call-index-bool", "call-index-negative", "call-index-wrong", "call-index-fraction", "call-index-integral-fraction", "call-index-exponent", "call-index-overflow", "call-index-second-wrong", "whitespace", "text", "extra-arguments", "bad-arguments", "duplicate-arguments", "wrong-tool",
            "bad-call-id", "mixed-batch", "missing-arguments", "total", "cache", "alias", "reasoning", "negative", "fraction", "exponent",
            "bad-detail", "missing-usage", "null-usage", "partial-usage", "unknown-usage-field", "unknown-root", "unknown-message",
            "missing-content", "array-content", "wrong-role", "bad-created", "bad-index", "logprobs", "filter", "aborted", "length", "resource", "unknown-finish", "missing-finish", "missing-reasoning", "arguments-limit", "arguments-unicode", "overflow-input", "scalar-usage", "unknown-function" })
        {
            var root = JsonNode.Parse("""{"id":"synthetic-response","object":"chat.completion","model":"deepseek-flash","choices":[{"index":0,"finish_reason":"tool_calls","message":{"role":"assistant","content":null,"reasoning_content":"synthetic-wire-private-canary","tool_calls":[{"id":"synthetic-call","type":"function","function":{"name":"smoke_increment","arguments":"{}"}}]}}],"usage":{"prompt_tokens":307,"completion_tokens":49,"total_tokens":356}}""")!;
            var choice = root["choices"]![0]!; var message = choice["message"]!; var call = message["tool_calls"]![0]!; var function = call["function"]!;
            var expected = normal; var error = ProviderError.None;
            switch (name)
            {
                case "call-index": call["index"] = 0; break;
                case "call-index-batch":
                case "call-index-second-wrong":
                    call["index"] = 0; var indexed = call.DeepClone(); indexed["id"] = "second-call";
                    indexed["index"] = name == "call-index-batch" ? 1 : 0; message["tool_calls"]!.AsArray().Add(indexed);
                    if (name == "call-index-second-wrong") { expected = expected with { Shape = 4 }; error = ProviderError.InvalidResponse; }
                    break;
                case "call-index-unknown": call["index"] = 0; call[Canary] = null; expected = expected with { Shape = 4 }; error = ProviderError.InvalidResponse; break;
                case "call-index-null":
                case "call-index-string":
                case "call-index-bool":
                case "call-index-negative":
                case "call-index-wrong":
                case "call-index-fraction":
                case "call-index-integral-fraction":
                case "call-index-exponent":
                case "call-index-overflow":
                    call["index"] = JsonNode.Parse(name switch {
                        "call-index-null" => "null", "call-index-string" => "\"0\"", "call-index-bool" => "true",
                        "call-index-negative" => "-1", "call-index-wrong" => "1", "call-index-fraction" => "0.5",
                        "call-index-integral-fraction" => "0.0", "call-index-exponent" => "0e0", _ => "2147483648" });
                    expected = expected with { Shape = 4 }; error = ProviderError.InvalidResponse; break;
                case "whitespace": function["arguments"] = " { } "; break;
                case "text": message["content"] = Canary; expected = expected with { Content = 2 }; break;
                case "extra-arguments": function["arguments"] = "{\"extra\":true}"; expected = expected with { ArgumentsError = 6 }; error = ProviderError.InvalidResponse; break;
                case "bad-arguments": function["arguments"] = "{"; expected = expected with { ArgumentsError = 4 }; error = ProviderError.InvalidResponse; break;
                case "duplicate-arguments": function["arguments"] = "{\"x\":1,\"x\":1}"; expected = expected with { ArgumentsError = 4 }; error = ProviderError.InvalidResponse; break;
                case "wrong-tool": function["name"] = "unknown_tool"; expected = expected with { ArgumentsError = null }; error = ProviderError.InvalidAssociation; break;
                case "bad-call-id": call["id"] = ""; expected = expected with { CallError = 1, ArgumentsError = null }; error = ProviderError.InvalidResponse; break;
                case "mixed-batch": var other = call.DeepClone(); other["id"] = "another-call"; other["function"]!["name"] = "unknown_tool"; message["tool_calls"]!.AsArray().Add(other); expected = expected with { ArgumentsError = null }; error = ProviderError.InvalidAssociation; break;
                case "missing-arguments": function.AsObject().Remove("arguments"); expected = expected with { Shape = 6, CallError = null, ArgumentsError = null }; error = ProviderError.InvalidResponse; break;
                case "total": root["usage"]!["total_tokens"] = 357; expected = expected with { UsageValid = false }; error = ProviderError.InvalidResponse; break;
                case "cache": root["usage"]!["prompt_cache_hit_tokens"] = 300; root["usage"]!["prompt_cache_miss_tokens"] = 8; expected = expected with { UsageValid = false }; error = ProviderError.InvalidResponse; break;
                case "alias": root["usage"]!["prompt_cache_hit_tokens"] = 1; root["usage"]!["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = 2 }; expected = expected with { UsageValid = false }; error = ProviderError.InvalidResponse; break;
                case "reasoning": root["usage"]!["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 50 }; expected = expected with { UsageValid = false }; error = ProviderError.InvalidResponse; break;
                case "negative": root["usage"]!["prompt_tokens"] = -1; expected = expected with { UsageValid = false }; error = ProviderError.InvalidResponse; break;
                case "fraction": root["usage"]!["prompt_tokens"] = 1.5; expected = expected with { UsageValid = false }; error = ProviderError.InvalidResponse; break;
                case "exponent": root["usage"]!["prompt_tokens"] = JsonNode.Parse("1e2"); expected = expected with { UsageValid = false }; error = ProviderError.InvalidResponse; break;
                case "bad-detail": root["usage"]!["completion_tokens_details"] = 4; expected = expected with { UsageValid = false }; error = ProviderError.InvalidResponse; break;
                case "missing-usage": root.AsObject().Remove("usage"); expected = expected with { UsageValid = null }; break;
                case "null-usage": root["usage"] = null; expected = expected with { UsageValid = null }; break;
                case "partial-usage": root["usage"] = new JsonObject { ["prompt_cache_hit_tokens"] = 4 }; break;
                case "unknown-usage-field": root["usage"]![Canary] = 1; break;
                case "unknown-root": root[Canary] = null; expected = expected with { Shape = 1 }; error = ProviderError.InvalidResponse; break;
                case "unknown-message": message[Canary] = null; expected = expected with { Shape = 3 }; error = ProviderError.InvalidResponse; break;
                case "missing-content": message.AsObject().Remove("content"); expected = expected with { Shape = 6, Content = 0 }; error = ProviderError.InvalidResponse; break;
                case "array-content": message["content"] = new JsonArray(); expected = expected with { Content = 3 }; error = ProviderError.InvalidResponse; break;
                case "wrong-role": message["role"] = "user"; expected = expected with { IdentityValid = false }; error = ProviderError.InvalidResponse; break;
                case "bad-created": root["created"] = "invalid"; expected = expected with { IdentityValid = false }; error = ProviderError.InvalidResponse; break;
                case "bad-index": choice["index"] = "invalid"; expected = expected with { IdentityValid = false }; error = ProviderError.InvalidResponse; break;
                case "logprobs": choice["logprobs"] = new JsonObject(); expected = expected with { Shape = 2 }; error = ProviderError.InvalidResponse; break;
                case "filter": choice["finish_reason"] = "content_filter"; expected = expected with { Finish = 3 }; error = ProviderError.InvalidResponse; break;
                case "aborted": choice["finish_reason"] = "aborted"; expected = expected with { Finish = 5 }; error = ProviderError.InvalidResponse; break;
                case "length": choice["finish_reason"] = "length"; expected = expected with { Finish = 2 }; error = ProviderError.InvalidResponse; break;
                case "resource": choice["finish_reason"] = "insufficient_system_resource"; expected = expected with { Finish = 4 }; error = ProviderError.InvalidResponse; break;
                case "unknown-finish": choice["finish_reason"] = Canary; expected = expected with { Finish = 6 }; error = ProviderError.InvalidResponse; break;
                case "missing-finish": choice.AsObject().Remove("finish_reason"); expected = expected with { Shape = 6, Finish = null }; error = ProviderError.InvalidResponse; break;
                case "arguments-limit": function["arguments"] = new string(' ', 129) + "{}"; expected = expected with { ArgumentsError = 2 }; error = ProviderError.InvalidResponse; break;
                case "arguments-unicode": function["arguments"] = "{\"x\":\"\\uD800\"}"; expected = expected with { ArgumentsError = 3 }; error = ProviderError.InvalidResponse; break;
                case "overflow-input": root["usage"]!["prompt_tokens"] = JsonNode.Parse("9223372036854775808"); expected = expected with { UsageValid = false }; error = ProviderError.InvalidResponse; break;
                case "scalar-usage": root["usage"] = 1; expected = expected with { UsageValid = false }; error = ProviderError.InvalidResponse; break;
                case "unknown-function": function[Canary] = null; expected = expected with { Shape = 5 }; error = ProviderError.InvalidResponse; break;
                case "missing-reasoning": message.AsObject().Remove("reasoning_content"); error = ProviderError.ContinuationMismatch; break;
            }
            yield return (name, root.ToJsonString(), expected, error);
        }
        yield return ("duplicate-usage", "{\"usage\":{\"prompt_tokens\":307},\"usage\":{\"completion_tokens\":49}}", new(JsonValid: false), ProviderError.InvalidResponse);
        yield return ("broken", "{", new(JsonValid: false), ProviderError.InvalidResponse);
        yield return ("deep", "{\"a\":" + new string('[', 17) + "0" + new string(']', 17) + "}", new(JsonValid: false), ProviderError.InvalidResponse);
        yield return ("duplicate", "{\"a\":1,\"a\":2}", new(JsonValid: false), ProviderError.InvalidResponse);
        yield return ("unicode", "{\"a\":\"\\uD800\"}", new(JsonValid: false), ProviderError.InvalidResponse);
    }
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        foreach (var test in Cases())
        {
            Check.That(WireObservations.Read(test.Body, Request()) == test.Expected);
            ProviderExchangeResult? first = null; string? requestBytes = null;
            foreach (var mode in new[] { 0, 1, 2 })
            {
                var request = Request(); var observer = new WireObserver(fail: mode == 2); observer.Begin(request);
                using var handler = new Terminal(test.Body, mode == 0 ? null : observer);
                using var provider = new DeepSeekProvider(new("synthetic-credential", 4096), handler);
                var result = await provider.ExchangeAsync(request); observer.End();
                Check.That(result.Error == test.Error && handler.Sends == 1 && handler.ResponseUnchanged);
                Check.That(mode == 1 ? observer.Value == test.Expected : observer.Value == new WireObservations());
                Check.That(!JsonSerializer.Serialize(observer.Value).Contains(Canary, StringComparison.Ordinal));
                if (first is null) { first = result; requestBytes = handler.RequestBody; }
                else Check.That(first.Error == result.Error && first.Outcome == result.Outcome
                    && first.Observation.Usage.InputTokens == result.Observation.Usage.InputTokens
                    && first.Observation.Usage.OutputTokens == result.Observation.Usage.OutputTokens && requestBytes == handler.RequestBody);
            }
            count++;
        }
        return count;
    }
    private sealed class Terminal(string body, WireObserver? observer) : HttpMessageHandler
    {
        internal int Sends; internal string? RequestBody; internal bool ResponseUnchanged;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Sends++; RequestBody = await request.Content!.ReadAsStringAsync(token); observer?.Observe(body);
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            ResponseUnchanged = (await content.ReadAsByteArrayAsync(token)).SequenceEqual(Encoding.UTF8.GetBytes(body));
            return new(HttpStatusCode.OK) { Content = content };
        }
    }
}

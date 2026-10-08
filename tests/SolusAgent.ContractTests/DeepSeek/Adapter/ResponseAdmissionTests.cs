using System.Text;
using System.Text.Json.Nodes;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.Adapter;

public sealed class ResponseAdmissionTests
{
    public static IEnumerable<object[]> UnsuccessfulFinishes()
    {
        foreach (var finish in new[] { "length", "content_filter", "insufficient_system_resource", "aborted", "unknown", "", "STOP" })
            foreach (var calls in new[] { false, true }) yield return [finish, calls];
    }
    [Theory]
    [MemberData(nameof(UnsuccessfulFinishes))]
    public async Task IncompleteOrUnsupportedFinishNeverAdmitsValidLookingFinalOrCalls(string finish, bool hasCalls)
    {
        using var handler = FakeHandler.Reply(AdapterFixture.Response("valid looking candidate", finish: finish,
            calls: hasCalls ? [AdapterFixture.Call()] : null));
        using var provider = AdapterFixture.Provider(handler);
        var result = await provider.ExchangeAsync(AdapterFixture.Request(tools: [AdapterFixture.Tool()]));
        Assert.Equal(ProviderOutcome.Rejected, result.Outcome); Assert.Equal(ProviderError.InvalidResponse, result.Error);
        Assert.Null(result.Response); Assert.Equal(10, result.Observation.Usage.InputTokens); Assert.Equal(5, result.Observation.Usage.OutputTokens);
        Assert.Equal(DispatchExposure.Dispatched, result.Observation.Exposure); Assert.Equal(1, handler.Sends);
    }
    [Theory]
    [InlineData("deepseek-v4-flash")]
    [InlineData("deepseek-v4.1-flash")]
    [InlineData("deepseek-chat")]
    [InlineData("deepseek-reasoner")]
    [InlineData("Deepseek-flash")]
    [InlineData("deepseek-flash ")]
    public async Task RequestCanonicalSelectionDoesNotInferAliasResponseIdentity(string model)
    {
        using var handler = FakeHandler.Reply(AdapterFixture.Response(model: model)); using var provider = AdapterFixture.Provider(handler);
        var result = await provider.ExchangeAsync(AdapterFixture.Request()); Assert.Equal(ProviderError.InvalidResponse, result.Error);
        Assert.Null(result.Response); Assert.Equal(UsageCompleteness.Complete, result.Observation.Usage.Completeness);
    }
    public static IEnumerable<object[]> InvalidShapes()
    {
        foreach (var mutation in new[] { "missing-model", "missing-id", "empty-id", "chunk", "two-choices", "zero-choices", "index", "role",
            "content-array", "missing-content", "null-finish", "missing-finish", "function-call", "delta", "tool-array", "stop-with-calls",
            "tool-finish-empty", "wrong-tool-type", "unknown-tool", "duplicate-call", "duplicate-argument", "bad-schema", "fractional-integer", "extra-argument", "missing-reasoning", "null-reasoning" })
        {
            var root = JsonNode.Parse(AdapterFixture.Response())!; var choice = root["choices"]![0]!; var message = choice["message"]!;
            switch (mutation)
            {
                case "missing-model": root.AsObject().Remove("model"); break;
                case "missing-id": root.AsObject().Remove("id"); break;
                case "empty-id": root["id"] = ""; break;
                case "chunk": root["object"] = "chat.completion.chunk"; break;
                case "two-choices": root["choices"]!.AsArray().Add(choice.DeepClone()); break;
                case "zero-choices": root["choices"] = new JsonArray(); break;
                case "index": choice["index"] = 1; break;
                case "role": message["role"] = "system"; break;
                case "content-array": message["content"] = new JsonArray("untrusted"); break;
                case "missing-content": message.AsObject().Remove("content"); break;
                case "null-finish": choice["finish_reason"] = null; break;
                case "missing-finish": choice.AsObject().Remove("finish_reason"); break;
                case "function-call": message["function_call"] = new JsonObject(); break;
                case "delta": choice["delta"] = new JsonObject(); break;
                case "tool-array": message["tool_calls"] = new JsonObject(); break;
                case "stop-with-calls": message["tool_calls"] = Calls(); break;
                case "tool-finish-empty": choice["finish_reason"] = "tool_calls"; message["tool_calls"] = new JsonArray(); break;
                case "missing-reasoning": message.AsObject().Remove("reasoning_content"); break;
                case "null-reasoning": message["reasoning_content"] = null; break;
                default:
                    choice["finish_reason"] = "tool_calls"; message["tool_calls"] = Calls();
                    var call = message["tool_calls"]![0]!;
                    if (mutation == "wrong-tool-type") call["type"] = "custom";
                    if (mutation == "unknown-tool") call["function"]!["name"] = "other";
                    if (mutation == "duplicate-call") message["tool_calls"]!.AsArray().Add(call.DeepClone());
                    if (mutation == "duplicate-argument") call["function"]!["arguments"] = "{\"text\":\"a\",\"text\":\"b\"}";
                    if (mutation == "bad-schema") call["function"]!["arguments"] = "{\"text\":4}";
                    if (mutation == "fractional-integer") call["function"]!["arguments"] = "{\"text\":\"a\",\"count\":1.0}";
                    if (mutation == "extra-argument") call["function"]!["arguments"] = "{\"text\":\"a\",\"endpoint\":\"https://untrusted.invalid\"}";
                    break;
            }
            yield return [mutation, root.ToJsonString()];
        }
        var canonical = AdapterFixture.Response();
        yield return ["duplicate-model", canonical.Replace("\"model\":\"deepseek-flash\"", "\"model\":\"deepseek-flash\",\"model\":\"deepseek-flash\"")];
        yield return ["escaped-duplicate-content", canonical.Replace("\"content\":\"final\"", "\"content\":\"final\",\"\\u0063ontent\":\"final\"")];
        yield return ["unpaired-surrogate", canonical.Replace("\"content\":\"final\"", "\"content\":\"\\uD800\"")];
        static JsonArray Calls() => JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new[] { AdapterFixture.Call() }))!.AsArray();
    }
    [Theory]
    [MemberData(nameof(InvalidShapes))]
    public async Task UnsupportedIdentityAssociationShapeAndUnicodeRetainValidatedUsage(string mutation, string body)
    {
        using var handler = FakeHandler.Reply(body); using var provider = AdapterFixture.Provider(handler);
        var result = await provider.ExchangeAsync(AdapterFixture.Request(tools: [AdapterFixture.Tool()]));
        Assert.True(result.Outcome == ProviderOutcome.Rejected, mutation); Assert.Null(result.Response);
        Assert.Equal(10, result.Observation.Usage.InputTokens); Assert.Equal(5, result.Observation.Usage.OutputTokens); Assert.Equal(1, handler.Sends);
    }
    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"deep\":[[[[[[[[[[[[[[[[[0]]]]]]]]]]]]]]]]]}")]
    [InlineData("{\"id\":1,}")]
    [InlineData("/*comment*/{}")]
    public async Task UnparseableBodyCannotInventUsage(string body)
    {
        using var handler = FakeHandler.Reply(body); using var provider = AdapterFixture.Provider(handler);
        var result = await provider.ExchangeAsync(AdapterFixture.Request()); Assert.Equal(ProviderError.InvalidResponse, result.Error);
        Assert.Null(result.Response); Assert.Equal(UsageCompleteness.Unavailable, result.Observation.Usage.Completeness);
    }
    [Fact]
    public async Task InvalidRawUtf8IsRejectedBeforePayloadAndMeasurements()
    {
        using var handler = new FakeHandler((_, _) =>
        {
            var response = AdapterFixture.Http(AdapterFixture.Response());
            response.Content = new ByteArrayContent([0xff, 0xfe]); response.Content.Headers.ContentType = new("application/json");
            return Task.FromResult(response);
        });
        using var provider = AdapterFixture.Provider(handler); var result = await provider.ExchangeAsync(AdapterFixture.Request());
        Assert.Equal(ProviderError.InvalidResponse, result.Error); Assert.Null(result.Response);
        Assert.Equal(UsageCompleteness.Unavailable, result.Observation.Usage.Completeness);
    }
    [Theory]
    [InlineData(8191, true)]
    [InlineData(8192, false)]
    public async Task RestrictedContinuationIncludesFrameInExactByteCeiling(int reasoningBytes, bool accepted)
    {
        using var handler = FakeHandler.Reply(AdapterFixture.Response(reasoning: new string('r', reasoningBytes)));
        using var provider = AdapterFixture.Provider(handler); var result = await provider.ExchangeAsync(AdapterFixture.Request(tools: [AdapterFixture.Tool()]));
        Assert.Equal(accepted ? ProviderOutcome.Succeeded : ProviderOutcome.Rejected, result.Outcome);
        if (accepted) Assert.Equal(8192, result.Response!.Continuation!.ByteCount);
        else { Assert.Equal(ProviderError.LimitExceeded, result.Error); Assert.Null(result.Response); }
        Assert.Equal(10, result.Observation.Usage.InputTokens);
    }
    [Fact]
    public async Task TextOnlyExchangeCanExplicitlyDeclineRestrictedContinuation()
    {
        using var handler = FakeHandler.Reply(AdapterFixture.Response()); using var provider = AdapterFixture.Provider(handler);
        var result = await provider.ExchangeAsync(AdapterFixture.Request(capabilities: ProviderCapabilities.UsageReporting));
        Assert.Equal(ProviderOutcome.Succeeded, result.Outcome); Assert.Null(result.Response!.Continuation);
    }

    [Theory]
    [InlineData("calls")]
    [InlineData("response")]
    [InlineData("continuation")]
    [InlineData("text")]
    public async Task ActualWireCandidateRespectsLoweredAndAbsoluteLogicalBounds(string boundary)
    {
        var bounds = boundary switch
        {
            "calls" => new ProviderExchangeBounds(maximumToolCalls: 1),
            "response" => new ProviderExchangeBounds(maximumResponseBytes: 1),
            "continuation" => new ProviderExchangeBounds(maximumContinuationBytes: 1),
            _ => new ProviderExchangeBounds(),
        };
        var body = boundary == "calls"
            ? AdapterFixture.Response(finish: "tool_calls", calls: [AdapterFixture.Call("first"), AdapterFixture.Call("second")])
            : AdapterFixture.Response(text: boundary == "text" ? new string('x', ProviderLimits.TextBytes + 1) : "final");
        using var handler = FakeHandler.Reply(body); using var provider = AdapterFixture.Provider(handler);
        var result = await provider.ExchangeAsync(AdapterFixture.Request(tools: [AdapterFixture.Tool()], bounds: bounds));
        Assert.Equal(ProviderError.LimitExceeded, result.Error); Assert.Null(result.Response);
        Assert.Equal(10, result.Observation.Usage.InputTokens); Assert.Equal(1, handler.Sends);
    }

    [Fact]
    public async Task WireCannotRecycleAnAcceptedHistoricalCallAfterItsActualToolResult()
    {
        var tool = new EchoTool();
        using var handler = FakeHandler.Reply(AdapterFixture.Response(finish: "tool_calls", calls: [AdapterFixture.Call()]));
        using var provider = AdapterFixture.Provider(handler);
        var initial = AdapterFixture.Request(tools: [tool.Descriptor]);
        var first = await provider.ExchangeAsync(initial); Assert.Equal(ProviderOutcome.Succeeded, first.Outcome);
        var call = first.Response!.Calls[0];
        var output = await tool.InvokeAsync(tool.Prepare(call).Prepared!, call, new EchoCapability());
        var next = AdapterFixture.Request(initial.Inputs.Concat([ProviderInput.FromModel(first.Response), ProviderInput.FromTool(output)]).ToArray(),
            [tool.Descriptor], first.Response.Continuation);
        var recycled = await provider.ExchangeAsync(next);
        Assert.Equal(ProviderError.InvalidAssociation, recycled.Error); Assert.Null(recycled.Response);
        Assert.Equal(10, recycled.Observation.Usage.InputTokens); Assert.Equal(2, handler.Sends); Assert.Equal(1, tool.Invocations);
    }
}

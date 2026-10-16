using System.Text.Json;
using System.Text.Json.Nodes;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.Adapter;

public sealed class ToolCallIndexTests
{
    private const string Arguments = " { \"text\" : \"index-argument-canary\" } ";
    [Theory]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(2, 0)] [InlineData(2, 1)]
    [InlineData(2, 2)] [InlineData(2, 3)] [InlineData(3, 7)]
    public async Task ValidOptionalIndicesPreserveOriginalCallCarriers(int count, int indexedMask)
    {
        var root = JsonNode.Parse(Body(count))!;
        var calls = root["choices"]![0]!["message"]!["tool_calls"]!.AsArray();
        for (var ordinal = 0; ordinal < calls.Count; ordinal++)
            if ((indexedMask & (1 << ordinal)) != 0) calls[ordinal]!["index"] = ordinal;
        using var handler = FakeHandler.Reply(root.ToJsonString()); using var provider = AdapterFixture.Provider(handler);
        var result = await provider.ExchangeAsync(AdapterFixture.Request(tools: [AdapterFixture.Tool()]));
        Assert.Equal(ProviderOutcome.Succeeded, result.Outcome); Assert.True(result.Response!.Accepted);
        Assert.Equal(Enumerable.Range(0, count).Select(i => "call-" + i), result.Response.Calls.Select(call => call.CallId));
        Assert.All(result.Response.Calls, call => { Assert.Equal("echo", call.ToolName); Assert.Equal(Arguments, call.ArgumentsJson); });
        Assert.Equal(count, result.Response.Calls.Count); Assert.NotNull(result.Response.Continuation); Assert.Equal(1, handler.Sends);
    }

    public static IEnumerable<object[]> InvalidIndices()
    {
        foreach (var position in new[] { 0, 1 })
        {
            foreach (var literal in new[] { "null", "\"0\"", "true", "false", "-1", "0.5", "0.0", "0e0",
                "2147483648", "9223372036854775808" }) yield return [position, literal];
            yield return [position, position == 0 ? "1" : "0"];
            if (position == 1) yield return [position, "2"];
        }
    }
    [Theory]
    [MemberData(nameof(InvalidIndices))]
    public async Task InvalidTypeOrPositionRejectsWithRetainedUsage(int position, string literal)
    {
        var root = JsonNode.Parse(AdapterFixture.WithCallIndices(Body(2)))!;
        root["choices"]![0]!["message"]!["tool_calls"]![position]!["index"] = JsonNode.Parse(literal);
        await Reject(root.ToJsonString(), ProviderError.InvalidResponse);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DirectAndEscapedDuplicateCallIndexKeysRemainRejected(bool escaped)
    {
        var body = AdapterFixture.WithCallIndices(Body(1));
        var replacement = escaped ? ",\"index\":0,\"\\u0069ndex\":0}" : ",\"index\":0,\"index\":0}";
        Assert.Contains(",\"index\":0}", body);
        await Reject(body.Replace(",\"index\":0}", replacement, StringComparison.Ordinal), ProviderError.InvalidResponse);
    }

    [Theory]
    [InlineData("unknown-call")] [InlineData("unknown-function")] [InlineData("missing-id")]
    [InlineData("missing-type")] [InlineData("missing-function")] [InlineData("wrong-type")]
    [InlineData("duplicate-id")] [InlineData("unknown-tool")] [InlineData("bad-schema")]
    public async Task ValidIndicesCannotBypassExistingShapeAssociationOrSchema(string mutation)
    {
        var root = JsonNode.Parse(AdapterFixture.WithCallIndices(Body(2)))!;
        var calls = root["choices"]![0]!["message"]!["tool_calls"]!.AsArray(); var call = calls[1]!;
        switch (mutation)
        {
            case "unknown-call": call["extra"] = 0; break;
            case "unknown-function": call["function"]!["index"] = 1; break;
            case "missing-id": call.AsObject().Remove("id"); break;
            case "missing-type": call.AsObject().Remove("type"); break;
            case "missing-function": call.AsObject().Remove("function"); break;
            case "wrong-type": call["type"] = "other"; break;
            case "duplicate-id": call["id"] = "call-0"; break;
            case "unknown-tool": call["function"]!["name"] = "unknown_tool"; break;
            case "bad-schema": call["function"]!["arguments"] = "{\"text\":4}"; break;
        }
        await Reject(root.ToJsonString(), mutation is "duplicate-id" or "unknown-tool"
            ? ProviderError.InvalidAssociation : ProviderError.InvalidResponse);
    }

    [Fact]
    public async Task LaterIndexedResponseRestartsAtZeroAndRetainsCallIdAuthority()
    {
        var tool = new EchoTool();
        var bodies = new Queue<string>([
            AdapterFixture.WithCallIndices(AdapterFixture.Response(null, AdapterFixture.Replay, "tool_calls", calls: [AdapterFixture.Call("first")])),
            AdapterFixture.WithCallIndices(AdapterFixture.Response(null, AdapterFixture.Replay, "tool_calls", calls: [AdapterFixture.Call("later")]))]);
        using var handler = new FakeHandler((_, _) => Task.FromResult(AdapterFixture.Http(bodies.Dequeue())));
        using var provider = AdapterFixture.Provider(handler);
        var request = AdapterFixture.Request(tools: [tool.Descriptor]); var first = await provider.ExchangeAsync(request);
        Assert.Equal(ProviderOutcome.Succeeded, first.Outcome);
        var call = Assert.Single(first.Response!.Calls);
        var result = await tool.InvokeAsync(tool.Prepare(call).Prepared!, call, new EchoCapability());
        var nextRequest = AdapterFixture.Request(request.Inputs.Concat([ProviderInput.FromModel(first.Response), ProviderInput.FromTool(result)]).ToArray(),
            [tool.Descriptor], first.Response.Continuation);
        var next = await provider.ExchangeAsync(nextRequest);
        Assert.Equal(ProviderOutcome.Succeeded, next.Outcome); Assert.Equal("later", Assert.Single(next.Response!.Calls).CallId);
        Assert.Equal(2, handler.Sends); Assert.Equal(1, tool.Invocations);
        using var recycled = AdapterFixture.Provider(FakeHandler.Reply(AdapterFixture.WithCallIndices(
            AdapterFixture.Response(null, AdapterFixture.Replay, "tool_calls", calls: [AdapterFixture.Call("first")]))));
        Assert.Equal(ProviderError.InvalidAssociation, (await recycled.ExchangeAsync(AdapterFixture.Request(nextRequest.Inputs, [tool.Descriptor], first.Response.Continuation))).Error);
    }

    private static string Body(int count) => AdapterFixture.Response(null, AdapterFixture.Replay, "tool_calls",
        calls: Enumerable.Range(0, count).Select(i => AdapterFixture.Call("call-" + i, args: Arguments)).ToArray());
    private static async Task Reject(string body, ProviderError expected)
    {
        using var handler = FakeHandler.Reply(body); using var provider = AdapterFixture.Provider(handler);
        var result = await provider.ExchangeAsync(AdapterFixture.Request(tools: [AdapterFixture.Tool()]));
        Assert.Equal(ProviderOutcome.Rejected, result.Outcome); Assert.Equal(expected, result.Error); Assert.Null(result.Response);
        Assert.Equal(10, result.Observation.Usage.InputTokens); Assert.Equal(5, result.Observation.Usage.OutputTokens);
        Assert.Equal(DispatchExposure.Dispatched, result.Observation.Exposure); Assert.Equal(1, handler.Sends);
        var ordinary = JsonSerializer.Serialize(result.Diagnostic);
        Assert.DoesNotContain(AdapterFixture.Replay, ordinary); Assert.DoesNotContain("index-argument-canary", ordinary);
    }
}

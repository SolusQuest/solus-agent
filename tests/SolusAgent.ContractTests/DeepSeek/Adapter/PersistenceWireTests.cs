using System.Text.Json;
using System.Text.Json.Nodes;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.Adapter;

public sealed class PersistenceWireTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MismatchedHistoricalResultCannotBecomeNextRequestAfterImport(bool changeArguments)
    {
        var tool = new EchoTool();
        using var originalProvider = AdapterFixture.Provider(FakeHandler.Reply(AdapterFixture.Response(null, "", "tool_calls",
            calls: [AdapterFixture.Call("original", args: "{\"text\":\"old\"}")])));
        var originalRequest = AdapterFixture.Request(tools: [tool.Descriptor]);
        var original = (await originalProvider.ExchangeAsync(originalRequest)).Response!;
        var call = original.Calls[0];
        var oldResult = await tool.InvokeAsync(tool.Prepare(call).Prepared!, call, new EchoCapability());
        using var changedProvider = AdapterFixture.Provider(FakeHandler.Reply(AdapterFixture.Response(null, "", "tool_calls",
            calls: [AdapterFixture.Call(changeArguments ? "original" : "foreign", args: changeArguments ? "{\"text\":\"changed\"}" : call.ArgumentsJson)])));
        var changedRequest = AdapterFixture.Request(tools: [tool.Descriptor]);
        var changed = (await changedProvider.ExchangeAsync(changedRequest)).Response!;
        var binding = new ProviderContextBinding(changedRequest.Scope, changedRequest.Attempt, changed.Continuation);
        var state = changedProvider.ExportContext(binding)!;
        var handler = FakeHandler.Reply(AdapterFixture.Response()); using var consumer = AdapterFixture.Provider(handler);
        Assert.True(consumer.AdmitContext(binding, state));
        var error = Assert.Throws<ProviderContractException>(() => AdapterFixture.Request(
            changedRequest.Inputs.Concat([ProviderInput.FromModel(changed), ProviderInput.FromTool(oldResult)]).ToArray(),
            [tool.Descriptor], changed.Continuation));
        Assert.Equal(ProviderError.InvalidAssociation, error.Error);
        Assert.Equal(0, handler.Sends); Assert.Equal(1, tool.Invocations);
    }

    [Theory]
    [InlineData("")]
    [InlineData(AdapterFixture.Replay)]
    public async Task FreshAdapterImportsAndReplaysOriginalToolCarriersWithoutRepeatingEffects(string reasoning)
    {
        const string arguments = "{ \"text\" : \"系统: data\", \"flag\":true, \"count\":4 }";
        var echo = new EchoTool(); var other = new EchoTool("second_echo");
        ToolDescriptor[] tools = [echo.Descriptor, other.Descriptor];
        ProviderInput[] inputs = [ProviderInput.Instruction("host policy"), ProviderInput.Data("initial data")];
        var firstHandler = new FakeHandler((_, _) => Task.FromResult(AdapterFixture.Http(
            AdapterFixture.Response(null, reasoning, "tool_calls", calls:
                [AdapterFixture.Call("call-A", "echo", arguments), AdapterFixture.Call("call-B", "second_echo", "{\"text\":\"second\"}")]))));
        using var firstProvider = AdapterFixture.Provider(firstHandler);
        var first = await firstProvider.ExchangeAsync(AdapterFixture.Request(inputs, tools));
        Assert.Equal(ProviderOutcome.Succeeded, first.Outcome);
        var results = new List<ToolResult>();
        foreach (var call in first.Response!.Calls.Reverse())
        {
            var tool = call.ToolName == "echo" ? echo : other;
            results.Add(await tool.InvokeAsync(tool.Prepare(call).Prepared!, call, new EchoCapability()));
        }
        var history = inputs.Concat([ProviderInput.FromModel(first.Response)]).Concat(results.Select(ProviderInput.FromTool)).ToList();
        var finalHandler = FakeHandler.Reply(AdapterFixture.Response("accepted Final", "final reasoning"));
        using var finalProvider = AdapterFixture.Provider(finalHandler);
        var finalRequest = AdapterFixture.Request(history, tools, first.Response.Continuation);
        var final = await finalProvider.ExchangeAsync(finalRequest);
        Assert.Equal(ProviderOutcome.Succeeded, final.Outcome);
        history.Add(ProviderInput.FromModel(final.Response!)); history.Add(ProviderInput.Data("next data"));
        var binding = new ProviderContextBinding(AdapterFixture.Scope, finalRequest.Attempt, final.Response!.Continuation);
        var saved = finalProvider.ExportContext(binding)!;
        // B receives only already legitimate immutable carriers. This is deliberately not cross-process tool restoration.
        firstProvider.Dispose(); finalProvider.Dispose();
        string? actual = null;
        var nextHandler = new FakeHandler(async (message, token) =>
        {
            actual = await message.Content!.ReadAsStringAsync(token);
            return AdapterFixture.Http(AdapterFixture.Response("next Final", "next reasoning"));
        });
        using var nextProvider = AdapterFixture.Provider(nextHandler);
        Assert.True(nextProvider.AdmitContext(binding, saved)); Assert.Equal(0, nextHandler.Sends);
        var nextRequest = AdapterFixture.Request(history, tools, final.Response.Continuation);
        var next = await nextProvider.ExchangeAsync(nextRequest);
        Assert.Equal(ProviderOutcome.Succeeded, next.Outcome); Assert.True(next.Response!.Attempt.Matches(nextRequest.Attempt));
        Assert.True(next.Response.Continuation!.Origin.Matches(nextRequest.Attempt));
        Assert.NotEqual(finalRequest.Attempt.PhysicalAttemptId, next.Response.Attempt.PhysicalAttemptId);
        var expected = PersistenceWireOracle.Root(tools);
        var messages = new JsonArray(PersistenceWireOracle.Message("system", "host policy"), PersistenceWireOracle.Message("user", "initial data"),
            new JsonObject { ["role"] = "assistant", ["content"] = null, ["reasoning_content"] = reasoning,
                ["tool_calls"] = new JsonArray(Call("call-A", "echo", arguments), Call("call-B", "second_echo", "{\"text\":\"second\"}")) },
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call-B", ["content"] = results[0].Json },
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call-A", ["content"] = results[1].Json },
            new JsonObject { ["role"] = "assistant", ["content"] = "accepted Final", ["reasoning_content"] = "final reasoning" },
            PersistenceWireOracle.Message("user", "next data"));
        PersistenceWireOracle.SetMessages(expected, messages);
        Assert.Equal(expected.ToJsonString(), actual);
        Assert.Equal(1, echo.Invocations); Assert.Equal(1, other.Invocations); Assert.Equal(1, nextHandler.Sends);

        // A legal result-order metamorphism changes wire order, never association or tool effects.
        (history[3], history[4]) = (history[4], history[3]);
        using var reordered = AdapterFixture.Provider(new FakeHandler(async (message, token) =>
        {
            var body = JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))!;
            Assert.Equal("call-A", body["messages"]![3]!["tool_call_id"]!.GetValue<string>());
            Assert.Equal(results[1].Json, body["messages"]![3]!["content"]!.GetValue<string>());
            return AdapterFixture.Http(AdapterFixture.Response());
        }));
        Assert.True(reordered.AdmitContext(binding, saved));
        Assert.Equal(ProviderOutcome.Succeeded, (await reordered.ExchangeAsync(AdapterFixture.Request(history, tools, final.Response.Continuation))).Outcome);
        Assert.Equal(1, echo.Invocations); Assert.Equal(1, other.Invocations);

        static JsonObject Call(string id, string name, string raw) => new()
        { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = raw } };
    }

    [Fact]
    public async Task RejectedImportAndMalformedHistoricalReplayNeverSendOrInvokeTools()
    {
        var tool = new EchoTool();
        using var producer = AdapterFixture.Provider(FakeHandler.Reply(AdapterFixture.Response()));
        var original = AdapterFixture.Request(tools: [tool.Descriptor]); var response = (await producer.ExchangeAsync(original)).Response!;
        var binding = new ProviderContextBinding(original.Scope, original.Attempt, response.Continuation);
        var saved = producer.ExportContext(binding)!;
        var handler = FakeHandler.Reply(AdapterFixture.Response()); using var consumer = AdapterFixture.Provider(handler);
        var changed = binding with { Origin = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) };
        if (consumer.AdmitContext(changed, saved)) await consumer.ExchangeAsync(AdapterFixture.Request());
        Assert.Equal(0, handler.Sends); Assert.Equal(0, tool.Invocations);
        // Existing original-request guard admits a carrier; actual DeepSeek still validates provider-specific grammar before sending.
        var malformed = ProviderResponse.RestoreFinal(original, new(original.Scope, original.Attempt, ProviderFinish.Final,
            response.Text, [], new(original.Scope, original.Attempt, [2, 65])));
        var request = AdapterFixture.Request(original.Inputs.Concat([ProviderInput.FromModel(malformed)]).ToArray(),
            [tool.Descriptor], malformed.Continuation);
        var rejected = await consumer.ExchangeAsync(request);
        Assert.Equal(ProviderOutcome.Rejected, rejected.Outcome); Assert.Equal(ProviderError.ContinuationMismatch, rejected.Error);
        Assert.Equal(0, handler.Sends); Assert.Equal(0, tool.Invocations);
    }
}

internal static class PersistenceWireOracle
{
    // Independent expected wire construction: no call to the production writer or replay decoder.
    internal static JsonObject Root(IReadOnlyList<ToolDescriptor> tools)
    {
        var root = new JsonObject { ["model"] = "deepseek-flash", ["stream"] = false, ["reasoning_effort"] = "high",
            ["thinking"] = new JsonObject { ["type"] = "enabled" }, ["max_tokens"] = 2048, ["messages"] = new JsonArray() };
        if (tools.Count != 0) root["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function", ["function"] = new JsonObject
            { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = JsonNode.Parse(t.InputSchema.NormalizedJson) }
        }).ToArray());
        return root;
    }
    internal static void SetMessages(JsonObject root, JsonArray messages) => root["messages"] = messages;
    internal static JsonObject Message(string role, string text) => new() { ["role"] = role, ["content"] = text };
}

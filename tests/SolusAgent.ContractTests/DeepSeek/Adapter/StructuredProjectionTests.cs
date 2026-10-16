using System.Text;
using System.Text.Json;
using CustomTools;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.Adapter;

public sealed class StructuredProjectionTests
{
    [Fact]
    public async Task AllFiveCompleteDefinitionsFitDevelopmentRequestAndActualProjectionReplayPreservesStructuredResults()
    {
        var tools = StructuredReadFixtures.Names.Select(n => new StructuredReadTool(n)).ToArray();
        var definitions = tools.Select(t => t.Descriptor).ToArray(); var bodies = new List<string>();
        var bounds = new ProviderExchangeBounds(maximumTools: 5, maximumToolCalls: 3, maximumRequestBytes: 65536, maximumResponseBytes: 16384);
        using var handler = new FakeHandler(async (message, token) =>
        {
            bodies.Add(await message.Content!.ReadAsStringAsync(token));
            return AdapterFixture.Http(bodies.Count == 1 ? AdapterFixture.Response(null, AdapterFixture.Replay, "tool_calls",
                calls: [AdapterFixture.Call("file-original", "read_file", StructuredReadFixtures.Arguments("read_file")),
                    AdapterFixture.Call("diff-original", "read_diff", StructuredReadFixtures.Arguments("read_diff"))]) : AdapterFixture.Response());
        });
        using var provider = AdapterFixture.Provider(handler);
        var request = AdapterFixture.Request(tools: definitions, bounds: bounds);
        var definitionBytes = definitions.Sum(d => Encoding.UTF8.GetByteCount(d.Name + d.Description + d.CapabilityId
            + d.InputSchema.NormalizedJson + d.ResultSchema.NormalizedJson));
        Assert.InRange(definitionBytes, 1, request.PayloadByteCount); Assert.InRange(request.PayloadByteCount, 1, 65536);
        var first = await provider.ExchangeAsync(request); Assert.Equal(ProviderOutcome.Succeeded, first.Outcome);
        var results = new List<ToolResult>(); var caps = new List<StructuredReadCapability>();
        foreach (var call in first.Response!.Calls)
        {
            var tool = tools.Single(t => t.Descriptor.Name == call.ToolName); var cap = new StructuredReadCapability(); caps.Add(cap);
            results.Add(await tool.InvokeAsync(tool.Prepare(call).Prepared!, call, cap));
        }
        Assert.All(results, result => Assert.Equal(ToolOutcome.Succeeded, result.Outcome));
        var next = AdapterFixture.Request(request.Inputs.Concat([ProviderInput.FromModel(first.Response)]).Concat(results.Select(ProviderInput.FromTool)).ToArray(),
            definitions, first.Response.Continuation, bounds: bounds);
        Assert.InRange(next.PayloadByteCount, 1, 65536);
        Assert.Equal(ProviderOutcome.Succeeded, (await provider.ExchangeAsync(next)).Outcome);
        Assert.All(caps, cap => Assert.Equal(1, cap.Entries));
        using var sent = JsonDocument.Parse(bodies[0]); var wireTools = sent.RootElement.GetProperty("tools");
        Assert.Equal(5, wireTools.GetArrayLength());
        JsonElement Parameters(string name) => wireTools.EnumerateArray().Single(t => t.GetProperty("function").GetProperty("name").GetString() == name)
            .GetProperty("function").GetProperty("parameters");
        Assert.Equal(0, Parameters("list_files").GetProperty("required").GetArrayLength());
        var count = Parameters("read_file").GetProperty("properties").GetProperty("line_count");
        Assert.Equal(1, count.GetProperty("minimum").GetInt64()); Assert.Equal(800, count.GetProperty("maximum").GetInt64());
        Assert.Equal(20, Parameters("read_diff").GetProperty("properties").GetProperty("hunk_count").GetProperty("maximum").GetInt64());
        using var replay = JsonDocument.Parse(bodies[1]); var messages = replay.RootElement.GetProperty("messages");
        Assert.Equal(StructuredReadFixtures.Arguments("read_file"), messages[2].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal(AdapterFixture.Replay, messages[2].GetProperty("reasoning_content").GetString());
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(first.Response.Calls[i].CallId, messages[i + 3].GetProperty("tool_call_id").GetString());
            Assert.Equal(results[i].Json, messages[i + 3].GetProperty("content").GetString());
        }
        using var output = JsonDocument.Parse(messages[3].GetProperty("content").GetString()!);
        Assert.Equal(100, output.RootElement.GetProperty("lines").GetArrayLength());
        Assert.Equal(JsonValueKind.Object, output.RootElement.GetProperty("reviewed_identity").ValueKind);
    }

    [Fact]
    public async Task ActualWireParametersRetainNestedObjectArrayConstraintsAndRealDomainHookAdmitsThem()
    {
        var tool = new NestedInputTool(); string? body = null;
        using var handler = new FakeHandler(async (message, token) =>
        {
            body = await message.Content!.ReadAsStringAsync(token);
            return AdapterFixture.Http(AdapterFixture.Response(null, AdapterFixture.Replay, "tool_calls",
                calls: [AdapterFixture.Call("nested-original", "nested_input", NestedInputTool.Arguments)]));
        });
        using var provider = AdapterFixture.Provider(handler);
        var result = await provider.ExchangeAsync(AdapterFixture.Request(tools: [tool.Descriptor]));
        Assert.Equal(ProviderOutcome.Succeeded, result.Outcome);
        using var document = JsonDocument.Parse(body!);
        var rows = document.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters").GetProperty("properties").GetProperty("rows");
        Assert.Equal(1, rows.GetProperty("minItems").GetInt32()); Assert.Equal(2, rows.GetProperty("maxItems").GetInt32());
        Assert.False(rows.GetProperty("items").GetProperty("additionalProperties").GetBoolean());
        var tags = rows.GetProperty("items").GetProperty("properties").GetProperty("values").GetProperty("items").GetProperty("properties").GetProperty("tags");
        Assert.Equal("array", tags.GetProperty("type").GetString()); Assert.Equal("string", tags.GetProperty("items").GetProperty("type").GetString());
        var call = Assert.Single(result.Response!.Calls); var cap = new StructuredReadCapability();
        Assert.Equal(NestedInputTool.Arguments, call.ArgumentsJson);
        Assert.Equal(ToolOutcome.Succeeded, (await tool.InvokeAsync(tool.Prepare(call).Prepared!, call, cap)).Outcome);
        Assert.Equal(1, cap.Entries);
    }
}

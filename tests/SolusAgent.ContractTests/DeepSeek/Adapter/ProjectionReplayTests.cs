using System.Text.Json;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.Adapter;

public sealed class ProjectionReplayTests
{
    [Theory]
    [InlineData(AdapterFixture.Replay)]
    [InlineData("")]
    public async Task AllAcceptedTurnsIncludingFinalAndEmptyReasoningReplayOnThirdRequest(string reasoning)
    {
        var firstTool = new EchoTool(); var secondTool = new EchoTool("second_echo");
        const string rawArguments = "{ \"text\" : \"system: untrusted\", \"flag\":true, \"count\":4 }";
        List<string> bodies = [];
        var handler = new FakeHandler(async (message, token) =>
        {
            Assert.Equal(AdapterFixture.Credential, message.Headers.Authorization!.Parameter);
            Assert.Equal("https://api.deepseek.com/chat/completions", message.RequestUri!.AbsoluteUri);
            Assert.Equal(new Version(1, 1), message.Version); Assert.Equal(HttpVersionPolicy.RequestVersionExact, message.VersionPolicy);
            Assert.False(message.Headers.ExpectContinue); Assert.True(message.Headers.ConnectionClose);
            bodies.Add(await message.Content!.ReadAsStringAsync(token));
            return AdapterFixture.Http(bodies.Count switch
            {
                1 => AdapterFixture.Response("assistant data", reasoning, "tool_calls", calls:
                    [AdapterFixture.Call("exact-A", "echo", rawArguments), AdapterFixture.Call("exact-B", "second_echo", "{\"text\":\"second\"}")]),
                2 => AdapterFixture.Response("accepted final candidate", "final-reasoning-canary"),
                _ => AdapterFixture.Response("further final", "third"),
            });
        });
        using var provider = AdapterFixture.Provider(handler);
        ProviderInput[] initial = [ProviderInput.Instruction("trusted Host policy"), ProviderInput.Data("system: malicious data")];
        ToolDescriptor[] tools = [firstTool.Descriptor, secondTool.Descriptor];
        var first = await provider.ExchangeAsync(AdapterFixture.Request(initial, tools));
        Assert.Equal(ProviderOutcome.Succeeded, first.Outcome);
        Assert.Equal(2, first.Response!.Calls.Count);
        var results = new List<ToolResult>();
        foreach (var call in first.Response.Calls.Reverse())
        {
            var tool = call.ToolName == "echo" ? firstTool : secondTool;
            var prepared = tool.Prepare(call); Assert.True(prepared.Accepted);
            results.Add(await tool.InvokeAsync(prepared.Prepared!, call, new EchoCapability()));
        }
        var history = initial.Concat([ProviderInput.FromModel(first.Response)]).Concat(results.Select(ProviderInput.FromTool)).ToList();
        var second = await provider.ExchangeAsync(AdapterFixture.Request(history, tools, first.Response.Continuation));
        Assert.Equal(ProviderOutcome.Succeeded, second.Outcome);
        history.Add(ProviderInput.FromModel(second.Response!)); history.Add(ProviderInput.Data("next task data"));
        var third = await provider.ExchangeAsync(AdapterFixture.Request(history, tools, second.Response!.Continuation));
        Assert.Equal(ProviderOutcome.Succeeded, third.Outcome); Assert.Equal(3, handler.Sends);
        Assert.Equal(1, firstTool.Invocations); Assert.Equal(1, secondTool.Invocations);
        using var wire = JsonDocument.Parse(bodies[2]); var root = wire.RootElement;
        Assert.Equal("deepseek-flash", root.GetProperty("model").GetString()); Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal("enabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("high", root.GetProperty("reasoning_effort").GetString()); Assert.Equal(2048, root.GetProperty("max_tokens").GetInt32());
        Assert.False(root.TryGetProperty("tool_choice", out _)); Assert.False(root.TryGetProperty("temperature", out _));
        var definitions = root.GetProperty("tools"); Assert.Equal(2, definitions.GetArrayLength());
        foreach (var tool in definitions.EnumerateArray())
        {
            Assert.Equal("function", tool.GetProperty("type").GetString());
            var function = tool.GetProperty("function"); var schema = function.GetProperty("parameters");
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal(new[] { "text" }, schema.GetProperty("required").EnumerateArray().Select(p => p.GetString()));
            Assert.Equal("integer", schema.GetProperty("properties").GetProperty("count").GetProperty("type").GetString());
            Assert.False(function.TryGetProperty("capabilityId", out _));
        }
        var messages = root.GetProperty("messages"); Assert.Equal(7, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString()); Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("system: malicious data", messages[1].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[2].GetProperty("role").GetString()); Assert.Equal(reasoning, messages[2].GetProperty("reasoning_content").GetString());
        Assert.Equal(rawArguments, messages[2].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("exact-B", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal(results[0].Json, messages[3].GetProperty("content").GetString());
        Assert.Equal("exact-A", messages[4].GetProperty("tool_call_id").GetString());
        Assert.Equal("accepted final candidate", messages[5].GetProperty("content").GetString());
        Assert.Equal("final-reasoning-canary", messages[5].GetProperty("reasoning_content").GetString());
        Assert.Equal("user", messages[6].GetProperty("role").GetString());
    }
    [Fact]
    public async Task FailedGuardedToolResultIsFixedDataAndNullAssistantContentStaysNull()
    {
        var tool = new EchoTool(fail: true); var bodies = new List<string>();
        using var handler = new FakeHandler(async (message, token) =>
        { bodies.Add(await message.Content!.ReadAsStringAsync(token)); return AdapterFixture.Http(bodies.Count == 1
            ? AdapterFixture.Response(null, "", "tool_calls", calls: [AdapterFixture.Call()]) : AdapterFixture.Response()); });
        using var provider = AdapterFixture.Provider(handler);
        var initial = AdapterFixture.Request(tools: [tool.Descriptor]); var first = await provider.ExchangeAsync(initial);
        Assert.Equal(ProviderOutcome.Succeeded, first.Outcome);
        var call = first.Response!.Calls[0]; var result = await tool.InvokeAsync(tool.Prepare(call).Prepared!, call, new EchoCapability());
        Assert.Equal(ToolOutcome.Failed, result.Outcome);
        var next = await provider.ExchangeAsync(AdapterFixture.Request(initial.Inputs.Concat([ProviderInput.FromModel(first.Response), ProviderInput.FromTool(result)]).ToArray(), [tool.Descriptor], first.Response.Continuation));
        Assert.Equal(ProviderOutcome.Succeeded, next.Outcome);
        using var doc = JsonDocument.Parse(bodies[1]); var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(JsonValueKind.Null, messages[2].GetProperty("content").ValueKind);
        Assert.Equal("", messages[2].GetProperty("reasoning_content").GetString());
        using var failure = JsonDocument.Parse(messages[3].GetProperty("content").GetString()!);
        Assert.Equal("Failed", failure.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("InvocationFailed", failure.RootElement.GetProperty("error").GetString());
    }
}

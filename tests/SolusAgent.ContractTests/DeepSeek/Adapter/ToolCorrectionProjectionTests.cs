using System.Text;
using System.Text.Json.Nodes;
using SolusAgent.Providers.DeepSeek;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.Adapter;

public sealed class ToolCorrectionProjectionTests
{
    private const string Bad = "{\"text\":\"REJECTED_WIRE_CANARY\",\"unknown\":true}";
    private const string Good = "{ \"text\" : \"retained\" }";
    private const string Marker = "{\"_solus_rejected\":true}";
    private const string Invalid = "{\"status\":\"error\",\"code\":\"arguments_invalid\",\"retryable\":true}";
    private const string Withheld = "{\"status\":\"error\",\"code\":\"batch_not_executed\",\"retryable\":true}";

    [Fact]
    public async Task ActualParserAndWriterPreserveExactReplayAndFixedCompleteBatch()
    {
        var bodies = new List<string>();
        using var handler = new FakeHandler(async (message, token) =>
        {
            bodies.Add(await message.Content!.ReadAsStringAsync(token));
            return AdapterFixture.Http(bodies.Count == 1 ? AdapterFixture.Response("original text", "original replay", "tool_calls", calls:
                [AdapterFixture.Call("bad", args: Bad), AdapterFixture.Call("good", args: Good)]) : AdapterFixture.Response());
        });
        using var provider = AdapterFixture.Provider(handler);
        var initial = Request(); var source = await provider.ExchangeAsync(initial);
        Assert.Equal(ProviderOutcome.Succeeded, source.Outcome); Assert.Equal(ProviderFinish.ToolCorrection, source.Response!.Finish);
        var next = Next(initial, source.Response);
        Assert.Equal(ProviderError.None, provider.ValidateRequest(next)); Assert.Equal(1, handler.Sends);
        Assert.Equal(ProviderError.None, provider.ValidateRequest(next)); Assert.Equal(1, handler.Sends);
        Assert.Equal(SolusAgent.Api.Usage.DispatchExposure.Unknown, next.Observation.Snapshot().Exposure);
        var final = await provider.ExchangeAsync(next);
        Assert.Equal(ProviderOutcome.Succeeded, final.Outcome); Assert.Equal(2, handler.Sends);
        Assert.True(JsonNode.DeepEquals(Expected(), JsonNode.Parse(bodies[1])));
        Assert.DoesNotContain("REJECTED_WIRE_CANARY", bodies[1]); Assert.DoesNotContain(Bad, bodies[1]);
    }

    [Fact]
    public async Task ProspectiveEscapedWireUsesActualSerializerAtExactAndOneUnderWithoutDispatchOrSealing()
    {
        var escaped = new string('"', 2048);
        using var sourceHandler = FakeHandler.Reply(AdapterFixture.Response("original text", "original replay", "tool_calls", calls:
            [AdapterFixture.Call("bad", args: Bad), AdapterFixture.Call("good", args: Good)]));
        using var source = AdapterFixture.Provider(sourceHandler);
        var initial = Request(escaped); var model = (await source.ExchangeAsync(initial)).Response!;
        var next = Next(initial, model);
        var expected = Expected(escaped).ToJsonString(); var bytes = Encoding.UTF8.GetByteCount(expected);
        Assert.True(next.PayloadByteCount < bytes);
        var body = "";
        using var exactHandler = new FakeHandler(async (message, token) =>
        { body = await message.Content!.ReadAsStringAsync(token); return AdapterFixture.Http(AdapterFixture.Response()); });
        using var exact = AdapterFixture.Provider(exactHandler, requestCap: bytes);
        using var shortHandler = FakeHandler.Reply(AdapterFixture.Response());
        using var shortProvider = AdapterFixture.Provider(shortHandler, requestCap: bytes - 1);
        Assert.Equal(ProviderError.None, exact.ValidateRequest(next));
        Assert.Equal(ProviderError.LimitExceeded, shortProvider.ValidateRequest(next));
        Assert.Equal(0, exactHandler.Sends); Assert.Equal(0, shortHandler.Sends);
        Assert.Equal(SolusAgent.Api.Usage.DispatchExposure.Unknown, next.Observation.Snapshot().Exposure);
        Assert.Equal(ProviderOutcome.Succeeded, (await exact.ExchangeAsync(next)).Outcome);
        Assert.Equal(bytes, Encoding.UTF8.GetByteCount(body)); Assert.True(JsonNode.DeepEquals(Expected(escaped), JsonNode.Parse(body)));
        Assert.Equal(1, exactHandler.Sends); Assert.Equal(0, shortHandler.Sends);
    }

    [Fact]
    public async Task MissingMandatoryReplayIsRejectedByPureActualProjectionBeforeAnySend()
    {
        var initial = Request();
        var origin = new ProviderContinuation(initial.Scope, initial.Attempt, [1]);
        var valid = new ProviderResponse(initial.Scope, initial.Attempt, ProviderFinish.ToolCalls, "original text",
            [new("bad", "echo", "{\"text\":\"valid\"}"), new("good", "echo", Good)], origin);
        var accepted = ProviderResponse.RestoreToolCalls(initial, valid);
        var correction = ProviderResponse.WithholdToolArguments(initial, accepted, [new("bad", ToolArgumentCorrectionPolicy.ArgumentsInvalid)]);
        var noReplay = ProviderResponse.RestoreToolCorrection(initial, correction.Text, null, correction.Correction!.Origin,
            correction.Correction.Members, correction.Correction.OriginalResponseBytes - origin.ByteCount - Encoding.UTF8.GetByteCount(initial.Scope.Provider) - Encoding.UTF8.GetByteCount(initial.Scope.Model),
            correction.Correction.DefinitionFingerprint);
        using var handler = FakeHandler.Reply(AdapterFixture.Response()); using var provider = AdapterFixture.Provider(handler);
        var next = Next(initial, noReplay);
        Assert.Equal(ProviderError.ContinuationMismatch, provider.ValidateRequest(next)); Assert.Equal(0, handler.Sends);
    }

    private static ProviderRequest Request(string data = "untrusted data") => new(AdapterFixture.Scope,
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1), [ProviderInput.Instruction("Host rule"), ProviderInput.Data(data)],
        [AdapterFixture.Tool()], requiredCapabilities: AdapterFixture.All, toolArgumentCorrection: new("fixed"));
    private static ProviderRequest Next(ProviderRequest initial, ProviderResponse model) => new(initial.Scope,
        new(initial.Attempt.ExecutionId, Guid.NewGuid(), Guid.NewGuid(), 1), [.. initial.Inputs, ProviderInput.FromModel(model),
            ProviderInput.FromCorrectionFeedback(model, 0), ProviderInput.FromCorrectionFeedback(model, 1)], initial.Tools, model.Continuation,
        AdapterFixture.All, toolArgumentCorrection: initial.ToolArgumentCorrection);

    private static JsonObject Expected(string data = "untrusted data") => new()
    {
        ["model"] = "deepseek-flash", ["stream"] = false, ["reasoning_effort"] = "high",
        ["thinking"] = new JsonObject { ["type"] = "enabled" }, ["max_tokens"] = 2048,
        ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = "Host rule" },
            new JsonObject { ["role"] = "user", ["content"] = data },
            new JsonObject { ["role"] = "assistant", ["content"] = "original text", ["reasoning_content"] = "original replay",
                ["tool_calls"] = new JsonArray(Call("bad", Marker), Call("good", Good)) },
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "bad", ["content"] = Invalid },
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "good", ["content"] = Withheld }),
        ["tools"] = new JsonArray(new JsonObject { ["type"] = "function", ["function"] = new JsonObject
        { ["name"] = "echo", ["description"] = "Generic synthetic echo", ["parameters"] = JsonNode.Parse("""{"type":"object","properties":{"text":{"type":"string"},"flag":{"type":"boolean"},"count":{"type":"integer"}},"required":["text"],"additionalProperties":false}""") } }),
    };
    private static JsonObject Call(string id, string arguments) => new()
    { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = "echo", ["arguments"] = arguments } };
}

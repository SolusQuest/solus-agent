using System.Net;
using System.Text.Json;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.ContractTests.DeepSeek.Adapter;
using SolusAgent.Providers.DeepSeek;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.RuntimeIntegration;

internal static class IntegrationFixture
{
    internal const string Instruction = "integration-host-instruction-canary";
    internal const string InputData = "integration-input-data-canary";
    internal const string CorrectionA = "integration-host-correction-A-canary";
    internal const string CorrectionB = "integration-host-correction-B-canary";
    internal const string CounterArguments = "{\"amount\":2}";
    internal const string CounterArgumentsSecond = "{\"amount\":3}";
    internal const string TransformArguments = "{ \"text\" : \"transform-canary\" }";
    internal const string TransformRepairArguments = "{\"text\":\"repair-transform-canary\"}";

    // The DeepSeek tool profile requires explicit continuation support from the first request.
    internal static RuntimeOptions Options(TimeProvider? clock = null) => new(clock, requireContinuation: true);

    internal static CandidateExecutionRequest Request(int units = 8, int submissions = 8, int repairs = 8,
        int continuations = 8, TimeSpan? duration = null, Guid? executionId = null) =>
        new(new AgentRequest(executionId ?? Guid.NewGuid(), Instruction, [new AgentInput(AgentInputSource.Repository, InputData)],
            new AgentExecutionBounds(units, duration ?? TimeSpan.FromSeconds(10)), AgentCapability.None),
            new CandidateExecutionBounds(submissions, repairs, continuations));

    internal static object Usage(int turn) => new
    { prompt_tokens = 100 + turn, completion_tokens = 10 + turn, total_tokens = 110 + 2 * turn };

    internal static string ResponseWithoutUsage(string text, string reasoning, string finish = "stop") =>
        JsonSerializer.Serialize(new
        {
            id = "synthetic-response", @object = "chat.completion", model = DeepSeekOptions.Model,
            choices = new[] { new { index = 0, finish_reason = finish, message = new { role = "assistant", content = text, reasoning_content = reasoning, tool_calls = (object[]?)null } } },
        });

    internal static void AssertTransport(HttpRequestMessage message)
    {
        Assert.Equal(AdapterFixture.Credential, message.Headers.Authorization!.Parameter);
        Assert.Equal("https://api.deepseek.com/chat/completions", message.RequestUri!.AbsoluteUri);
    }

    internal static void AssertControlFields(JsonElement root)
    {
        Assert.Equal(DeepSeekOptions.Model, root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal("enabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("high", root.GetProperty("reasoning_effort").GetString());
        Assert.Equal(2048, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(new[] { "counter", "transform" }, root.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("function").GetProperty("name").GetString()).ToArray());
    }

    internal static JsonElement Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}

/// <summary>A transform-shaped narrow capability whose admission consultation is observable.</summary>
internal sealed class ObservedTransformCapability : IToolCapability
{
    private int reads;
    internal int Reads => Volatile.Read(ref reads);
    public string CapabilityId { get { Interlocked.Increment(ref reads); return "text_transform"; } }
}

/// <summary>Non-cooperative response body optionally held at the read boundary, signalling entry and drained completion.</summary>
internal sealed class HeldContent(byte[] body, TaskCompletionSource completed, TaskCompletionSource? held = null, TaskCompletionSource? release = null) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteAsync(stream);
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => WriteAsync(stream);
    private async Task WriteAsync(Stream stream)
    {
        held?.TrySetResult();
        if (release is not null) await release.Task.ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
        completed.TrySetResult();
    }
    protected override bool TryComputeLength(out long length) { length = body.Length; return true; }
}

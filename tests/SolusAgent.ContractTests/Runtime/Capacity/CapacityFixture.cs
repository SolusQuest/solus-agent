using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CustomTools;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Capacity;

internal static class CapacityFixture
{
    internal const int MiB = 1024 * 1024;
    internal static ProviderExchangeBounds Bounds => new(maximumInputs: 2048, maximumRequestBytes: 128 * MiB,
        maximumResponseBytes: 8 * MiB, maximumContinuationBytes: 2 * MiB, maximumTextBytes: 32 * MiB);
    internal static RuntimeOptions Options(bool continuation = false, TimeProvider? clock = null) =>
        new(clock, maximumAttempts: 128, maximumRecords: 2048, maximumRetainedBytes: 256 * MiB, requireContinuation: continuation);
    internal static AgentRequest Request(int work = 2, IReadOnlyList<AgentInput>? data = null, AgentUsageLimits? usage = null) =>
        new(Guid.NewGuid(), "Synthetic current Host policy.", data ?? [], new(work, TimeSpan.FromMinutes(1)), AgentCapability.None, usage);
    internal static string Envelope(string artifact) => JsonSerializer.Serialize(new { text = artifact });
    internal static string Artifact(int bytes, string token = "a")
    {
        const string prefix = "{\"value\":\"";
        const string suffix = "\"}";
        var encoded = token is "\"" or "\\" or "\0" ? JsonSerializer.Serialize(token)[1..^1] : token;
        var available = bytes - Encoding.UTF8.GetByteCount(prefix + suffix);
        var count = available / Encoding.UTF8.GetByteCount(encoded);
        var result = prefix + string.Concat(Enumerable.Repeat(encoded, count)) + suffix;
        return result + new string(' ', bytes - Encoding.UTF8.GetByteCount(result));
    }
    internal static TransformTool Tool(string result, int resultLimit = 8 * MiB, int argumentLimit = 8 * MiB) =>
        new(maximumResultBytes: resultLimit, maximumArgumentBytes: argumentLimit, effect: (call, capability, token) =>
        { capability.Upper("effect", token); return ValueTask.FromResult(ToolOutput.Success(call, result)); });
    internal static ToolCall Call(string id = "capacity-call", string? arguments = null) => new(id, "transform", arguments ?? Envelope("{}"));
    internal static SolusAgent.Api.Execution.IAgent Agent(IModelProvider provider, IReadOnlyList<RuntimeToolRegistration>? tools = null,
        ProviderExchangeBounds? bounds = null, RuntimeOptions? options = null) =>
        RuntimeAgentFactory.Create(new(provider, tools ?? [], new RuntimeHooks(), bounds ?? Bounds), options ?? Options());
    internal static ValueTask<ProviderResponse> Final(ProviderRequest request, ProviderObservation observation, CancellationToken token)
    { observation.CaptureUsage(new(3, 2)); return ValueTask.FromResult(new ProviderResponse(request.Scope, request.Attempt, ProviderFinish.Final, "complete", [])); }
    internal static void Same(string expected, string actual)
    {
        Assert.True(StringComparer.Ordinal.Equals(expected, actual), "Complete synthetic content differed.");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expected))),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actual))));
    }
    internal static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);
    internal static int CallBytes(ToolCall call) => Bytes(call.CallId) + Bytes(call.ToolName) + Bytes(call.ArgumentsJson);
    internal static string LogicalRequest()
    {
        var blocks = Enumerable.Repeat(new string('x', 4 * MiB), 8).ToArray();
        var metadata = new string('m', 4096);
        var overhead = JsonSerializer.SerializeToUtf8Bytes(new { blocks = new string[8].Select(_ => "").ToArray(), metadata }).Length;
        blocks[^1] = new string('x', 4 * MiB - overhead);
        return JsonSerializer.Serialize(new { blocks, metadata });
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using SolusAgent.Providers.DeepSeek;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;

namespace SolusAgent.ContractTests.DeepSeek.Adapter;

internal static class AdapterFixture
{
    internal const string Credential = "synthetic-credential-canary";
    internal const string Replay = "synthetic-restricted-replay-canary";
    internal static readonly ProviderScope Scope = new("deepseek", DeepSeekOptions.Model);
    internal static readonly Guid Execution = Guid.NewGuid();
    internal const ProviderCapabilities All = ProviderCapabilities.ToolCalls | ProviderCapabilities.Continuation | ProviderCapabilities.UsageReporting;
    internal static ProviderRequest Request(IReadOnlyList<ProviderInput>? inputs = null, IReadOnlyList<ToolDescriptor>? tools = null,
        ProviderContinuation? continuation = null, ProviderCapabilities capabilities = All, ProviderExchangeBounds? bounds = null) =>
        new(Scope, new(Execution, Guid.NewGuid(), Guid.NewGuid()), inputs ?? [ProviderInput.Instruction("Host rule"), ProviderInput.Data("untrusted data")], tools, continuation, capabilities, bounds);
    internal static DeepSeekProvider Provider(HttpMessageHandler handler, int requestCap = 1048576, int responseCap = 524288, TimeSpan? timeout = null) =>
        new(new(Credential, 2048, maximumRequestBodyBytes: requestCap, maximumResponseBodyBytes: responseCap, timeout: timeout), handler);
    internal static ToolDescriptor Tool(string name = "echo") => new(name, "Generic synthetic echo",
        ToolSchema.Parse("""{"type":"object","properties":{"text":{"type":"string"},"flag":{"type":"boolean"},"count":{"type":"integer"}},"required":["text"],"additionalProperties":false}"""),
        ToolSchema.Parse("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"],"additionalProperties":false}"""),
        "echo_access", ToolEffect.ReadOnly);
    internal static string Response(string? text = "final", string? reasoning = Replay, string finish = "stop", string model = DeepSeekOptions.Model,
        object[]? calls = null, object? usage = null) => JsonSerializer.Serialize(new
        {
            id = "synthetic-response", @object = "chat.completion", model,
            choices = new[] { new { index = 0, finish_reason = finish, message = new { role = "assistant", content = text, reasoning_content = reasoning, tool_calls = calls } } },
            usage = usage ?? new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 },
        });
    internal static object Call(string id = "call-1", string tool = "echo", string args = "{\"text\":\"data\"}") =>
        new { id, type = "function", function = new { name = tool, arguments = args } };
    internal static HttpResponseMessage Http(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

internal sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    private int sends;
    internal int Sends => Volatile.Read(ref sends);
    internal bool Disposed { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    { Interlocked.Increment(ref sends); return send(request, token); }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    internal static FakeHandler Reply(string body, HttpStatusCode status = HttpStatusCode.OK) => new((_, _) => Task.FromResult(AdapterFixture.Http(body, status)));
}

internal sealed class EchoCapability : IToolCapability
{ public string CapabilityId => "echo_access"; }
internal sealed class EchoTool(string name = "echo", bool fail = false) : FunctionTool<EchoCapability>(AdapterFixture.Tool(name))
{
    internal int Invocations { get; private set; }
    protected override ValueTask<ToolOutput> InvokeCoreAsync(ToolCall call, EchoCapability capability, CancellationToken token)
    {
        Invocations++;
        using var document = JsonDocument.Parse(call.ArgumentsJson);
        return ValueTask.FromResult(fail ? ToolOutput.Failure(call) : ToolOutput.Success(call,
            JsonSerializer.Serialize(new { text = document.RootElement.GetProperty("text").GetString() })));
    }
}

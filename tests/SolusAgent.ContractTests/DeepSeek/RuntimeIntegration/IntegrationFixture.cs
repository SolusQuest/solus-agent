using System.Net;
using System.Text.Json;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.ContractTests.DeepSeek.Adapter;
using SolusAgent.Providers.DeepSeek;
using SolusAgent.Runtime.Api.Exposure;
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
    internal static RuntimeOptions Options(TimeProvider? clock = null, int maximumAttempts = 64) =>
        new(clock, maximumAttempts, requireContinuation: true);

    internal static CandidateExecutionRequest Request(int units = 8, int submissions = 8, int repairs = 8,
        int continuations = 8, TimeSpan? duration = null, Guid? executionId = null) =>
        new(new AgentRequest(executionId ?? Guid.NewGuid(), Instruction, [new AgentInput(AgentInputSource.Repository, InputData)],
            new AgentExecutionBounds(units, duration ?? TimeSpan.FromSeconds(10)), AgentCapability.None),
            new CandidateExecutionBounds(submissions, repairs, continuations));

    internal static AgentRequest Execution(int units = 8) =>
        new(Guid.NewGuid(), Instruction, [new AgentInput(AgentInputSource.Repository, InputData)],
            new AgentExecutionBounds(units, TimeSpan.FromSeconds(10)), AgentCapability.None);

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
        Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
        Assert.Equal(AdapterFixture.Credential, message.Headers.Authorization.Parameter);
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

/// <summary>A genuinely wrong-typed narrow capability whose identifier still matches the transform descriptor.</summary>
internal sealed class ForeignTransformCapability : IToolCapability
{ public string CapabilityId => "text_transform"; }

/// <summary>
/// Fixture-only method observer that forwards every call, unchanged and without replay, to the real guarded
/// producer while counting the effect-free admission calls it actually receives.
/// </summary>
internal sealed class ObservedFunctionTool(IFunctionTool inner) : IFunctionTool
{
    private int preparations;
    private int admissions;
    private int invocations;
    private int lastPrepareError;
    internal int Preparations => Volatile.Read(ref preparations);
    internal int Admissions => Volatile.Read(ref admissions);
    internal int Invocations => Volatile.Read(ref invocations);
    internal ToolError LastPrepareError => (ToolError)Volatile.Read(ref lastPrepareError);
    public ToolDescriptor Descriptor => inner.Descriptor;
    public ToolPreparation Prepare(ToolCall call)
    {
        Interlocked.Increment(ref preparations);
        var preparation = inner.Prepare(call);
        Volatile.Write(ref lastPrepareError, (int)preparation.Error);
        return preparation;
    }
    public ToolError ValidateInvocation(PreparedToolInvocation prepared, ToolCall expectedCall, IToolCapability? capability)
    {
        Interlocked.Increment(ref admissions);
        return inner.ValidateInvocation(prepared, expectedCall, capability);
    }
    public ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation prepared, ToolCall expectedCall,
        IToolCapability? capability, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref invocations);
        return inner.InvokeAsync(prepared, expectedCall, capability, cancellationToken);
    }
}

/// <summary>Non-cooperative response body optionally held at the read boundary, signalling entry and drained delivery.</summary>
internal sealed class HeldContent(byte[] body, TaskCompletionSource drained, TaskCompletionSource? held = null, TaskCompletionSource? release = null) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteAsync(stream);
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => WriteAsync(stream);
    private async Task WriteAsync(Stream stream)
    {
        held?.TrySetResult();
        if (release is not null) await release.Task.ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
        drained.TrySetResult();
    }
    protected override bool TryComputeLength(out long length) { length = body.Length; return true; }
}

/// <summary>
/// Transparent fixture-only observer of the actual guarded provider invocation. It forwards the exact request,
/// observation and token exactly once to the real adapter and signals only when that invocation has returned.
/// </summary>
internal sealed class ObservedProvider(IModelProvider inner) : IModelProvider
{
    private readonly TaskCompletionSource returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Returned => returned;
    public ProviderScope Scope => inner.Scope;
    public ProviderCapabilities Capabilities => inner.Capabilities;
    public async ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken cancellationToken = default)
    {
        try { return await inner.ExchangeAsync(request, cancellationToken).ConfigureAwait(false); }
        finally { returned.TrySetResult(); }
    }
}

/// <summary>
/// Transparent fixture-only observer of the actual exposure and settlement hook invocations. It forwards to the
/// real hooks and signals only when each returned hook producer has completed.
/// </summary>
internal sealed class ObservedHooks(IRuntimeExposureHooks inner) : IRuntimeExposureHooks
{
    private readonly TaskCompletionSource beforeReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource afterReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource BeforeReturned => beforeReturned;
    internal TaskCompletionSource AfterReturned => afterReturned;
    public async ValueTask<ExposureAcknowledgement?> BeforeDispatchAsync(RuntimeExposure exposure, CancellationToken token)
    {
        try { return await inner.BeforeDispatchAsync(exposure, token).ConfigureAwait(false); }
        finally { beforeReturned.TrySetResult(); }
    }
    public async ValueTask<SettlementAcknowledgement?> AfterAttemptAsync(RuntimeSettlement settlement, CancellationToken token)
    {
        try { return await inner.AfterAttemptAsync(settlement, token).ConfigureAwait(false); }
        finally { afterReturned.TrySetResult(); }
    }
}

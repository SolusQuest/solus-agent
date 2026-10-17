using System.Text.Json;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Execution;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Tools;

internal sealed class SnapshotCapability(Action? observe = null) : IToolCapability
{
    public string CapabilityId => "snapshot_access";
    internal int Entries;
    internal void Read() { Interlocked.Increment(ref Entries); observe?.Invoke(); }
}

internal sealed class SnapshotTool(bool invalidResult = false, int maximumArgumentBytes = 2048) : FunctionTool<SnapshotCapability>(new("read_snapshot", "Synthetic fixed snapshot",
    ToolSchema.Parse("""{"type":"object","properties":{"path":{"type":"string"},"cursor":{"type":"string"}},"required":["path"],"additionalProperties":false}"""),
    ToolSchema.Parse("""{"type":"object","properties":{"read":{"type":"boolean"}},"required":["read"],"additionalProperties":false}"""),
    "snapshot_access", ToolEffect.ReadOnly, maximumArgumentBytes, 128))
{
    internal int DomainChecks;
    protected override bool ValidateArguments(JsonElement arguments)
    { Interlocked.Increment(ref DomainChecks); return arguments.GetProperty("path").GetString() != "unsafe"; }
    protected override ValueTask<ToolOutput> InvokeCoreAsync(ToolCall call, SnapshotCapability capability, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); capability.Read();
        return ValueTask.FromResult(ToolOutput.Success(call, invalidResult ? "{\"read\":\"invalid\"}" : "{\"read\":true}"));
    }
}

internal sealed class ArgumentCorrectionFixture
{
    internal const string Canary = "REJECTED_RUNTIME_CANARY";
    internal const string Good = "{ \"path\" : \"safe\" }";
    internal static readonly ProviderCapabilities Capabilities = DelegateProvider.All | ProviderCapabilities.ToolArgumentCorrection;
    internal readonly SnapshotCapability Capability;
    internal readonly SnapshotTool Tool;
    internal readonly RuntimeHooks Hooks = new();
    internal readonly List<ProviderRequest> Requests = [];
    internal readonly ScriptedProvider Provider;
    internal readonly ToolArgumentCorrectionPolicy Policy = new("snapshot_policy", [new("path_invalid", "path_invalid", [new("field", "path")]),
        new("membership_invalid", "membership_invalid", [new("domain", "current_files")]),
        new("cursor_invalid", "cursor_invalid", [new("action", "restart")])]);
    internal readonly string Mode;
    internal readonly bool Enabled;
    internal int Classifications;
    internal Action<ProviderRequest>? Validate;
    internal readonly string BadArguments;

    internal ArgumentCorrectionFixture(string mode = "schema", bool enabled = true, bool finalImmediately = false, Action? effect = null)
    {
        Mode = mode; Enabled = enabled; Capability = new(effect); Tool = new(mode == "result");
        BadArguments = mode switch
        {
            "json" => "not JSON " + Canary,
            "depth" => "{\"path\":[[[[[[[0]]]]]]]}",
            "path" or "fatal" or "unknown_code" or "throw" => "{\"path\":\"../" + Canary + "\"}",
            "membership" => "{\"path\":\"untracked\"}",
            "cursor" => "{\"path\":\"safe\",\"cursor\":\"stale\"}",
            "unsafe" => "{\"path\":\"unsafe\"}",
            "valid" => Good,
            "bytes" => "{\"path\":\"" + new string('x', 2048) + "\"}",
            "encoding" => "{\"path\":\"\\ud800\"}",
            _ => "{\"path\":0,\"rejected\":\"" + Canary + "\"}",
        };
        Provider = new([
            (r, o, _) =>
            {
                Requests.Add(r); o.CaptureUsage(new(3, 2));
                return ValueTask.FromResult(new ProviderResponse(r.Scope, r.Attempt, ProviderFinish.ToolCalls, "model data",
                    [new("bad", "read_snapshot", BadArguments), new("good", mode == "unknown_tool" ? "unknown" : "read_snapshot",
                        mode == "fatal" ? "{\"path\":\"unsafe\"}" : Good)], new(r.Scope, r.Attempt, [1, 42])));
            },
            (r, o, _) =>
            {
                Requests.Add(r);
                if (mode == "valid") return ScriptedProvider.Final(r, o, default);
                AssertCorrection(r);
                if (finalImmediately) return ScriptedProvider.Final(r, o, default);
                o.CaptureUsage(new(3, 2));
                return ValueTask.FromResult(new ProviderResponse(r.Scope, r.Attempt, ProviderFinish.ToolCalls, null,
                    [new("corrected_a", "read_snapshot", Good), new("corrected_b", "read_snapshot", Good)], new(r.Scope, r.Attempt, [1, 43])));
            },
            (r, o, _) =>
            {
                Requests.Add(r);
                Assert.Equal(new[] { "corrected_a", "corrected_b" }, r.Inputs.Where(i => i.ToolResult is not null).Select(i => i.ToolResult!.Call.CallId));
                Assert.All(r.Inputs.Where(i => i.ToolResult is not null), i => Assert.True(i.ToolResult!.InvocationStarted));
                return ScriptedProvider.Final(r, o, default);
            },
        ], capabilities: Capabilities);
    }

    internal SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration Configuration(bool wrongCapability = false) => new(new Validating(Provider, r => Validate?.Invoke(r)),
        [Binding(wrongCapability)], Hooks,
        toolArgumentCorrection: Enabled ? Policy : null);
    internal RuntimeToolRegistration Binding(bool wrongCapability = false, IReadOnlyList<string>? allowedErrors = null) => new(Tool, wrongCapability ? new OtherCapability() : Capability,
        argumentCorrection: Enabled ? new("snapshot_classifier", allowedErrors ?? ["path_invalid", "membership_invalid", "cursor_invalid"], Classify) : null);
    internal IAgent Agent(RuntimeOptions? options = null, bool wrongCapability = false) =>
        RuntimeAgentFactory.Create(Configuration(wrongCapability), options ?? new(requireContinuation: true));
    internal static AgentRequest Request(int work = 3, int admissions = 4, int invocations = 2, Guid? id = null) => new(id ?? Guid.NewGuid(), "host", [],
        new(work, TimeSpan.FromSeconds(10)), AgentCapability.ToolCallAdmissionLimit | AgentCapability.ToolInvocationLimit,
        new(maximumLogicalCalls: work, maximumPhysicalDispatches: work, maximumToolInvocations: invocations, maximumToolCallAdmissions: admissions));

    private ToolArgumentDisposition Classify(ToolCall call, IToolCapability capability)
    {
        Interlocked.Increment(ref Classifications);
        if (Mode == "throw") throw new InvalidOperationException(Canary);
        if (Mode == "unknown_code") return ToolArgumentDisposition.Correct("not_configured");
        using var document = JsonDocument.Parse(call.ArgumentsJson);
        var root = document.RootElement; var path = root.GetProperty("path").GetString()!;
        if (path.StartsWith("../", StringComparison.Ordinal)) return ToolArgumentDisposition.Correct("path_invalid");
        if (path == "untracked") return ToolArgumentDisposition.Correct("membership_invalid");
        if (root.TryGetProperty("cursor", out var cursor) && cursor.GetString() != "current") return ToolArgumentDisposition.Correct("cursor_invalid");
        return ToolArgumentDisposition.Allow();
    }

    internal static void AssertCorrection(ProviderRequest request)
    {
        var corrected = Assert.Single(request.Inputs, i => i.Model?.Finish == ProviderFinish.ToolCorrection).Model!;
        Assert.Empty(corrected.Calls);
        Assert.Equal(new[] { "bad", "good" }, corrected.Correction!.Members.Select(m => m.Call.CallId));
        Assert.Equal(request.ToolArgumentCorrection!.MarkerArgumentsJson, corrected.Correction.Members[0].Call.ArgumentsJson);
        Assert.Equal(Good, corrected.Correction.Members[1].Call.ArgumentsJson);
        Assert.Equal(new[] { "bad", "good" }, request.Inputs.Where(i => i.CorrectionFeedback is not null).Select(i => i.CorrectionFeedback!.Call.CallId));
        Assert.Equal(ToolArgumentCorrectionPolicy.BatchNotExecuted, request.Inputs.Last(i => i.CorrectionFeedback is not null).CorrectionFeedback!.ErrorCode);
        Assert.DoesNotContain(request.Inputs, i => i.ToolResult is not null);
    }

    private sealed class OtherCapability : IToolCapability { public string CapabilityId => "snapshot_access"; }
    private sealed class Validating(ScriptedProvider inner, Action<ProviderRequest> validation) : IModelProvider, IProviderRequestValidation
    {
        public ProviderScope Scope => inner.Scope;
        public ProviderCapabilities Capabilities => inner.Capabilities;
        public ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken token = default) => inner.ExchangeAsync(request, token);
        public ProviderError ValidateRequest(ProviderRequest request) { validation(request); return inner.ValidateRequest(request); }
    }
}

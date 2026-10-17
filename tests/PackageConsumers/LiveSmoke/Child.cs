using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Providers.DeepSeek;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;

namespace PackageConsumers.LiveSmoke;

internal static class Child
{
    internal static async Task<int> RunAsync()
    {
        var boot = Pipe.Parse<Bootstrap>(await Pipe.ReadAsync(Console.In, CancellationToken.None));
        if (boot.Crash) throw new InvalidOperationException(Fixture.PrivateError + Fixture.CredentialA + Fixture.Replay);
        if (boot.MalformedFrame)
        {
            await Pipe.WriteAsync(Console.Out, new Frame("unrecognized", Body: Fixture.CredentialA + Fixture.Replay), CancellationToken.None);
            return 0;
        }
        var wire = new WireObserver(boot.ObserverFailure);
        var rpc = new Rpc(); var counter = new Counter(rpc, boot.Target);
        var tool = new GuardedTool(counter, boot.ToolAllowance > 0, boot.FailTool);
        using var provider = new DeepSeekProvider(new(boot.Fresh ? Fixture.CredentialA : Fixture.CredentialB,
            boot.MaximumTokens, maximumRequestBodyBytes: 65536, maximumResponseBodyBytes: 65536, timeout: TimeSpan.FromSeconds(120)), new RpcHandler(rpc, boot.WireFault, wire));
        var observed = new ObservedProvider(provider, boot.Fresh, wire);
        var authority = new Authority(rpc, boot.Target);
        var agent = (IContextAgent)RuntimeAgentFactory.Create(new(observed, [new(tool, counter)], new Hooks(),
            new(maximumInputs: 32, maximumTools: 1, maximumToolCalls: 3, maximumRequestBytes: 65536,
                maximumResponseBytes: 65536, maximumContinuationBytes: 8192), contextAuthority: authority),
            new(maximumAttempts: 6, maximumRecords: 32, maximumRetainedBytes: 196608, requireContinuation: true));
        var request = new AgentRequest(boot.Execution, Fixture.Instruction,
            boot.InputCharacters == 0 ? [] : [new(AgentInputSource.Repository, new string('x', boot.InputCharacters))], new(1, TimeSpan.FromMinutes(5)),
            AgentCapability.WorkUnitLimit | AgentCapability.DurationLimit | AgentCapability.Cancellation | AgentCapability.DispatchLimits
                | AgentCapability.ToolInvocationLimit | AgentCapability.UsageAccounting | AgentCapability.UsageReporting,
            new(maximumLogicalCalls: 6, maximumPhysicalDispatches: 6, maximumToolInvocations: Math.Max(1, boot.ToolAllowance),
                accountingPolicy: new(new(16384, 4096), 98304, 24576)));
        var capture = new Sink(); var progress = new Progress();
        var execution = boot.Fresh ? new ContextExecutionRequest(request, ContextExecutionIntent.Fresh)
            : new(request, boot.CompletedSource ? ContextExecutionIntent.NewRunFromContext : ContextExecutionIntent.ContinueRun,
                boot.Source!.Envelope(), boot.Grant);
        var result = await agent.ExecuteWithContextAsync(execution, capture, progress);
        Snapshot? snapshot = result.Checkpoint is { } checkpoint && capture.Value is { } context
            ? Snapshot.From(context, checkpoint) : null;
        if (snapshot is not null)
        {
            var restricted = Encoding.UTF8.GetString(snapshot.Payload);
            Check.That(!restricted.Contains(Fixture.CredentialA, StringComparison.Ordinal)
                && !restricted.Contains(Fixture.CredentialB, StringComparison.Ordinal), Failure.Confinement);
        }
        var usage = result.Outcome?.Usage;
        var report = new Report(Environment.ProcessId, result.Admission, result.Outcome?.Reason, result.CaptureStatus,
            observed.Calls, tool.Entries, observed.Imports, authority.Claims,
            result.History.Sum(round => round.Usage.ToolInvocations?.Invoked ?? 0), result.History.Sum(round => round.Usage.Attempts.Count),
            usage?.InputTokens.ObservedTokens, usage?.OutputTokens.ObservedTokens,
            usage?.Attempts.Count ?? 0, usage?.ToolInvocations?.Invoked ?? 0,
            result.History.All(round => round.Usage.InputTokens.ObservedTokens.HasValue)
                ? result.History.Sum(round => round.Usage.InputTokens.ObservedTokens!.Value) : null,
            result.History.All(round => round.Usage.OutputTokens.ObservedTokens.HasValue)
                ? result.History.Sum(round => round.Usage.OutputTokens.ObservedTokens!.Value) : null,
            observed.ReplyMatches,
            Fixture.Confined(JsonSerializer.Serialize(result) + JsonSerializer.Serialize(progress.Items) + JsonSerializer.Serialize(observed.Diagnostics)),
            observed.Diagnostics.LastOrDefault()?.Error, snapshot, wire.Value);
        // The only stdout writer is the restricted IPC protocol; the parent never forwards it.
        await Pipe.WriteAsync(Console.Out, new Frame("report", Report: report), CancellationToken.None);
        return 0;
    }

    private sealed class Sink : IRestrictedContextSink
    {
        internal AgentContextEnvelope? Value;
        public void Capture(AgentContextEnvelope context) => Value = new(context.ImplementationId, context.FormatVersion,
            context.CompatibilityVersion, context.CopyRestrictedPayload());
    }
    private sealed class Progress : IProgress<AgentProgress>
    {
        internal List<AgentProgress> Items { get; } = [];
        public void Report(AgentProgress value) => Items.Add(value);
    }
    private sealed class Hooks : IRuntimeExposureHooks
    {
        public ValueTask<ExposureAcknowledgement?> BeforeDispatchAsync(RuntimeExposure exposure, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ExposureAcknowledgement?>(new(exposure, RuntimeHookStatus.Acknowledged,
                ExposureDecision.Permit, exposure.RequiredAcknowledgement));
        }
        public ValueTask<SettlementAcknowledgement?> AfterAttemptAsync(RuntimeSettlement settlement, CancellationToken token) =>
            ValueTask.FromResult<SettlementAcknowledgement?>(new(settlement.Exposure, RuntimeHookStatus.Acknowledged,
                RuntimeContinuation.Continue, settlement.Accounting));
    }
    private sealed class Authority(Rpc rpc, Guid target) : IRuntimeContextAuthority
    {
        internal int Claims;
        public bool TryClaim(AgentContextEnvelope context, ContextRoundGrant grant)
        {
            var allowed = rpc.Ask(new("claim", Source: Snapshot.From(context, grant.Source), Grant: grant, Target: target)).Allowed;
            if (allowed) Claims++;
            return allowed;
        }
    }
    private sealed class ObservedProvider(DeepSeekProvider inner, bool fresh, WireObserver wire) : IModelProvider, IProviderContextPersistence
    {
        internal int Calls, Imports;
        internal bool ReplyMatches = true;
        internal List<ProviderDiagnostic> Diagnostics { get; } = [];
        public ProviderScope Scope => inner.Scope;
        public ProviderCapabilities Capabilities => inner.Capabilities;
        public async ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken token = default)
        {
            Calls++;
            wire.Begin(request);
            ProviderExchangeResult value;
            try { value = await inner.ExchangeAsync(request, token); }
            finally { wire.End(); }
            Diagnostics.Add(value.Diagnostic);
            if (value.Response is { } response)
            {
                var expected = fresh ? response.Finish == ProviderFinish.ToolCalls && response.Calls.Count == 1
                    && response.Calls[0].ToolName == "smoke_increment" && IsEmptyObject(response.Calls[0].ArgumentsJson)
                    : response.Finish == ProviderFinish.Final && response.Text == Fixture.Final && response.Calls.Count == 0;
                ReplyMatches &= expected;
                if (!expected) throw new SmokeFailure(Failure.UnexpectedReply);
            }
            return value;
        }
        private static bool IsEmptyObject(string arguments)
        {
            try
            {
                using var value = JsonDocument.Parse(arguments);
                return value.RootElement.ValueKind == JsonValueKind.Object && !value.RootElement.EnumerateObject().Any();
            }
            catch (JsonException) { return false; }
        }
        public ProviderSavedState? ExportContext(ProviderContextBinding binding) => inner.ExportContext(binding);
        public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state) { Imports++; return inner.AdmitContext(binding, state); }
    }
    private sealed class Rpc
    {
        private readonly object gate = new();
        internal Frame Ask(Frame frame, CancellationToken token = default)
        {
            lock (gate)
            {
                Pipe.WriteAsync(Console.Out, frame, token).GetAwaiter().GetResult();
                var reply = Pipe.Parse<Frame>(Pipe.ReadAsync(Console.In, token).GetAwaiter().GetResult());
                Check.That(reply.Kind == "reply", Failure.Protocol);
                return reply;
            }
        }
    }
    private sealed class RpcHandler(Rpc rpc, WireFault fault, WireObserver wire) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check.That(request.RequestUri?.AbsoluteUri == DeepSeekOptions.OfficialEndpoint && request.Method == HttpMethod.Post);
            var body = await request.Content!.ReadAsStringAsync(token);
            // Closed fault selection exists only on the trusted fixture bootstrap, never on the local CLI.
            if (fault == WireFault.Malformed) body = "{";
            else if (fault != WireFault.None)
            {
                var node = JsonNode.Parse(body)!;
                if (fault == WireFault.Profile) node["model"] = "synthetic-unselected-model";
                if (fault == WireFault.Input) node["messages"]![0]!["content"] = "synthetic-instruction-mismatch";
                if (fault == WireFault.History) node["messages"]![1]!["content"] = "synthetic-history-mismatch";
                body = node.ToJsonString();
            }
            var response = rpc.Ask(new("send", Body: body), token);
            wire.Observe(response.Body!);
            return new((HttpStatusCode)response.Status) { Content = new StringContent(response.Body!, Encoding.UTF8, "application/json") };
        }
    }
    private sealed class Counter(Rpc rpc, Guid target) : IToolCapability
    {
        public string CapabilityId => "smoke_counter";
        internal void Enter(string callId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check.That(rpc.Ask(new("tool", Body: callId, Target: target), token).Allowed, Failure.ToolLimit);
        }
        internal void Effect(string callId, CancellationToken token) =>
            Check.That(rpc.Ask(new("effect", Body: callId, Target: target), token).Allowed, Failure.Authority);
    }
    private sealed class Increment : FunctionTool<Counter>
    {
        private readonly bool fail;
        internal Increment(bool fail) : base(new("smoke_increment", "Increment the synthetic smoke counter once.",
            ToolSchema.Parse("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"),
            ToolSchema.Parse("{\"type\":\"object\",\"properties\":{\"done\":{\"type\":\"boolean\"}},\"required\":[\"done\"],\"additionalProperties\":false}"),
            "smoke_counter", ToolEffect.Mutating, 128, 128)) { this.fail = fail; }
        protected override ValueTask<ToolOutput> InvokeCoreAsync(ToolCall call, Counter capability, CancellationToken token)
        {
            if (fail) throw new InvalidOperationException(Fixture.PrivateError);
            capability.Effect(call.CallId, token);
            return ValueTask.FromResult(ToolOutput.Success(call, "{\"done\":true}"));
        }
    }
    private sealed class GuardedTool(Counter counter, bool allowed, bool fail) : IFunctionTool
    {
        private readonly Increment inner = new(fail);
        internal int Entries;
        public ToolDescriptor Descriptor => inner.Descriptor;
        public ToolPreparation Prepare(ToolCall call) => inner.Prepare(call);
        public ToolError ValidateCapability(IToolCapability? capability) =>
            allowed ? inner.ValidateCapability(capability) : ToolError.UnsupportedCapability;
        public ToolError ValidateInvocation(PreparedToolInvocation prepared, ToolCall expected, IToolCapability? capability) =>
            allowed ? inner.ValidateInvocation(prepared, expected, capability) : ToolError.UnsupportedCapability;
        public ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation prepared, ToolCall expected, IToolCapability? capability, CancellationToken token = default)
        {
            Check.That(ValidateInvocation(prepared, expected, capability) == ToolError.None, Failure.ToolLimit);
            Entries++; counter.Enter(expected.CallId, token); // Charge interface entry, including failure, before the effect.
            return inner.InvokeAsync(prepared, expected, capability, token);
        }
    }
}

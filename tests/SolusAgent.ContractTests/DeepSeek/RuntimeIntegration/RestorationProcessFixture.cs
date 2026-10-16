using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CustomTools;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ContractTests.DeepSeek.Adapter;
using SolusAgent.ContractTests.Runtime.Candidates;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Providers.DeepSeek;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.RuntimeIntegration;

internal sealed record RestorationObservation(int Pid, ContextAdmission Admission, ContextRejectionCode Rejection,
    CandidateStopReason? Stop, ContextCaptureStatus Capture, int Sends, int Exposures, int Settlements, int Imports,
    int AuthorityCalls, int Claims, int OrdinaryEntries, int OperationEntries, int QueryEntries, int CounterEffects,
    long CounterTotal, int RecoveryEffects, int Hosts, int HostEffects, int CurrentTools, int HistoricalTools,
    int CurrentModels, int HistoricalModels, int HistoricalRounds, int CurrentRepairs, int HistoricalRepairs,
    int CurrentReceipts, int HistoricalReceipts, long CurrentInput, long HistoricalInput, long CurrentOutput,
    long HistoricalOutput, long MeasuredInput, long HistoricalMeasuredInput, long ReservedInput, long UnresolvedInput,
    long InputAllowance, bool SettlementMatches, bool WireMatches, string? WireHash, Guid? WorkId, Guid? RoundId,
    Guid? ExecutionId, Guid? RejectedSubmission, Guid? RepairsSubmissionId, Guid[] AcceptedOrigins, Guid[] ModelOrigins,
    int ContinuedCalls, bool CredentialsConfined, bool CheckpointPrepared);

internal static class RestorationChild
{
    internal const string Instruction = "restoration-trusted-instruction";
    internal const string Data = "restoration-classified-input";
    internal const string InitialFinal = "restoration-rejected-candidate";
    internal const string FinalReasoning = "restoration-restricted-final-replay";
    internal const string ToolReasoning = "restoration-restricted-tool-replay";
    internal const string CredentialB = "restoration-rotated-synthetic-credential";
    internal const string CorrectionA = "restoration-correction-A";
    internal const string CorrectionB = "restoration-correction-B";
    internal const string FirstArguments = "{ \"amount\" : 7 }";
    internal const string FailedArguments = "{\"amount\":5}";
    internal const string LastArguments = "{\"amount\":1}";
    private sealed record TargetSelection(Guid Counter, string Recovery, ToolRecoveryGuarantee Guarantee);
    private sealed record CounterState(long Total, int Effects);
    private sealed record HostReceipt(Guid Execution, Guid Submission, string Correction);
    private sealed record Delivered(AgentContextEnvelopeMetadata Metadata, byte[] Bytes, ContextCheckpointInfo Info);
    private sealed record AgentContextEnvelopeMetadata(Guid Implementation, int Format, int Compatibility);

    internal static async Task<int> Run(string[] args)
    {
        if (args.Length != 3) return 2;
        var root = args[1]; var command = args[2]; Directory.CreateDirectory(root);
        var producing = command.StartsWith("produce", StringComparison.Ordinal);
        if (producing) File.WriteAllText(Path.Combine(root, "scenario.txt"), command);
        var scenario = File.ReadAllText(Path.Combine(root, "scenario.txt"));
        var correction = scenario.Contains("correction-b", StringComparison.Ordinal) ? CorrectionB : CorrectionA;
        var guarantee = scenario.Contains("repeat", StringComparison.Ordinal) ? ToolRecoveryGuarantee.ConcurrentSafeRepeat : ToolRecoveryGuarantee.QueryOutcome;
        var sourceDir = Path.Combine(root, command.StartsWith("third", StringComparison.Ordinal) ? "continued" : "source");
        var outputDir = Path.Combine(root, producing ? "source" : command.StartsWith("third", StringComparison.Ordinal) ? "third" : "continued");
        var selectedPath = Path.Combine(root, "selected-targets.json"); var counterPath = Path.Combine(root, "counter.json");
        var targets = producing ? new TargetSelection(Guid.NewGuid(), "DEEPSEEK_RECOVERY_TARGET", guarantee) : Read<TargetSelection>(selectedPath);
        if (producing) Write(selectedPath, targets);
        var actualTargets = command == "consume-target" ? targets with { Counter = Guid.NewGuid() } : targets;
        var retained = producing ? new CounterState(0, 0) : Read<CounterState>(counterPath);
        var counter = new CounterCapability(initialTotal: retained.Total, initialEffects: retained.Effects,
            observe: (total, effects) => Write(counterPath, new CounterState(total, effects)));
        var recovery = new RecoveryCounterCapability(actualTargets.Recovery, guarantee, storePath: Path.Combine(root, "recovery.json"))
        { FailAfterEffect = producing, Allowed = command != "consume-permission" };
        if (command == "consume-unresolved") recovery.QueryBehavior = RecoveryQueryBehavior.Unresolved;
        var counterTool = new EntryTool(new CounterTool(producing && scenario.Contains("unsafe", StringComparison.Ordinal)
            ? CounterMode.FailAfterEffect : CounterMode.Normal, maximumResultBytes: command == "consume-tool" ? 101 : 100));
        var recoveryTool = new EntryRecoveryTool(new RecoverableCounterTool());
        var recoveryBinding = new ToolRecoveryBinding("keyed_recovery", command == "consume-binding" ? "OTHER_TARGET" : targets.Recovery, guarantee);
        RuntimeToolRegistration[] tools = [new(counterTool, counter), new(recoveryTool, recovery, recoveryBinding)];
        var hooks = new RuntimeHooks(); using var cancellation = new CancellationTokenSource();
        var wires = new List<string>(); var wireMatches = true;
        ObservedPersistence? observed = null;
        using var handler = new FakeHandler(async (message, token) =>
        {
            Assert.Equal(producing ? AdapterFixture.Credential : CredentialB, message.Headers.Authorization!.Parameter);
            Assert.Equal("https://api.deepseek.com/chat/completions", message.RequestUri!.AbsoluteUri);
            var body = await message.Content!.ReadAsStringAsync(token); wires.Add(body);
            // Restricted test evidence; never stdout or recovery authority.
            File.WriteAllText(Path.Combine(root, command + $"-wire-{wires.Count}.json"), body);
            var phase = producing ? wires.Count - 1 : 2;
            wireMatches &= body == RestorationWire.Expected(phase, correction).ToJsonString();
            if (producing && wires.Count == 1)
                return AdapterFixture.Http(AdapterFixture.Response(InitialFinal, FinalReasoning, usage: IntegrationFixture.Usage(1)));
            if (producing)
            {
                if (scenario == "produce-export-failure") observed!.FailExport = true;
                var response = AdapterFixture.Response(null, ToolReasoning, "tool_calls", calls:
                    [AdapterFixture.Call("first", "counter", FirstArguments), AdapterFixture.Call("uncertain", "recover", FailedArguments),
                     AdapterFixture.Call("last", "counter", LastArguments)], usage: IntegrationFixture.Usage(2));
                return AdapterFixture.Http(scenario == "produce-index" ? AdapterFixture.WithCallIndices(response) : response);
            }
            return AdapterFixture.Http(AdapterFixture.Response("restoration-repaired-candidate", "restoration-new-final-replay", usage: IntegrationFixture.Usage(3)));
        });
        using var provider = new DeepSeekProvider(new(producing ? AdapterFixture.Credential : CredentialB, command == "consume-config" ? 2049 : 2048), handler);
        observed = new(provider);
        var authority = new TargetAuthority(sourceDir, selectedPath, actualTargets, command is "consume-stale" or "consume-storage", command == "consume-before" ? cancellation.Cancel : null);
        var agent = (IContextCandidateAgent)RuntimeAgentFactory.Create(new(observed, tools, hooks, contextAuthority: producing ? null : authority), new(requireContinuation: true));
        var hostEffectsPath = Path.Combine(root, "host-effects.txt"); var hostReceiptPath = Path.Combine(root, "host-rejection.json");
        ScriptedCandidateHost? host = null;
        host = new((submission, _) =>
        {
            if (producing)
            {
                Assert.Equal(InitialFinal, submission.Payload);
                Write(hostReceiptPath, new HostReceipt(submission.ExecutionId, submission.SubmissionId, correction));
                return CandidateFixture.Feedback(submission, CandidateDecision.Reject, CandidateContinuation.Continue, correction);
            }
            var receipt = Read<HostReceipt>(hostReceiptPath);
            Assert.Equal(receipt.Submission, submission.RepairsSubmissionId);
            Assert.Equal("restoration-repaired-candidate", submission.Payload);
            File.AppendAllText(hostEffectsPath, submission.SubmissionId + "\n");
            return command == "consume-unknown"
                ? ValueTask.FromResult<CandidateFeedback?>(new(submission.ExecutionId, submission.SubmissionId, CandidateAcknowledgement.Unknown))
                : CandidateFixture.Feedback(submission);
        });
        var sink = new RestrictedContextHost();
        var request = Request(producing); ContextExecutionRequest execution;
        if (producing) execution = new(request, ContextExecutionIntent.Fresh);
        else
        {
            AgentContextEnvelope envelope; ContextCheckpointInfo info;
            if (command == "consume-storage")
            {
                var delivered = Read<Delivered>(Path.Combine(root, "delivered.json"));
                envelope = new(delivered.Metadata.Implementation, delivered.Metadata.Format, delivered.Metadata.Compatibility, delivered.Bytes); info = delivered.Info;
            }
            else { envelope = FileContextAuthority.Read(sourceDir); info = FileContextAuthority.Selection(sourceDir).Info; }
            if (command.StartsWith("consume-corrupt", StringComparison.Ordinal) || command is "consume-truncated" or "consume-oversized"
                or "consume-provider" or "consume-model" or "consume-lineage" or "consume-correction" or "consume-witness")
            {
                envelope = Alter(envelope, command);
                // Trusted fixture corruption reaches the owning grammar/provider gate; not positive state injection.
                FileContextAuthority.Save(sourceDir, envelope, info);
            }
            var grant = new ContextRoundGrant(Guid.NewGuid(), info, request.ExecutionId, Guid.NewGuid());
            var grantPath = Path.Combine(root, "used-grant.json");
            if (command == "consume-replay")
            {
                grant = Read<ContextRoundGrant>(grantPath); request = Request(false, grant.ExecutionId);
            }
            else if (command == "consume") Write(grantPath, grant);
            execution = new(request, ContextExecutionIntent.ContinueRun, envelope, grant);
        }
        var progress = new SafeProgress(command == "consume-cut" ? cancellation.Cancel : null);
        IRestrictedContextSink capture = scenario is "produce-sink-before" or "produce-sink-after" ? new ThrowingSink(sink, scenario.EndsWith("after", StringComparison.Ordinal)) : sink;
        var result = await CandidateConsumer.RunWithContextAsync(agent, execution, new(producing ? 2 : 1, producing ? 1 : 0, 0), host, capture, progress, cancellation.Token);
        AgentContextEnvelope? captured = null;
        if (sink.CaptureCount != 0) captured = sink.CopyRestrictedContext();
        if (captured is not null && result.Context.Checkpoint is { } checkpoint)
        {
            var text = Encoding.UTF8.GetString(captured.CopyRestrictedPayload());
            Assert.DoesNotContain(AdapterFixture.Credential, text); Assert.DoesNotContain(CredentialB, text);
            if (scenario == "produce-index")
            {
                using var payload = JsonDocument.Parse(text);
                Assert.True(AssertNormalizedCalls(payload.RootElement) > 0);
            }
            if (scenario != "produce-storage-failure" && result.Context.CaptureStatus == ContextCaptureStatus.Delivered)
                FileContextAuthority.Save(outputDir, captured, checkpoint);
            else if (scenario == "produce-storage-failure")
                Write(Path.Combine(root, "delivered.json"), new Delivered(new(captured.ImplementationId, captured.FormatVersion, captured.CompatibilityVersion), captured.CopyRestrictedPayload(), checkpoint));
        }
        var outcome = result.Context.Outcome; var usage = outcome?.Usage; var candidates = result.Candidates;
        var historical = result.Context.History;
        var account = usage?.Accounting;
        var observation = new RestorationObservation(Environment.ProcessId, result.Context.Admission, result.Context.RejectionCode, candidates?.StopReason,
            result.Context.CaptureStatus, handler.Sends, hooks.Exposures.Count, hooks.Settlements.Count, observed.Imports, authority.Calls, authority.Claims,
            counterTool.Invocations + recoveryTool.Ordinary, recoveryTool.Operations, recoveryTool.Queries, counter.Effects, counter.Total, recovery.Effects,
            host.Submissions.Count, File.Exists(hostEffectsPath) ? File.ReadAllLines(hostEffectsPath).Length : 0,
            usage?.ToolInvocations?.Invoked ?? 0, historical.Sum(r => r.Usage.ToolInvocations?.Invoked ?? 0), outcome?.CompletedWorkUnits ?? 0,
            historical.Sum(r => r.CompletedWorkUnits), historical.Count, candidates?.RepairsAdmitted ?? 0,
            captured is null ? 0 : JsonNode.Parse(captured.CopyRestrictedPayload())!["Rounds"]!.AsArray().SkipLast(1).Sum(r => r!["Candidates"]!["Repairs"]!.GetValue<int>()),
            candidates?.Receipts.Count ?? 0, candidates?.HistoricalReceipts.Count ?? 0,
            usage?.InputTokens.ObservedTokens ?? 0, historical.Sum(r => r.Usage.InputTokens.ObservedTokens ?? 0),
            usage?.OutputTokens.ObservedTokens ?? 0, historical.Sum(r => r.Usage.OutputTokens.ObservedTokens ?? 0),
            account?.Input.MeasuredTokens ?? 0, historical.Sum(r => r.Usage.Accounting?.Input.MeasuredTokens ?? 0),
            account?.Input.ReservedTokens ?? 0, account?.Input.UnresolvedTokens ?? 0, account?.Policy.InputAllowance ?? 0,
            hooks.Settlements.Count == 0 || account is not null && hooks.Settlements[^1].Accounting!.Matches(account), wireMatches,
            wires.Count == 0 ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(wires[^1]))),
            result.Context.Checkpoint?.LogicalWorkId, result.Context.Checkpoint?.RoundId, outcome?.ExecutionId,
            File.Exists(hostReceiptPath) ? Read<HostReceipt>(hostReceiptPath).Submission : null, host.Submissions.LastOrDefault()?.RepairsSubmissionId,
            observed.Requests.LastOrDefault()?.History?.AcceptedOrigins.Select(a => a.PhysicalAttemptId).ToArray() ?? [],
            usage?.Attempts.Select(a => a.PhysicalAttemptId).ToArray() ?? [], usage?.ContinuedCalls.Count ?? 0,
            captured is null || !Encoding.UTF8.GetString(captured.CopyRestrictedPayload()).Contains(CredentialB, StringComparison.Ordinal), result.Context.Checkpoint is not null);
        var safe = JsonSerializer.Serialize(result) + JsonSerializer.Serialize(progress.Items) + JsonSerializer.Serialize(observation);
        foreach (var canary in new[] { FinalReasoning, ToolReasoning, InitialFinal, CorrectionA, CorrectionB, AdapterFixture.Credential, CredentialB })
            Assert.DoesNotContain(canary, safe);
        Write(Path.Combine(root, command + "-result.json"), observation);
        return 0;
    }

    private static AgentRequest Request(bool producing, Guid? id = null) => new(id ?? Guid.NewGuid(), Instruction,
        producing ? [new(AgentInputSource.Repository, Data)] : [], new(producing ? 2 : 1, TimeSpan.FromSeconds(30)), AgentCapability.None,
        new(maximumLogicalCalls: producing ? 2 : 1, maximumPhysicalDispatches: producing ? 2 : 1, maximumToolInvocations: producing ? 3 : 2,
            accountingPolicy: new(new(512, 64), producing ? 700 : 512, producing ? 128 : 64)));

    private static int AssertNormalizedCalls(JsonElement value)
    {
        var count = 0;
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("CallId", out _) && value.TryGetProperty("ToolName", out _) && value.TryGetProperty("ArgumentsJson", out _))
            {
                Assert.Equal(new[] { "ArgumentsJson", "CallId", "ToolName" }, value.EnumerateObject().Select(p => p.Name).OrderBy(name => name, StringComparer.Ordinal));
                count++;
            }
            foreach (var property in value.EnumerateObject()) count += AssertNormalizedCalls(property.Value);
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) count += AssertNormalizedCalls(item);
        return count;
    }

    private static AgentContextEnvelope Alter(AgentContextEnvelope envelope, string command)
    {
        var bytes = envelope.CopyRestrictedPayload();
        if (command == "consume-corrupt") bytes = [0x7b, 0x21, 0x7d];
        else if (command == "consume-truncated") bytes = bytes[..(bytes.Length / 2)];
        else if (command == "consume-oversized") bytes = new byte[2 * 1024 * 1024 + 1];
        else
        {
            var node = JsonNode.Parse(bytes)!;
            switch (command)
            {
                case "consume-provider": node["Scope"]!["Provider"] = "wrong-provider"; break;
                case "consume-model": node["Scope"]!["Model"] = "wrong-model"; break;
                case "consume-lineage": node["Members"]![1]!["ModelAttempt"]!["PhysicalAttemptId"] = Guid.NewGuid(); break;
                case "consume-correction": node["Candidates"]!["Episodes"]![1]!["CorrectionIndex"] = 0; break;
                case "consume-witness":
                    var witness = Convert.FromBase64String(node["Provider"]!["Bytes"]!.GetValue<string>()); witness[^1] ^= 1;
                    node["Provider"]!["Bytes"] = Convert.ToBase64String(witness); break;
            }
            bytes = JsonSerializer.SerializeToUtf8Bytes(node);
        }
        return new(envelope.ImplementationId, envelope.FormatVersion, envelope.CompatibilityVersion, bytes);
    }

    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path))!;
    private static void Write<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value));

    private sealed class TargetAuthority(string directory, string selected, TargetSelection actual, bool deny, Action? after) : IRuntimeContextAuthority
    {
        internal int Calls { get; private set; }
        internal int Claims { get; private set; }
        public bool TryClaim(AgentContextEnvelope context, ContextRoundGrant grant)
        {
            Calls++;
            if (deny || Read<TargetSelection>(selected) != actual || !new FileContextAuthority(directory).TryClaim(context, grant)) return false;
            Claims++; after?.Invoke(); return true;
        }
    }
    private sealed class ObservedPersistence(DeepSeekProvider inner) : IModelProvider, IProviderContextPersistence
    {
        internal bool FailExport { get; set; }
        internal int Imports { get; private set; }
        internal List<ProviderRequest> Requests { get; } = [];
        public ProviderScope Scope => inner.Scope;
        public ProviderCapabilities Capabilities => inner.Capabilities;
        public ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken token = default)
        { Requests.Add(request); return inner.ExchangeAsync(request, token); }
        public ProviderSavedState? ExportContext(ProviderContextBinding binding) => FailExport
            ? throw new InvalidOperationException("EXPORT_FAILURE_CANARY") : inner.ExportContext(binding);
        public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state) { Imports++; return inner.AdmitContext(binding, state); }
    }
    private sealed class EntryTool(IFunctionTool inner) : IFunctionTool
    {
        internal int Invocations { get; private set; }
        public ToolDescriptor Descriptor => inner.Descriptor;
        public ToolPreparation Prepare(ToolCall call) => inner.Prepare(call);
        public ToolError ValidateInvocation(PreparedToolInvocation p, ToolCall c, IToolCapability? cap) => inner.ValidateInvocation(p, c, cap);
        public ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation p, ToolCall c, IToolCapability? cap, CancellationToken token = default)
        { Invocations++; return inner.InvokeAsync(p, c, cap, token); }
    }
    private sealed class EntryRecoveryTool(IRecoverableFunctionTool inner) : IRecoverableFunctionTool
    {
        internal int Ordinary { get; private set; }
        internal int Operations { get; private set; }
        internal int Queries { get; private set; }
        public ToolDescriptor Descriptor => inner.Descriptor;
        public ToolPreparation Prepare(ToolCall call) => inner.Prepare(call);
        public ToolError ValidateInvocation(PreparedToolInvocation p, ToolCall c, IToolCapability? cap) => inner.ValidateInvocation(p, c, cap);
        public ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation p, ToolCall c, IToolCapability? cap, CancellationToken token = default)
        { Ordinary++; return inner.InvokeAsync(p, c, cap, token); }
        public ToolError ValidateOperation(ToolOperation op, IToolCapability? cap) => inner.ValidateOperation(op, cap);
        public ValueTask<ToolResult> InvokeOperationAsync(PreparedToolInvocation p, ToolOperation op, IToolCapability? cap, CancellationToken token = default)
        { Operations++; return inner.InvokeOperationAsync(p, op, cap, token); }
        public ValueTask<ToolRecoveryReceipt> QueryOutcomeAsync(ToolOperation op, IToolCapability? cap, CancellationToken token = default)
        { Queries++; return inner.QueryOutcomeAsync(op, cap, token); }
    }
    private sealed class SafeProgress(Action? cut) : IProgress<AgentProgress>
    {
        internal List<AgentProgress> Items { get; } = [];
        public void Report(AgentProgress progress) { Items.Add(progress); if (progress.Usage?.ToolInvocations?.Invoked > 0) cut?.Invoke(); }
    }
    private sealed class ThrowingSink(RestrictedContextHost retained, bool after) : IRestrictedContextSink
    { public void Capture(AgentContextEnvelope context) { if (after) retained.Capture(context); throw new InvalidOperationException("STORAGE_FAILURE_CANARY"); } }
}

internal static class RestorationWire
{
    // Literal fixture contracts and independent JsonNode construction, never restored records or the production writer.
    internal static JsonObject Expected(int phase, string correction)
    {
        var root = new JsonObject { ["model"] = "deepseek-flash", ["stream"] = false, ["reasoning_effort"] = "high",
            ["thinking"] = new JsonObject { ["type"] = "enabled" }, ["max_tokens"] = 2048 };
        var messages = new JsonArray(Message("system", RestorationChild.Instruction), Message("user", RestorationChild.Data));
        if (phase >= 1)
        {
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = RestorationChild.InitialFinal, ["reasoning_content"] = RestorationChild.FinalReasoning });
            messages.Add(Message("user", correction));
        }
        if (phase >= 2)
        {
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = null, ["reasoning_content"] = RestorationChild.ToolReasoning,
                ["tool_calls"] = new JsonArray(Call("first", "counter", RestorationChild.FirstArguments), Call("uncertain", "recover", RestorationChild.FailedArguments), Call("last", "counter", RestorationChild.LastArguments)) });
            messages.Add(Result("first", "{\"total\":7}")); messages.Add(Result("uncertain", "{\"total\":5}")); messages.Add(Result("last", "{\"total\":8}"));
        }
        root["messages"] = messages;
        const string schema = "{\"type\":\"object\",\"properties\":{\"amount\":{\"type\":\"integer\"}},\"required\":[\"amount\"],\"additionalProperties\":false}";
        root["tools"] = new JsonArray(Tool("counter", "Synthetic counter", schema), Tool("recover", "Synthetic keyed recovery", schema));
        return root;
    }
    private static JsonObject Message(string role, string text) => new() { ["role"] = role, ["content"] = text };
    private static JsonObject Call(string id, string name, string arguments) => new() { ["id"] = id, ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments } };
    private static JsonObject Result(string id, string json) => new() { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = json };
    private static JsonObject Tool(string name, string description, string schema) => new() { ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = name, ["description"] = description, ["parameters"] = JsonNode.Parse(schema) } };
}

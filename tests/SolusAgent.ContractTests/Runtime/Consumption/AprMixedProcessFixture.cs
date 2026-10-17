using System.Text.Json;
using AprHost;
using CustomTools;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Candidates;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.ContractTests.Runtime.Tools;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Consumption;

internal static class AprMixedProcessFixture
{
    internal sealed record MixedObservation(string Stage, int Pid, ContextAdmission Admission, int Claims, int Imports,
        int Dispatches, int CounterEntries, int OriginalEntries, int Queries, int CounterEffects, long CounterTotal,
        int ReadMutations, int ToolCharges, int HistoricalToolCharges, int Accepted, int Effects, int NewEffects,
        int Submissions, int HistoricalReceipts, Guid OriginalSubmission, bool PriorHostCommitted, bool CompleteBatch,
        bool FrozenAfterLateReturn);
    private sealed record CounterState(long Total, int Effects);

    internal static async Task<M4AprFreshProcessTests.Observation> Execute(string directory, string stage)
    {
        var source = stage.StartsWith("mixed-source-", StringComparison.Ordinal);
        var kindPath = Path.Combine(directory, "mixed-kind.txt");
        if (source) File.WriteAllText(kindPath, stage["mixed-source-".Length..]);
        var kind = File.ReadAllText(kindPath);
        var target = stage == "mixed-wrong-target" ? "OTHER_SNAPSHOT" : "APR_FIXED_SNAPSHOT_41";
        var counterPath = Path.Combine(directory, "mixed-counter.json");
        var previous = source ? new CounterState(0, 0) : JsonSerializer.Deserialize<CounterState>(File.ReadAllText(counterPath))!;
        var counter = new CounterCapability(initialTotal: previous.Total, initialEffects: previous.Effects,
            observe: (total, effects) => File.WriteAllText(counterPath, JsonSerializer.Serialize(new CounterState(total, effects))));
        var recovery = new RecoveryCounterCapability(target, ToolRecoveryGuarantee.QueryOutcome, mutates: kind == "mutation",
            storePath: Path.Combine(directory, "mixed-read.json"), fixedResultJson: "{\"total\":41}");
        var clock = new ControlledTimeProvider();
        var observed = new ObservedRecovery(new RecoverableCounterTool(maximumResultBytes: stage == "mixed-wrong-tool" ? 101 : 100,
            effect: kind == "mutation" ? ToolEffect.Mutating : ToolEffect.ReadOnly));
        if (source) recovery.BeforeReturn = async (_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(31));
            await WaitFile(Path.Combine(directory, "release-original"));
        };
        RuntimeToolRegistration[] tools = [new(new CounterTool(maximumResultBytes: 100), counter),
            new(observed, recovery, kind == "read" ? new("keyed_recovery", target, ToolRecoveryGuarantee.QueryOutcome) : null)];
        var authorization = Path.Combine(directory, "mixed-authorization");
        var authority = source ? null : new Authority(authorization, stage != "mixed-wrong-authority", recovery.TargetSnapshot);
        var provider = new PersistentProvider(tools: true);
        var agent = CandidateContextFixture.Agent(provider, authority, tools: tools, options: new(clock, requireContinuation: true));
        var host = new AprBusinessHost((IAgent)agent, contextCandidateAgent: agent,
            acceptance: new(Path.Combine(directory, "mixed-business")));
        var priorHostCommitted = !source && host.Acceptance.EffectCount == 1;
        var priorEffects = 0; var completeBatch = false;
        provider.Inspect = r =>
        {
            File.AppendAllText(Path.Combine(directory, "mixed-provider-entries.txt"), $"{stage}:{Environment.ProcessId}\n");
            if (source && provider.Effects == 1)
            {
                Assert.Equal(1, host.Acceptance.AcceptedCount);
                priorEffects = host.Acceptance.ApplyEffects();
                Assert.Equal(1, priorEffects); priorHostCommitted = true;
            }
            if (!source)
            {
                var results = r.Inputs.Where(i => i.ToolResult is not null).Select(i => i.ToolResult!).ToArray();
                completeBatch = results.Select(t => t.Call.CallId).SequenceEqual(new[] { "first", "uncertain", "last" })
                    && results.All(t => t.Outcome == ToolOutcome.Succeeded)
                    && results[0].IsHistorical && results[1].IsHistorical && !results[2].IsHistorical
                    && results[1].Json == "{\"total\":41}";
            }
        };
        provider.Respond = r => source && provider.Effects == 2 ? PartialRecoveryTests.Calls(r)
            : new(r.Scope, r.Attempt, ProviderFinish.Final, new AprItem(source ? "prior" : "after", source ? 7 : 8).ToPayload(), [],
                new(r.Scope, r.Attempt, "APR_MIXED_REPLAY"u8.ToArray()));
        var submitted = new List<Guid>();
        var channel = new ScriptedCandidateHost((s, _) =>
        {
            submitted.Add(s.SubmissionId);
            return ValueTask.FromResult<CandidateFeedback?>(new(s.ExecutionId, s.SubmissionId,
                CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept,
                source ? CandidateContinuation.Continue : CandidateContinuation.End));
        });
        var request = CandidateContextFixture.Request(limits: new(maximumLogicalCalls: 4, maximumPhysicalDispatches: 4,
            maximumToolInvocations: source ? 3 : stage == "mixed-insufficient" ? 1 : 2));
        ContextExecutionRequest context;
        if (source) context = new(request, ContextExecutionIntent.Fresh);
        else
        {
            ContextRoundGrant grant;
            var grantPath = Path.Combine(directory, "mixed-grant.json");
            if (stage == "mixed-duplicate")
            {
                grant = JsonSerializer.Deserialize<ContextRoundGrant>(File.ReadAllText(grantPath))!;
                request = CandidateContextFixture.Request(id: grant.ExecutionId, limits: request.UsageLimits);
            }
            else
            {
                grant = new(Guid.NewGuid(), FileContextAuthority.Selection(authorization).Info, request.ExecutionId, Guid.NewGuid());
                File.WriteAllText(grantPath, JsonSerializer.Serialize(grant));
            }
            context = new(request, ContextExecutionIntent.ContinueRun, FileContextAuthority.Read(authorization), grant);
        }
        var sink = source ? new RestrictedContextHost() : null;
        var result = await host.ExecuteCandidatesWithContextAsync(context, source ? new(2, 0, 1) : new(1, 0, 0), channel, sink);
        var newEffects = source ? priorEffects : result.Candidates is null ? 0 : host.Acceptance.ApplyEffects();
        var originalPath = Path.Combine(directory, "mixed-original-id.json");
        if (source) File.WriteAllText(originalPath, JsonSerializer.Serialize(Assert.Single(host.Acceptance.Accepted).SubmissionId));
        var original = JsonSerializer.Deserialize<Guid>(File.ReadAllText(originalPath));
        if (!source && result.Candidates is not null)
        {
            Assert.Equal(original, Assert.Single(result.Candidates.HistoricalReceipts).SubmissionId);
            Assert.DoesNotContain(original, submitted);
        }
        var observation = new MixedObservation(stage, Environment.ProcessId, result.Context.Admission, authority?.Claims ?? 0,
            provider.Imports, provider.Effects, counter.Effects - previous.Effects, recovery.Invocations, recovery.Queries,
            counter.Effects, counter.Total, ReadMutations(directory), result.Context.Outcome?.Usage?.ToolInvocations?.Invoked ?? 0,
            result.Context.History.Sum(h => h.Usage.ToolInvocations?.Invoked ?? 0), host.Acceptance.AcceptedCount,
            host.Acceptance.EffectCount, newEffects, submitted.Count, result.Candidates?.HistoricalReceipts.Count ?? 0,
            original, priorHostCommitted, completeBatch, false);
        if (source)
        {
            Assert.Equal(ContextCaptureStatus.Delivered, result.Context.CaptureStatus);
            Assert.Equal(CandidateStopReason.DurationLimit, result.Candidates!.StopReason);
            var envelope = sink!.CopyRestrictedContext();
            FileContextAuthority.Save(authorization, envelope, result.Context.Checkpoint!);
            File.WriteAllText(Path.Combine(authorization, "actual-target.txt"), target);
            var frozen = envelope.CopyRestrictedPayload(); var safe = JsonSerializer.Serialize(result);
            File.WriteAllText(Path.Combine(directory, stage + "-mixed.json"), JsonSerializer.Serialize(observation));
            File.WriteAllText(Path.Combine(directory, "source-ready"), "ready");
            await WaitFile(Path.Combine(directory, "release-original"));
            await observed.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
            observation = observation with { FrozenAfterLateReturn = frozen.SequenceEqual(envelope.CopyRestrictedPayload())
                && safe == JsonSerializer.Serialize(result) && host.Acceptance.AcceptedCount == 1 && host.Acceptance.EffectCount == 1 };
        }
        File.WriteAllText(Path.Combine(directory, stage + "-mixed.json"), JsonSerializer.Serialize(observation));
        return new(stage, Environment.ProcessId, observation.Admission, observation.Imports, observation.Dispatches,
            observation.Submissions, observation.Accepted, observation.Effects, observation.NewEffects, observation.HistoricalReceipts,
            result.Context.History.Sum(h => h.Usage.Attempts.Count), context.RoundGrant?.Source.CheckpointId ?? result.Context.Checkpoint?.CheckpointId,
            observation.Claims, Stop: result.Candidates?.StopReason);
    }

    private static int ReadMutations(string directory)
    {
        using var state = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "mixed-read.json")));
        return state.RootElement.GetProperty("Effects").GetInt32();
    }

    internal static async Task WaitFile(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!File.Exists(path))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("APR process coordination expired.");
            await Task.Delay(20);
        }
    }

    private sealed class Authority(string directory, bool authorized, string actualTarget) : IRuntimeContextAuthority
    {
        private readonly FileContextAuthority inner = new(directory);
        internal int Claims { get; private set; }
        public bool TryClaim(AgentContextEnvelope context, ContextRoundGrant grant)
        {
            Claims++;
            return authorized && actualTarget == File.ReadAllText(Path.Combine(directory, "actual-target.txt")) && inner.TryClaim(context, grant);
        }
    }

    private sealed class ObservedRecovery(IRecoverableFunctionTool inner) : IRecoverableFunctionTool
    {
        internal TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ToolDescriptor Descriptor => inner.Descriptor;
        public ToolPreparation Prepare(ToolCall call) => inner.Prepare(call);
        public ToolError ValidateCapability(IToolCapability? capability) => inner.ValidateCapability(capability);
        public ToolError ValidateInvocation(PreparedToolInvocation p, ToolCall c, IToolCapability? capability) => inner.ValidateInvocation(p, c, capability);
        public ToolError ValidateRecoveryBinding(ToolRecoveryBinding binding, IToolCapability? capability) => inner.ValidateRecoveryBinding(binding, capability);
        public ToolError ValidateOperation(ToolOperation operation, IToolCapability? capability) => inner.ValidateOperation(operation, capability);
        public ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation p, ToolCall c, IToolCapability? capability, CancellationToken token = default) =>
            capability is RecoveryCounterCapability target ? InvokeOperationAsync(p, new(Guid.NewGuid(), c,
                new("keyed_recovery", target.TargetSnapshot, target.Guarantee)), capability, token) : inner.InvokeAsync(p, c, capability, token);
        public async ValueTask<ToolResult> InvokeOperationAsync(PreparedToolInvocation p, ToolOperation operation, IToolCapability? capability, CancellationToken token = default)
        {
            try { return await inner.InvokeOperationAsync(p, operation, capability, token); }
            finally { Returned.TrySetResult(); }
        }
        public ValueTask<ToolRecoveryReceipt> QueryOutcomeAsync(ToolOperation operation, IToolCapability? capability, CancellationToken token = default) =>
            inner.QueryOutcomeAsync(operation, capability, token);
    }
}

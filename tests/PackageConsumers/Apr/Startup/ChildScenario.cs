using System.Text.Json;
using PackageConsumers.Apr.Business;
using PackageConsumers.Apr.CustomTool;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Providers.DeepSeek;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;

namespace PackageConsumers.Apr.Startup;

internal sealed record Observation(int Pid, Guid Execution, ContextAdmission Admission, ContextRejectionCode Rejection,
    ContextCaptureStatus Capture, CandidateStopReason? Stop, bool HasOutcome, int Sends, int Providers, int Imports,
    int AuthorityCalls, int Claims, int Permissions, int Closures, int ToolEntries, int ToolEffects, int Hosts,
    int NewHostEffects, int HostEffects, Acceptance[] Accepted, int Progress, int Sinks, int CurrentAttempts,
    int HistoricalAttempts, int CurrentTools, int HistoricalTools, int CurrentRepairs, int CurrentContinuations,
    int CurrentReceipts, int HistoricalReceipts, int AcceptedCount, long? Input, long? Output,
    TokenObservationCoverage? InputCoverage, long HistoricalKnownInput, long UnresolvedInput, long ConservativeInput,
    long MeasuredInput, long InputAllowance, long UnresolvedOutput, long ConservativeOutput, int CampaignAttempts, long CampaignCharge,
    Guid[] PhysicalAttempts, Guid[] LogicalCalls, int[] Ordinals, Guid? PreviousAttempt,
    ContextCheckpointInfo? Checkpoint, Guid? RepairsSubmission, Guid? RejectedSubmission);

internal static class ChildScenario
{
    internal static async Task RunAsync(string root, string scenario, string stage)
    {
        Directory.CreateDirectory(root);
        var producing = stage == "produce";
        var targetPath = Path.Combine(root, "target.json");
        if (producing) Store.Write(targetPath, new TargetState(Guid.NewGuid(), 0));
        var target = Store.Read<TargetState>(targetPath);
        var actualTarget = stage == "target" ? Guid.NewGuid() : target.Id;
        var actualPath = stage == "target" ? Path.Combine(root, "other-target.json") : targetPath;
        if (stage == "target") Store.Write(actualPath, new TargetState(actualTarget, 0));
        var counter = new Counter(actualPath, actualTarget);
        var tool = new ObservedTool(new IncrementTool());
        using var handler = new ControlledTransport(scenario, producing);
        using var adapter = new DeepSeekProvider(new(producing ? Wire.CredentialA : Wire.CredentialB, 2048), handler);
        var provider = new ObservedProvider(adapter);
        var hooks = new Hooks();
        var host = new AprBusinessHost(root, producing && scenario == "unknown");
        var progress = new Progress(); var sink = new RestrictedSink();
        var source = Path.Combine(root, stage == "terminal" ? "restored-source" : "source");
        var authority = new CheckpointAuthority(root, source, counter.TargetId);
        var id = Guid.NewGuid(); ContextRoundGrant? grant = null;
        if (stage == "duplicate") { grant = Store.Read<ContextRoundGrant>(Path.Combine(root, "used-grant.json")); id = grant.ExecutionId; }
        var request = new AgentRequest(id, AprBusinessHost.Instruction, producing ? [new(AgentInputSource.Repository, AprBusinessHost.Data)] : [],
            new(producing ? scenario == "mixed" ? 3 : 4 : 1, TimeSpan.FromSeconds(15)),
            AgentCapability.WorkUnitLimit | AgentCapability.DurationLimit | AgentCapability.DispatchLimits | AgentCapability.UsageAccounting | AgentCapability.ToolInvocationLimit,
            new(maximumLogicalCalls: producing ? 3 : 1, maximumPhysicalDispatches: producing ? scenario == "mixed" ? 3 : 2 : 1,
                maximumToolInvocations: 1, accountingPolicy: new(new(64, 16), stage == "low-budget" ? 1 : producing ? 512 : 64,
                    producing ? 128 : 16, UnknownUsagePolicy.ContinueUnknown), retryPolicy: new(2)));
        ContextExecutionRequest execution;
        if (producing) execution = new(request, ContextExecutionIntent.Fresh);
        else
        {
            var envelope = Store.Read<StoredEnvelope>(Path.Combine(source, "envelope.json")).Open();
            var bytes = envelope.CopyRestrictedPayload();
            if (stage == "malformed") bytes = "{!}"u8.ToArray();
            if (stage == "truncated") bytes = bytes[..(bytes.Length / 2)];
            if (stage == "tamper") bytes = [.. bytes, (byte)' ']; // Valid JSON, different independently retained fingerprint.
            envelope = new(envelope.ImplementationId, envelope.FormatVersion, envelope.CompatibilityVersion, bytes);
            var selected = Store.Read<Selection>(Path.Combine(source, "selection.json"));
            grant ??= new(Guid.NewGuid(), selected.Checkpoint, id, Guid.NewGuid());
            if (stage == "restore") Store.Write(Path.Combine(root, "used-grant.json"), grant);
            execution = new(request, ContextExecutionIntent.ContinueRun, envelope, grant);
        }
        var configuration = new RuntimeConfiguration(provider, [new(tool, counter)], hooks, contextAuthority: producing ? null : authority);
        var options = new RuntimeOptions(requireContinuation: true);
        var agent = (IContextCandidateAgent)RuntimeAgentFactory.Create(configuration, options);
        var result = await host.ExecuteAsync(agent, execution, new(3, producing ? 1 : 0, producing ? 1 : 0), sink, progress);
        var usage = result.Context.Outcome?.Usage;
        if (usage is not null)
        {
            // Replay of observations exercises Host idempotence, never an additional dispatch or charge.
            foreach (var old in result.Context.History) host.ObserveCampaign(old.Usage);
            host.ObserveCampaign(usage);
            foreach (var old in result.Context.History) host.ObserveCampaign(old.Usage);
            host.ObserveCampaign(usage);
        }
        Check.That(handler.Sends == provider.Requests.Count && hooks.Permissions.Count == handler.Sends
            && hooks.Closures.Count == handler.Sends, "Exposure, provider and settlement counts differ.");
        for (var i = 0; i < provider.Requests.Count; i++)
        {
            var attempt = provider.Requests[i].Attempt;
            Check.That(hooks.Permissions[i].Attempt.Matches(attempt)
                && hooks.Closures[i].Exposure.Matches(hooks.Permissions[i])
                && hooks.Closures[i].Observation.PhysicalAttemptId == attempt.PhysicalAttemptId
                && hooks.Closures[i].Observation.LogicalCallId == attempt.LogicalCallId
                && hooks.Closures[i].Observation.ExecutionId == attempt.ExecutionId, "Same-attempt hook association failed.");
        }
        if (hooks.Closures.Count != 0)
            Check.That(usage?.Accounting is { } accounting && hooks.Closures[^1].Accounting!.Matches(accounting), "Final settlement accounting mismatch.");
        if (result.Context.CaptureStatus == ContextCaptureStatus.Delivered)
        {
            Check.That(sink.Envelope is not null && result.Context.Checkpoint is not null, "Delivered capture is incomplete.");
            var bytes = sink.Envelope!.CopyRestrictedPayload();
            var restricted = System.Text.Encoding.UTF8.GetString(bytes);
            Check.That(!restricted.Contains(Wire.CredentialA, StringComparison.Ordinal) && !restricted.Contains(Wire.CredentialB, StringComparison.Ordinal), "Credential entered saved context.");
            CheckpointAuthority.Save(Path.Combine(root, producing ? "source" : "restored-source"), sink.Envelope, result.Context.Checkpoint!, target.Id);
        }
        if (stage == "target") Check.That(counter.Effects == 0, "Changed actual target received an effect.");
        var candidates = result.Candidates;
        var observation = new Observation(Environment.ProcessId, id, result.Context.Admission, result.Context.RejectionCode,
            result.Context.CaptureStatus, candidates?.StopReason, result.Context.Outcome is not null, handler.Sends, provider.Requests.Count,
            provider.Imports, producing ? 0 : authority.Calls, producing ? 0 : authority.Claims, hooks.Permissions.Count, hooks.Closures.Count,
            tool.Entries, Store.Read<TargetState>(targetPath).Effects, host.Calls, host.NewEffects, host.Effects, host.Accepted.ToArray(), progress.Items.Count, sink.Calls,
            usage?.Attempts.Count ?? 0, result.Context.History.Sum(h => h.Usage.Attempts.Count), usage?.ToolInvocations?.Invoked ?? 0,
            result.Context.History.Sum(h => h.Usage.ToolInvocations?.Invoked ?? 0), candidates?.RepairsAdmitted ?? 0,
            candidates?.ContinuationsAdmitted ?? 0, candidates?.Receipts.Count ?? 0, candidates?.HistoricalReceipts.Count ?? 0,
            candidates?.AcceptedCount ?? 0, usage?.InputTokens.ObservedTokens, usage?.OutputTokens.ObservedTokens, usage?.InputTokens.Coverage,
            result.Context.History.Sum(h => h.Usage.Attempts.Sum(a => a.Usage.InputTokens ?? 0)), usage?.Accounting?.Input.UnresolvedTokens ?? 0,
            usage?.Accounting?.Input.ConservativeChargeTokens ?? 0, usage?.Accounting?.Input.MeasuredTokens ?? 0,
            usage?.Accounting?.Policy.InputAllowance ?? 0, usage?.Accounting?.Output.UnresolvedTokens ?? 0,
            usage?.Accounting?.Output.ConservativeChargeTokens ?? 0, host.Campaign.Count, host.Campaign.Sum(a => a.InputCharge),
            usage?.Attempts.Select(a => a.PhysicalAttemptId).ToArray() ?? [], usage?.Attempts.Select(a => a.LogicalCallId).ToArray() ?? [],
            usage?.Attempts.Select(a => a.AttemptNumber).ToArray() ?? [], provider.Requests.LastOrDefault()?.History?.OperationOrigin?.PhysicalAttemptId,
            result.Context.Checkpoint, host.Accepted.LastOrDefault()?.RepairsSubmissionId,
            File.Exists(Path.Combine(root, "rejected.json")) ? Store.Read<Rejection>(Path.Combine(root, "rejected.json")).SubmissionId : null);
        Check.Confined(JsonSerializer.Serialize(result) + JsonSerializer.Serialize(progress.Items) + JsonSerializer.Serialize(observation)
            + JsonSerializer.Serialize(RuntimeAgentFactory.Describe(configuration, options)));
        Store.Write(Path.Combine(root, stage + "-observation.json"), observation);
        Console.WriteLine("APR child assertions passed.");
    }

    private sealed class Progress : IProgress<AgentProgress>
    {
        internal List<AgentProgress> Items { get; } = [];
        public void Report(AgentProgress value) => Items.Add(value);
    }
    private sealed class ObservedTool(IFunctionTool inner) : IFunctionTool
    {
        internal int Entries { get; private set; }
        public ToolDescriptor Descriptor => inner.Descriptor;
        public ToolPreparation Prepare(ToolCall call) => inner.Prepare(call);
        public ToolError ValidateCapability(IToolCapability? capability) => inner.ValidateCapability(capability);
        public ToolError ValidateInvocation(PreparedToolInvocation prepared, ToolCall call, IToolCapability? capability) => inner.ValidateInvocation(prepared, call, capability);
        public ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation prepared, ToolCall call, IToolCapability? capability, CancellationToken cancellationToken = default)
        { Entries++; return inner.InvokeAsync(prepared, call, capability, cancellationToken); }
    }
    private sealed class Hooks : IRuntimeExposureHooks
    {
        internal List<RuntimeExposure> Permissions { get; } = [];
        internal List<RuntimeSettlement> Closures { get; } = [];
        public ValueTask<ExposureAcknowledgement?> BeforeDispatchAsync(RuntimeExposure exposure, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Permissions.Add(exposure);
            return ValueTask.FromResult<ExposureAcknowledgement?>(new(exposure, RuntimeHookStatus.Acknowledged, ExposureDecision.Permit, exposure.RequiredAcknowledgement));
        }
        public ValueTask<SettlementAcknowledgement?> AfterAttemptAsync(RuntimeSettlement settlement, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Closures.Add(settlement);
            return ValueTask.FromResult<SettlementAcknowledgement?>(new(settlement.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue, settlement.Accounting));
        }
    }
}

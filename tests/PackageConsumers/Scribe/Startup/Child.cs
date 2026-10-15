using System.Text.Json;
using PackageConsumers.Scribe.Business;
using PackageConsumers.Scribe.CustomTool;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Startup;
namespace PackageConsumers.Scribe.Startup;
internal sealed record Observation(int Pid, bool Rejected, Guid? Execution, CandidateStopReason? Stop, int Sends, int Imports, int HostEffects,
    int Retained, string[] ToolEntries, int Repairs, int Accepts, int Receipts, int History, long? Input, long? Output, long? Unresolved,
    Guid[] Physical, Guid[] Logical, Guid[] Submissions, int Backlinks, long? InitialMeasured, long? InitialReserved, int InitialAttempts);
internal static class Child
{
    internal static async Task Run(string directory, bool first, string variant)
    {
        var transfer = JsonSerializer.Deserialize<BusinessProgress>(File.ReadAllBytes(Path.Combine(directory, "business.json")))!;
        Check.That(Directory.GetFiles(directory).Select(Path.GetFileName).Order().SequenceEqual(new[] { "business.json" }), "Child received unexpected persisted input.");
        ScribeHost host;
        try { host = new(transfer, member =>
        {
            var effects = Path.Combine(directory, "host-effects");Directory.CreateDirectory(effects);
            using var stream = new FileStream(Path.Combine(effects, member + ".effect"), FileMode.CreateNew, FileAccess.Write);stream.WriteByte(1);
        }); }
        catch (ArgumentException) when (variant.StartsWith("negative", StringComparison.Ordinal))
        {
            Write(directory, new(Environment.ProcessId, true, null, null, 0, 0, 0, 0, [], 0, 0, 0, 0, null, null, 0, [], [], [], 0, 0, 0, 0));return;
        }
        Check.That(!variant.StartsWith("negative", StringComparison.Ordinal), "Invalid progress passed Host validation.");
        var target = Path.Combine(directory, "targets");Directory.CreateDirectory(target);
        var capability = new MemberCapability(target, transfer.Selected.Except(transfer.Accepted.Select(f => f.Member)).ToArray());
        var hooks = new Hooks();
        using var handler = new Transport(transfer, first, variant, () => Check.That(hooks.Exposures.Count == hooks.Settlements.Count + 1, "HTTP preceded permission."));
        using var adapter = new SolusAgent.Providers.DeepSeek.DeepSeekProvider(new(Transport.Credential, 2048), handler);
        var provider = new Provider(adapter);var options = new RuntimeOptions(requireContinuation: true);
        var configuration = new SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration(provider, [new(new MemberTool(), capability)], hooks);
        var agent = (IContextCandidateAgent)RuntimeAgentFactory.Create(configuration, options);
        var sink = new Sink();var progress = new SafeProgress();
        var result = await host.RunAsync(agent, first, variant, sink, progress);
        var candidates = result.Candidates!;var usage = candidates.Outcome.Usage!;
        Check.That(result.Context.Admission == ContextAdmission.Fresh && result.Context.History.Count == 0 && candidates.HistoricalReceipts.Count == 0
            && usage.ContinuedCalls.Count == 0 && provider.Imports == 0 && provider.Requests[0].History is null, "Fresh imported historical runtime state.");
        Check.That(result.Context.CaptureStatus == ContextCaptureStatus.Delivered && sink.Envelope is not null, "Fresh capture failed.");
        Check.That(!System.Text.Encoding.UTF8.GetString(sink.Envelope!.CopyRestrictedPayload()).Contains(Transport.Credential, StringComparison.Ordinal), "Credential captured.");
        // Captured Runtime state is discarded; only domain progress is transferred by the parent.
        Check.That(hooks.Exposures.Count == handler.Sends && hooks.Settlements.Count == handler.Sends && provider.Requests.Count == handler.Sends, "Hook/HTTP counts differ.");
        for (var i = 0; i < handler.Sends; i++)
            Check.That(hooks.Exposures[i].Attempt.Matches(provider.Requests[i].Attempt) && hooks.Settlements[i].Exposure.Matches(hooks.Exposures[i])
                && hooks.Settlements[i].Observation.PhysicalAttemptId == provider.Requests[i].Attempt.PhysicalAttemptId, "Hook association mismatch.");
        Check.That(hooks.Settlements[^1].Accounting!.Matches(usage.Accounting!), "Settlement ledger differs.");
        Check.That(candidates.Receipts.Count == host.Exchanges.Count && host.Exchanges.Select(e => e.Submission).Distinct().Count() == host.Exchanges.Count, "Independent receipt count mismatch.");
        for (var i = 0; i < host.Exchanges.Count; i++)
            Check.That(candidates.Receipts[i].SubmissionId == host.Exchanges[i].Submission && candidates.Receipts[i].ExecutionId == host.Exchanges[i].Execution
                && candidates.Receipts[i].Decision == host.Exchanges[i].Decision && candidates.Receipts[i].Acknowledgement == host.Exchanges[i].Acknowledgement, "Independent feedback association mismatch.");
        if (first && variant is not ("repair" or "unknown"))
            Check.That(host.Exchanges[1].Repairs == host.Exchanges[0].Submission && candidates.RepairsAdmitted == 1, "Repair backlink was lost.");
        if (first && variant == "usage") Check.That(usage.Attempts[^1].Usage.InputTokens is null && usage.Attempts[^1].Usage.OutputTokens is null
            && usage.InputTokens.Coverage == SolusAgent.Api.Usage.TokenObservationCoverage.Partial && usage.Accounting!.Input.ConservativeChargeTokens == 0
            && usage.Accounting.Output.UnresolvedTokens == 16, "Missing usage was turned into measured consumption.");
        Check.That(first || host.Exchanges.All(e => e.Repairs is null), "Fresh repair backlink inherited.");
        Check.That(usage.ToolInvocations?.Invoked == capability.Entries.Count, "Runtime tool entries differ from actual capability effects.");
        Check.That(Directory.GetFiles(target).Length == capability.Entries.Count && capability.Entries.Distinct().Count() == capability.Entries.Count, "Tool effects repeated.");
        Check.Safe(JsonSerializer.Serialize(result) + JsonSerializer.Serialize(progress.Items) + JsonSerializer.Serialize(RuntimeAgentFactory.Describe(configuration, options)));
        Check.That(host.NewEffects == Directory.GetFiles(Path.Combine(directory, "host-effects")).Length, "Independent Host effect witness differs.");
        File.WriteAllBytes(Path.Combine(directory, "validated.json"), JsonSerializer.SerializeToUtf8Bytes(host.Progress));
        var initial = hooks.Exposures[0].Accounting!;
        Write(directory, new(Environment.ProcessId, false, candidates.Outcome.ExecutionId, candidates.StopReason, handler.Sends, provider.Imports,
            host.NewEffects, transfer.Accepted.Length, capability.Entries.ToArray(), candidates.RepairsAdmitted, candidates.AcceptedCount, candidates.Receipts.Count,
            result.Context.History.Count, usage.InputTokens.ObservedTokens, usage.OutputTokens.ObservedTokens, usage.Accounting!.Input.UnresolvedTokens,
            usage.Attempts.Select(a => a.PhysicalAttemptId).ToArray(), usage.Attempts.Select(a => a.LogicalCallId).ToArray(), candidates.Receipts.Select(a => a.SubmissionId).ToArray(),
            host.Exchanges.Count(e => e.Repairs is not null), initial.Input.MeasuredTokens, initial.Input.ReservedTokens, initial.Attempts.Count));
    }
    private static void Write(string root, Observation observation)
    { Check.Safe(JsonSerializer.Serialize(observation));File.WriteAllBytes(Path.Combine(root, "observation.json"), JsonSerializer.SerializeToUtf8Bytes(observation));Console.WriteLine("Scribe child passed."); }
    private sealed class SafeProgress : IProgress<AgentProgress> { internal List<AgentProgress> Items { get; } = [];public void Report(AgentProgress value) => Items.Add(value); }
    private sealed class Hooks : IRuntimeExposureHooks
    {
        internal List<RuntimeExposure> Exposures { get; } = [];internal List<RuntimeSettlement> Settlements { get; } = [];
        public ValueTask<ExposureAcknowledgement?> BeforeDispatchAsync(RuntimeExposure exposure, CancellationToken token)
        { token.ThrowIfCancellationRequested();Exposures.Add(exposure);return ValueTask.FromResult<ExposureAcknowledgement?>(new(exposure, RuntimeHookStatus.Acknowledged, ExposureDecision.Permit, exposure.RequiredAcknowledgement)); }
        public ValueTask<SettlementAcknowledgement?> AfterAttemptAsync(RuntimeSettlement settlement, CancellationToken token)
        { token.ThrowIfCancellationRequested();Settlements.Add(settlement);return ValueTask.FromResult<SettlementAcknowledgement?>(new(settlement.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue, settlement.Accounting)); }
    }
}

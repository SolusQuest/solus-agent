using System.Diagnostics;
using System.Text.Json;
using CustomTools;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Tools.Api;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Candidates;

internal sealed record CandidateChildObservation(int Pid, ContextAdmission Admission, ContextCaptureStatus Capture,
    CandidateStopReason? Stop, int Providers, int Hosts, int Repairs, int Continuations, int Accepted,
    int CurrentAttempts, int HistoricalAttempts, long? CurrentInput, long? HistoricalInput,
    int Corrections, Guid? Submission, Guid? Origin, Guid? Replaces, string? History, string? ExpectedHistory,
    Guid? LogicalCall, Guid? Previous, Guid? Physical, int? AttemptNumber, int Counter, int Transform, int BusinessEffects, int Imports, string? Profile, string? ExpectedProfile);

internal static class CandidateContextChild
{
    internal static async Task<int> Run(string[] args)
    {
        var root = args[1]; var command = args[2]; var parts = command.Split(':'); var scenario = parts[0]; var stage = parts[1];
        Directory.CreateDirectory(root);
        var first = stage == "A"; var toolMode = scenario == "tools";
        var targetPath = Path.Combine(root, "targets.json"); var selectedPath = Path.Combine(root, "selected-targets.json");
        var targets = first || !toolMode ? new ToolTargets(Guid.NewGuid(), Guid.NewGuid(), 0, 0, 0)
            : JsonSerializer.Deserialize<ToolTargets>(File.ReadAllText(targetPath))!;
        if (stage == "changed-target") targets = targets with { CounterId = Guid.NewGuid() };
        var initialCounter = targets.CounterEffects; var initialTransform = targets.TransformEffects;
        var counter = new CounterCapability(initialTotal: targets.Total, initialEffects: targets.CounterEffects, observe: (total, effects) =>
        { targets = targets with { Total = total, CounterEffects = effects }; File.WriteAllText(targetPath, JsonSerializer.Serialize(targets)); });
        var transform = new TransformCapability(targets.TransformEffects, effects =>
        { targets = targets with { TransformEffects = effects }; File.WriteAllText(targetPath, JsonSerializer.Serialize(targets)); });
        var provider = new PersistentProvider(tools: toolMode);
        var tools = toolMode ? ToolContextFixture.Bindings(counter, transform).Select(t => new RuntimeToolRegistration(new ObservingTool(t.Tool), t.Capability)).ToArray() : [];
        var produced = new List<(ProviderRequest Request, ProviderResponse Response)>();
        string? expected = null; string? expectedProfile = null;
        provider.Inspect = r => File.AppendAllText(Path.Combine(root, "effects.txt"), $"provider:{Environment.ProcessId}\n");
        provider.Respond = r =>
        {
            if (!first) return ToolContextFixture.Final(r);
            if (scenario == "accepted" && provider.Effects == 2)
            {
                expected = ToolContextFixture.History(r); expectedProfile = Profile(r);
                throw new ProviderFailureException(new(ProviderRetryKind.Transient));
            }
            var response = scenario == "tools" && provider.Effects == 2 ? ToolContextFixture.Calls(r) : ToolContextFixture.Final(r);
            produced.Add((r, response)); return response;
        };
        using var cancel = new CancellationTokenSource();
        var lateFeedback = new TaskCompletionSource<CandidateFeedback?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CandidateSubmission? heldSubmission = null;
        ScriptedCandidateHost host = null!;
        host = new ScriptedCandidateHost((s, _) =>
        {
            File.AppendAllText(Path.Combine(root, "effects.txt"), $"host:{Environment.ProcessId}:{s.SubmissionId}\n");
            var decision = first && scenario is "correction" or "tools" or "reject-end" ? CandidateDecision.Reject : CandidateDecision.Accept;
            var continuation = first && scenario is "accepted" or "correction" or "tools" ? CandidateContinuation.Continue : CandidateContinuation.End;
            if (decision == CandidateDecision.Accept || scenario is "unknown" or "held")
            {
                host.ApplyEffect();
                File.AppendAllText(Path.Combine(root, "business-effects.txt"), $"effect:{Environment.ProcessId}:{s.SubmissionId}\n");
            }
            if (first && scenario == "held") { heldSubmission = s; cancel.Cancel(); return new(lateFeedback.Task); }
            return ValueTask.FromResult<CandidateFeedback?>(scenario == "unknown"
                ? new(s.ExecutionId, s.SubmissionId, CandidateAcknowledgement.Unknown)
                : new(s.ExecutionId, s.SubmissionId, CandidateAcknowledgement.Acknowledged, decision, continuation,
                    decision == CandidateDecision.Reject && continuation == CandidateContinuation.Continue ? "RESTRICTED_CORRECTION_CANARY" : null));
        });
        var request = CandidateContextFixture.Request(work: first && toolMode ? 2 : 4,
            limits: new(maximumPhysicalDispatches: first && scenario == "accepted" ? 2 : 4, maximumToolInvocations: 2, retryPolicy: new(2)));
        var options = new RuntimeOptions(maximumRecords: scenario == "correction" && stage != "C" ? 6 : 64, requireContinuation: true);
        var sink = new RestrictedContextHost();
        ContextExecutionRequest input;
        IRuntimeContextAuthority? authority = null;
        if (first) input = new(request, ContextExecutionIntent.Fresh);
        else
        {
            var envelope = FileContextAuthority.Read(root); var selection = FileContextAuthority.Selection(root);
            input = new(request, ContextExecutionIntent.ContinueRun, envelope, new(Guid.NewGuid(), selection.Info, request.ExecutionId, Guid.NewGuid()));
            authority = toolMode ? new TargetAuthority(root, targets) : new FileContextAuthority(root);
        }
        var result = await CandidateConsumer.RunWithContextAsync(CandidateContextFixture.Agent(provider, authority, options, tools: tools),
            input, new(3, first || stage == "C" ? 2 : 0, first ? 2 : 0), host, sink,
            progress: first && scenario == "final-ready" ? new CandidateContextTests.CallbackProgress(() => throw new InvalidOperationException("OBSERVER_CANARY")) : null,
            cancellationToken: cancel.Token);
        if (heldSubmission is not null)
        {
            var frozen = JsonSerializer.Serialize(result); var frozenBytes = sink.CopyRestrictedContext().CopyRestrictedPayload();
            lateFeedback.TrySetResult(new(heldSubmission.ExecutionId, heldSubmission.SubmissionId, CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept, CandidateContinuation.End));
            await lateFeedback.Task;
            if (frozen != JsonSerializer.Serialize(result) || !frozenBytes.SequenceEqual(sink.CopyRestrictedContext().CopyRestrictedPayload())) return 4;
        }
        if (result.Context.CaptureStatus == ContextCaptureStatus.Delivered) FileContextAuthority.Save(root, sink.CopyRestrictedContext(), result.Context.Checkpoint!);
        if (first && toolMode)
        {
            File.WriteAllText(selectedPath, JsonSerializer.Serialize(targets));
            var originalFinal = ProviderResponse.RestoreFinal(produced[0].Request, produced[0].Response);
            var originalTools = ProviderResponse.RestoreToolCalls(produced[1].Request, produced[1].Response);
            var results = tools.SelectMany(t => ((ObservingTool)t.Tool).Results).Select(ProviderInput.FromTool);
            var oracle = new ProviderRequest(provider.Scope, new(request.ExecutionId, Guid.NewGuid(), Guid.NewGuid(), 1),
                [ProviderInput.Instruction(request.Instructions), ProviderInput.FromModel(originalFinal), ProviderInput.Data("RESTRICTED_CORRECTION_CANARY"),
                    ProviderInput.FromModel(originalTools), .. results], tools.Select(t => t.Descriptor).ToArray(), originalTools.Continuation,
                ProviderCapabilities.Continuation, history: new([originalFinal.Attempt, originalTools.Attempt]));
            expected = ToolContextFixture.History(oracle); expectedProfile = Profile(oracle);
        }
        var last = provider.LastRequest; var receipt = result.Candidates?.Receipts.LastOrDefault() ?? result.Candidates?.HistoricalReceipts.LastOrDefault();
        var observed = new CandidateChildObservation(Environment.ProcessId, result.Context.Admission, result.Context.CaptureStatus,
            result.Candidates?.StopReason, provider.Effects, host.Submissions.Count, result.Candidates?.RepairsAdmitted ?? 0,
            result.Candidates?.ContinuationsAdmitted ?? 0, result.Candidates?.AcceptedCount ?? 0,
            result.Candidates?.Outcome.Usage?.Attempts.Count ?? 0, result.Context.History.Sum(h => h.Usage.Attempts.Count),
            result.Candidates?.Outcome.Usage?.InputTokens.ObservedTokens, result.Context.History.Sum(h => h.Usage.InputTokens.ObservedTokens),
            last?.Inputs.Count(i => i.Text == "RESTRICTED_CORRECTION_CANARY") ?? 0,
            receipt?.SubmissionId, receipt?.ExecutionId, host.Submissions.LastOrDefault()?.RepairsSubmissionId,
            last is null ? null : ToolContextFixture.History(last), expected, last?.Attempt.LogicalCallId,
            last?.History?.OperationOrigin?.PhysicalAttemptId, last?.Attempt.PhysicalAttemptId, last?.Attempt.AttemptNumber,
            counter.Effects - initialCounter, transform.Effects - initialTransform, host.Effects, provider.Imports,
            last is null ? null : Profile(last), expectedProfile);
        File.WriteAllText(Path.Combine(root, command.Replace(':', '-') + ".json"), JsonSerializer.Serialize(observed));
        return result.Context.Admission == ContextAdmission.Rejected ? 3 : 0;
    }
    private static string Profile(ProviderRequest r) => JsonSerializer.Serialize(new { r.Bounds, r.RequiredCapabilities,
        AcceptedOrigins = r.Inputs.Where(i => i.Model is not null).Select(i => i.Model!.Attempt) });
    private sealed class TargetAuthority(string root, ToolTargets targets) : IRuntimeContextAuthority
    {
        private readonly FileContextAuthority claims = new(root);
        public bool TryClaim(AgentContextEnvelope context, ContextRoundGrant grant) =>
            JsonSerializer.Deserialize<ToolTargets>(File.ReadAllText(Path.Combine(root, "selected-targets.json"))) == targets && claims.TryClaim(context, grant);
    }
    private sealed class ObservingTool(IFunctionTool inner) : IFunctionTool
    {
        internal List<ToolResult> Results { get; } = [];
        public ToolDescriptor Descriptor => inner.Descriptor;
        public ToolPreparation Prepare(ToolCall call) => inner.Prepare(call);
        public ToolError ValidateCapability(IToolCapability? capability) => inner.ValidateCapability(capability);
        public ToolError ValidateInvocation(PreparedToolInvocation prepared, ToolCall call, IToolCapability? capability) => inner.ValidateInvocation(prepared, call, capability);
        public async ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation prepared, ToolCall call, IToolCapability? capability, CancellationToken token = default)
        { var result = await inner.InvokeAsync(prepared, call, capability, token); Results.Add(result); return result; }
    }
}

public sealed class CandidateContextProcessTests
{
    [Fact]
    public async Task AcceptedThenFailedOperationRestartsInDistinctProcessWithFullExactRequest()
    {
        var root = Root(); var a = await Run(root, "accepted:A"); var b = await Run(root, "accepted:B");
        Assert.NotEqual(a.Pid, b.Pid); Assert.Equal(1, a.Accepted); Assert.Equal(1, a.Continuations);
        Assert.Equal(ContextAdmission.Supplied, b.Admission); Assert.Equal(CandidateStopReason.Completed, b.Stop);
        Assert.Equal(1, b.Providers); Assert.Equal(1, b.Hosts); Assert.Equal(2, b.Accepted); Assert.Equal(0, b.Continuations);
        Assert.Equal(a.ExpectedHistory, b.History); Assert.Equal(a.ExpectedProfile, b.Profile); Assert.Equal(a.Physical, b.Previous); Assert.Equal(a.LogicalCall, b.LogicalCall);
        Assert.Equal(2, b.AttemptNumber); Assert.Equal(2, b.HistoricalAttempts); Assert.Equal(6, b.HistoricalInput); Assert.Equal(3, b.CurrentInput);
        Assert.Equal(5, File.ReadAllLines(Path.Combine(root, "effects.txt")).Length);
        Assert.Equal(1, a.BusinessEffects); Assert.Equal(1, b.BusinessEffects); Assert.Equal(2, File.ReadAllLines(Path.Combine(root, "business-effects.txt")).Length);
    }
    [Fact]
    public async Task CorrectionCommittedBeforeModelSurvivesTwoZeroAdmissionBoundaries()
    {
        var root = Root(); var a = await Run(root, "correction:A"); var b = await Run(root, "correction:B"); var c = await Run(root, "correction:C");
        Assert.NotEqual(a.Pid, b.Pid); Assert.NotEqual(b.Pid, c.Pid); Assert.Equal(CandidateStopReason.RuntimeLimit, a.Stop);
        Assert.Equal(0, a.Repairs); Assert.Equal(0, b.Providers); Assert.Equal(0, b.Hosts); Assert.Equal(0, b.CurrentAttempts);
        Assert.Equal(0, b.CurrentInput); Assert.Equal(ContextCaptureStatus.Delivered, b.Capture);
        // B denies this unadmitted repair; C admits it once under its own renewed allowance.
        Assert.Equal(CandidateStopReason.Completed, c.Stop); Assert.Equal(1, c.Repairs); Assert.Equal(1, c.Corrections);
        Assert.Equal(a.Submission, c.Replaces); Assert.Equal(1, c.Providers); Assert.Equal(1, c.Hosts);
    }
    [Fact]
    public async Task AdmittedRepairReusesCompletedToolsWithoutAnotherRepairDebit()
    {
        var root = Root(); var a = await Run(root, "tools:A"); var b = await Run(root, "tools:B");
        Assert.NotEqual(a.Pid, b.Pid); Assert.Equal(1, a.Repairs); Assert.Equal(1, a.Counter); Assert.Equal(1, a.Transform);
        Assert.Equal(CandidateStopReason.WorkUnitLimit, a.Stop); Assert.Equal(CandidateStopReason.Completed, b.Stop);
        Assert.Equal(0, b.Repairs); Assert.Equal(0, b.Counter); Assert.Equal(0, b.Transform); Assert.Equal(1, b.Corrections);
        Assert.Equal(a.Submission, b.Replaces); Assert.Equal(2, b.HistoricalAttempts);
        Assert.Equal(a.ExpectedHistory, b.History); Assert.Equal(a.ExpectedProfile, b.Profile);
        using var history = JsonDocument.Parse(b.History!);
        Assert.Equal(new[] { "HostInstruction", "ModelData", "InputData", "ModelData", "ToolResultData", "ToolResultData" },
            history.RootElement.GetProperty("Inputs").EnumerateArray().Select(i => ((ProviderInputKind)i.GetProperty("Kind").GetInt32()).ToString()));
    }
    [Theory]
    [InlineData("accept-end", CandidateStopReason.Completed)]
    [InlineData("reject-end", CandidateStopReason.HostEnded)]
    [InlineData("unknown", CandidateStopReason.UnknownAcknowledgement)]
    [InlineData("held", CandidateStopReason.UnknownAcknowledgement)]
    public async Task TerminalAndUnknownFeedbackHaveNoDependentEffectsAcrossProcesses(string scenario, CandidateStopReason stop)
    {
        var root = Root(); var a = await Run(root, scenario + ":A"); var b = await Run(root, scenario + ":B");
        Assert.NotEqual(a.Pid, b.Pid); Assert.Equal(ContextAdmission.Supplied, b.Admission); Assert.Equal(stop, b.Stop);
        Assert.Equal(0, b.Providers); Assert.Equal(0, b.Hosts); Assert.Equal(0, b.CurrentAttempts);
        Assert.Equal(a.Submission, b.Submission); Assert.Equal(a.Origin, b.Origin); Assert.Equal(0, b.BusinessEffects); Assert.Equal(2, File.ReadAllLines(Path.Combine(root, "effects.txt")).Length);
    }
    [Fact]
    public async Task UnsubmittedFinalHasOneFirstInvocationAfterProcessRestart()
    {
        var root = Root(); var a = await Run(root, "final-ready:A"); var b = await Run(root, "final-ready:B");
        Assert.NotEqual(a.Pid, b.Pid); Assert.Equal(CandidateStopReason.ProgressObserverFailed, a.Stop);
        Assert.Equal(1, a.Providers); Assert.Equal(0, a.Hosts); Assert.Equal(0, a.BusinessEffects);
        Assert.Equal(CandidateStopReason.Completed, b.Stop); Assert.Equal(0, b.Providers); Assert.Equal(1, b.Hosts); Assert.Equal(1, b.BusinessEffects);
        Assert.Equal(0, b.CurrentAttempts); Assert.Equal(1, b.HistoricalAttempts);
        var checkpoint = JsonDocument.Parse(FileContextAuthority.Read(root).CopyRestrictedPayload());
        Assert.Equal(b.Submission, checkpoint.RootElement.GetProperty("Candidates").GetProperty("Submissions")[0].GetProperty("SubmissionId").GetGuid());
        Assert.Equal(b.Origin, checkpoint.RootElement.GetProperty("Rounds")[0].GetProperty("Usage").GetProperty("ExecutionId").GetGuid());
    }
    [Fact]
    public async Task ReinjectedDifferentActualTargetRejectsCandidateToolContinuation()
    {
        var root = Root(); await Run(root, "tools:A"); var changed = await Run(root, "tools:changed-target", reject: true);
        Assert.Equal(ContextAdmission.Rejected, changed.Admission); Assert.Equal(0, changed.Imports); Assert.Equal(0, changed.Providers);
        Assert.Equal(0, changed.Hosts); Assert.Equal(0, changed.Counter); Assert.Equal(0, changed.Transform); Assert.Empty(Directory.GetFiles(root, "*.claim"));
    }
    private static string Root() => Path.Combine(AppContext.BaseDirectory, "candidate-context-fixtures", Guid.NewGuid().ToString("N"));
    private static async Task<CandidateChildObservation> Run(string root, string command, bool reject = false)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(CandidateContextProcessTests).Assembly.Location); start.ArgumentList.Add("candidate-context-child");
        start.ArgumentList.Add(root); start.ArgumentList.Add(command);
        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var output = Path.Combine(root, command.Replace(':', '-') + ".json");
        Assert.True(process.ExitCode == (reject ? 3 : 0), $"Child failed {process.ExitCode}: {await stdout}; {await stderr}; {(File.Exists(output) ? File.ReadAllText(output) : "no observation")}");
        return JsonSerializer.Deserialize<CandidateChildObservation>(File.ReadAllText(output))!;
    }
}

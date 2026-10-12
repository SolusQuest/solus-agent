using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CustomTools;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Tools;

internal sealed record CursorObservation(int Pid, ContextAdmission Admission, AgentTerminationReason? Reason,
    ContextCaptureStatus Capture, bool Cursor, int Claims, int Imports, int ProviderEffects, int Entries,
    int HistoricalEntries, int CounterEffects, int KeyedEffects, int Queries, int Invocations, bool Frozen, bool CompleteBatch);

internal static class RecoveryCursorChild
{
    internal static async Task<int> Run(string[] args)
    {
        var root = args[1]; var parts = args[2].Split('|'); var command = parts[0]; var mode = parts[1]; var cut = parts[2]; var stage = parts[3];
        Directory.CreateDirectory(root);
        var producing = command == "cursor-produce"; var middle = command == "cursor-middle";
        var guarantee = mode == "repeat" ? ToolRecoveryGuarantee.ConcurrentSafeRepeat : ToolRecoveryGuarantee.QueryOutcome;
        var counterFile = Path.Combine(root, "cursor-counter.json");
        var counter = new CounterCapability(initialEffects: File.Exists(counterFile) ? int.Parse(File.ReadAllText(counterFile)) : 0,
            observe: (_, n) => File.WriteAllText(counterFile, n.ToString()));
        var target = new RecoveryCounterCapability("CURSOR_TARGET", guarantee, storePath: Path.Combine(root, "cursor-target.json"))
        { ThrowAfterEffect = producing };
        var clock = new ControlledTimeProvider(); using var cancellation = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask Pause()
        {
            if (cut == "cancel") cancellation.Cancel(); else clock.Advance(TimeSpan.FromSeconds(31));
            await release.Task;
        }
        var ordinary = new PauseTool(new CounterTool(cut == "negative" && middle ? CounterMode.FailAfterEffect : CounterMode.Normal,
            maximumResultBytes: 100), middle && stage == "start" && mode == "ordinary"
            ? cut == "throw" ? () => throw new InvalidOperationException("controlled-start-failure")
                : cut == "negative" ? null : Pause : null, returned);
        RuntimeToolRegistration[] tools = [new(ordinary, counter), new(new ObservingRecovery(new RecoverableCounterTool(), returned,
            stage == "start" ? "last" : "uncertain"), target,
            new("keyed_recovery", target.TargetSnapshot, guarantee))];
        if (middle && stage == "start" && mode != "ordinary")
            target.BeforeReturn = (op, _) => op.Call.CallId == "last" ? Pause() : ValueTask.CompletedTask;
        if (middle && stage == "recovery")
        {
            if (guarantee == ToolRecoveryGuarantee.QueryOutcome) target.BeforeQuery = (_, _) => Pause();
            else target.BeforeReturn = (_, _) => Pause();
        }
        if (middle && cut == "before") clock.TimerCreated = _ => { clock.TimerCreated = null; clock.Advance(TimeSpan.FromSeconds(31)); };
        var complete = false;
        var provider = new PersistentProvider(tools: true) { Respond = producing ? r => new(r.Scope, r.Attempt, ProviderFinish.ToolCalls, null,
            [new("uncertain", "recover", "{\"amount\":5}"), new("last", mode == "ordinary" ? "counter" : "recover", "{\"amount\":1}")], new(r.Scope, r.Attempt, [1])) : ToolContextFixture.Final };
        if (!producing) provider.Inspect = r => complete = r.Inputs.Where(i => i.ToolResult is not null).Select(i => i.ToolResult!)
            .Select(t => (t.Call.CallId, t.Outcome)).SequenceEqual(new[] { ("uncertain", ToolOutcome.Succeeded), ("last", ToolOutcome.Succeeded) });
        var request = ToolContextFixture.Request(3, new(maximumToolInvocations: 2)); var sink = new RestrictedContextHost();
        var authority = new CountingAuthority(root);
        ContextExecutionResult result;
        if (producing) result = await ToolContextFixture.Agent(provider, tools).ExecuteWithContextAsync(new(request, ContextExecutionIntent.Fresh), sink);
        else
        {
            var envelope = FileContextAuthority.Read(root);
            if (command == "cursor-forged")
            {
                var node = JsonNode.Parse(envelope.CopyRestrictedPayload())!;
                node["ToolCursor"] = node["Rounds"]![0]!["ToolCursor"]!.DeepClone();
                node["Rounds"]!.AsArray()[^1]!["ToolCursor"] = node["ToolCursor"]!.DeepClone();
                envelope = new(envelope.ImplementationId, envelope.FormatVersion, envelope.CompatibilityVersion, JsonSerializer.SerializeToUtf8Bytes(node));
                FileContextAuthority.Save(root, envelope, FileContextAuthority.Selection(root).Info);
            }
            var grant = new ContextRoundGrant(Guid.NewGuid(), FileContextAuthority.Selection(root).Info, request.ExecutionId, Guid.NewGuid());
            result = await ToolContextFixture.Agent(provider, tools, authority, new(timeProvider: clock, requireContinuation: true))
                .ExecuteWithContextAsync(new(request, ContextExecutionIntent.ContinueRun, envelope, grant), sink, cancellationToken: cancellation.Token);
        }
        AgentContextEnvelope? captured = result.CaptureStatus == ContextCaptureStatus.Delivered ? sink.CopyRestrictedContext() : null;
        if ((producing || middle) && captured is not null) FileContextAuthority.Save(root, captured, result.Checkpoint!);
        var bytes = captured?.CopyRestrictedPayload();
        Write();
        if (middle) File.WriteAllText(Path.Combine(root, "cursor-middle-ready"), "ready");
        if (middle && cut is "timeout" or "cancel")
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(Path.Combine(root, "cursor-release")))
            { if (DateTime.UtcNow > deadline) throw new TimeoutException("Cursor child release expired."); await Task.Delay(10); }
            release.TrySetResult(); await returned.Task.WaitAsync(TimeSpan.FromSeconds(5)); Write();
        }
        return 0;
        void Write() => File.WriteAllText(Path.Combine(root, command + ".json"), JsonSerializer.Serialize(new CursorObservation(
            Environment.ProcessId, result.Admission, result.Outcome?.Reason, result.CaptureStatus,
            bytes is not null && JsonNode.Parse(bytes)!["ToolCursor"] is not null, authority.Claims, provider.Imports, provider.Effects,
            result.Outcome?.Usage?.ToolInvocations?.Invoked ?? 0, result.History.Sum(r => r.Usage.ToolInvocations?.Invoked ?? 0),
            counter.Effects, target.Effects, target.Queries, target.Invocations, bytes is null || bytes.SequenceEqual(captured!.CopyRestrictedPayload()), complete)));
    }
    private sealed class CountingAuthority(string root) : IRuntimeContextAuthority
    {
        internal int Claims;
        public bool TryClaim(AgentContextEnvelope envelope, ContextRoundGrant grant) { Claims++; return new FileContextAuthority(root).TryClaim(envelope, grant); }
    }
    private sealed class PauseTool(IFunctionTool inner, Func<ValueTask>? pause, TaskCompletionSource returned) : IFunctionTool
    {
        public ToolDescriptor Descriptor => inner.Descriptor;
        public ToolPreparation Prepare(ToolCall call) => inner.Prepare(call);
        public ToolError ValidateInvocation(PreparedToolInvocation p, ToolCall c, IToolCapability? cap) => inner.ValidateInvocation(p, c, cap);
        public async ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation p, ToolCall c, IToolCapability? cap, CancellationToken token = default)
        {
            try { var result = await inner.InvokeAsync(p, c, cap, token); if (pause is not null) await pause(); return result; }
            finally { returned.TrySetResult(); }
        }
    }
    private sealed class ObservingRecovery(IRecoverableFunctionTool inner, TaskCompletionSource returned, string watchedCall) : IRecoverableFunctionTool
    {
        public ToolDescriptor Descriptor => inner.Descriptor;
        public ToolPreparation Prepare(ToolCall call) => inner.Prepare(call);
        public ToolError ValidateInvocation(PreparedToolInvocation p, ToolCall c, IToolCapability? cap) => inner.ValidateInvocation(p, c, cap);
        public ToolError ValidateOperation(ToolOperation op, IToolCapability? cap) => inner.ValidateOperation(op, cap);
        public ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation p, ToolCall c, IToolCapability? cap, CancellationToken token = default) => inner.InvokeAsync(p, c, cap, token);
        public async ValueTask<ToolResult> InvokeOperationAsync(PreparedToolInvocation p, ToolOperation op, IToolCapability? cap, CancellationToken token = default)
        { try { return await inner.InvokeOperationAsync(p, op, cap, token); } finally { if (op.Call.CallId == watchedCall) returned.TrySetResult(); } }
        public async ValueTask<ToolRecoveryReceipt> QueryOutcomeAsync(ToolOperation op, IToolCapability? cap, CancellationToken token = default)
        { try { return await inner.QueryOutcomeAsync(op, cap, token); } finally { if (op.Call.CallId == watchedCall) returned.TrySetResult(); } }
    }
}

public sealed class RecoveryCursorProcessTests
{
    [Theory]
    [InlineData("ordinary", "timeout", "start")]
    [InlineData("ordinary", "cancel", "start")]
    [InlineData("ordinary", "throw", "start")]
    [InlineData("ordinary", "negative", "start")]
    [InlineData("ordinary", "before", "recovery")]
    [InlineData("query", "timeout", "start")]
    [InlineData("query", "cancel", "start")]
    [InlineData("repeat", "timeout", "start")]
    [InlineData("repeat", "cancel", "start")]
    [InlineData("query", "cancel", "recovery")]
    [InlineData("repeat", "timeout", "recovery")]
    [InlineData("repeat", "cancel", "recovery")]
    [InlineData("query", "before", "recovery")]
    [InlineData("repeat", "before", "recovery")]
    public async Task EffectiveStateControlsContinuationAcrossProcesses(string mode, string cut, string stage)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "recovery-fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var source = await Run(root, "cursor-produce", mode, cut, stage);
        Assert.True(source.Cursor); Assert.Equal(1, source.KeyedEffects);
        using var middle = Start(root, "cursor-middle", mode, cut, stage);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(Path.Combine(root, "cursor-middle-ready")))
            { Assert.True(DateTime.UtcNow < deadline); Assert.False(middle.HasExited, "Middle process exited before capture."); await Task.Delay(10); }
            var observed = Read(root, "cursor-middle");
            var recoverable = mode != "ordinary" || cut == "before";
            Assert.Equal(ContextCaptureStatus.Delivered, observed.Capture); Assert.Equal(recoverable, observed.Cursor);
            Assert.Equal(cut == "before" ? 0 : stage == "start" ? 2 : 1, observed.Entries);
            Assert.Equal(0, observed.ProviderEffects); Assert.Equal(1, observed.Claims); Assert.Equal(1, observed.HistoricalEntries);
            var last = await Run(root, "cursor-resume", mode, cut, stage);
            Assert.NotEqual(source.Pid, observed.Pid); Assert.NotEqual(observed.Pid, last.Pid);
            Assert.Equal(recoverable ? ContextAdmission.Supplied : ContextAdmission.Rejected, last.Admission);
            Assert.Equal(recoverable ? 1 : 0, last.Claims); Assert.Equal(recoverable ? 1 : 0, last.Imports);
            Assert.Equal(observed.KeyedEffects + (mode != "ordinary" && (stage == "recovery" || cut == "before") ? 1 : 0), last.KeyedEffects);
            Assert.Equal(mode == "ordinary" ? 1 : 0, last.CounterEffects);
            if (recoverable)
            {
                Assert.Equal(AgentTerminationReason.Completed, last.Reason); Assert.True(last.CompleteBatch);
                Assert.Equal(stage == "start" ? 1 : 2, last.Entries); Assert.Equal(1, last.ProviderEffects);
            }
            else
            {
                Assert.Equal(0, last.Entries); Assert.Equal(0, last.Queries); Assert.Equal(0, last.Invocations);
                var forged = await Run(root, "cursor-forged", mode, cut, stage);
                Assert.Equal(ContextAdmission.Rejected, forged.Admission); Assert.Equal(0, forged.Claims); Assert.Equal(0, forged.Imports);
            }
        }
        finally { File.WriteAllText(Path.Combine(root, "cursor-release"), "release"); await Exit(middle); }
        Assert.True(Read(root, "cursor-middle").Frozen);
    }
    private static Process Start(string root, string command, string mode, string cut, string stage)
    {
        var p = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { typeof(RecoveryCursorProcessTests).Assembly.Location, "partial-recovery-child", root, string.Join('|', command, mode, cut, stage) }) p.ArgumentList.Add(arg);
        return Process.Start(p)!;
    }
    private static async Task Exit(Process p)
    {
        var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40)); Assert.True(p.ExitCode == 0, $"{p.ExitCode}: {await output}; {await error}");
    }
    private static CursorObservation Read(string root, string command) => JsonSerializer.Deserialize<CursorObservation>(File.ReadAllText(Path.Combine(root, command + ".json")))!;
    private static async Task<CursorObservation> Run(string root, string command, string mode, string cut, string stage)
    { using var p = Start(root, command, mode, cut, stage); await Exit(p); return Read(root, command); }
}

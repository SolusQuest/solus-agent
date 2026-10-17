using System.Diagnostics;
using System.Text.Json;
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

internal sealed record RecoveryProcessObservation(int Pid, ContextAdmission Admission, AgentTerminationReason? Reason,
    int CurrentEntries, int HistoricalEntries, int Queries, int OriginalEntries, int CounterEffects, int RecoveryEffects,
    int ProviderEffects, int Imports, bool CompleteBatch, bool HistoricalRecovered, bool Frozen);
internal static class PartialRecoveryChild
{
    private sealed record CounterState(long Total, int Effects);
    internal static async Task<int> Run(string[] args)
    {
        if (args[2].StartsWith("cursor-", StringComparison.Ordinal)) return await RecoveryCursorChild.Run(args);
        var root = args[1]; var command = args[2]; Directory.CreateDirectory(root);
        var producing = command.StartsWith("produce-", StringComparison.Ordinal);
        var read = command.Contains("read", StringComparison.Ordinal);
        var query = command.Contains("query", StringComparison.Ordinal);
        var unsafeOriginal = command.Contains("unsafe", StringComparison.Ordinal);
        var guarantee = query ? ToolRecoveryGuarantee.QueryOutcome : ToolRecoveryGuarantee.ConcurrentSafeRepeat;
        var counterPath = Path.Combine(root, "counter.json");
        var counterState = producing ? new CounterState(0, 0) : JsonSerializer.Deserialize<CounterState>(File.ReadAllText(counterPath))!;
        var counter = new CounterCapability(initialTotal: counterState.Total, initialEffects: counterState.Effects,
            observe: (total, effects) => File.WriteAllText(counterPath, JsonSerializer.Serialize(new CounterState(total, effects))));
        var recovery = new RecoveryCounterCapability("PROCESS_TARGET_CANARY", guarantee, !read, Path.Combine(root, "keyed-target.json"));
        var clock = new ControlledTimeProvider();
        var observed = new Observing(new RecoverableCounterTool(effect: read ? ToolEffect.ReadOnly : ToolEffect.Mutating));
        if (producing) recovery.BeforeReturn = async (_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(31));
            await WaitFile(Path.Combine(root, "release-original"));
        };
        if (command.StartsWith("unresolved-", StringComparison.Ordinal)) recovery.QueryBehavior = RecoveryQueryBehavior.Unresolved;
        var binding = command.StartsWith("missing-", StringComparison.Ordinal) || unsafeOriginal ? null
            : new ToolRecoveryBinding("keyed_recovery", command.StartsWith("changed-", StringComparison.Ordinal) ? "CHANGED_TARGET" : recovery.TargetSnapshot, guarantee);
        RuntimeToolRegistration[] tools = [new(new CounterTool(maximumResultBytes: 100), counter), new(observed, recovery, binding)];
        var complete = false; var historical = false;
        var provider = new PersistentProvider(tools: true) { Respond = producing ? PartialRecoveryTests.Calls : ToolContextFixture.Final };
        if (!producing) provider.Inspect = request =>
        {
            var results = request.Inputs.Where(i => i.ToolResult is not null).Select(i => i.ToolResult!).ToArray();
            complete = results.Select(t => t.Call.CallId).SequenceEqual(new[] { "first", "uncertain", "last" })
                && results.All(t => t.Outcome == ToolOutcome.Succeeded);
            historical = results.Length == 3 && results[1].IsHistorical && results[1].InvocationStarted;
        };
        var request = ToolContextFixture.Request(3, new(maximumToolInvocations: 3)); var sink = new RestrictedContextHost();
        ContextExecutionResult result; AgentContextEnvelope envelope;
        if (producing)
        {
            result = await ToolContextFixture.Agent(provider, tools, options: new(timeProvider: clock, requireContinuation: true))
                .ExecuteWithContextAsync(new(request, ContextExecutionIntent.Fresh), sink);
            if (result.CaptureStatus != ContextCaptureStatus.Delivered) return 3;
            envelope = sink.CopyRestrictedContext(); FileContextAuthority.Save(root, envelope, result.Checkpoint!);
        }
        else
        {
            envelope = FileContextAuthority.Read(root); var selected = FileContextAuthority.Selection(root);
            var grant = new ContextRoundGrant(Guid.NewGuid(), selected.Info, request.ExecutionId, Guid.NewGuid());
            result = await ToolContextFixture.Agent(provider, tools, new FileContextAuthority(root))
                .ExecuteWithContextAsync(new(request, ContextExecutionIntent.ContinueRun, envelope, grant), sink);
            if (command.StartsWith("unresolved-", StringComparison.Ordinal) && result.CaptureStatus == ContextCaptureStatus.Delivered)
                FileContextAuthority.Save(root, sink.CopyRestrictedContext(), result.Checkpoint!);
        }
        var frozen = envelope.CopyRestrictedPayload();
        Write(frozen.SequenceEqual(envelope.CopyRestrictedPayload()));
        if (producing)
        {
            File.WriteAllText(Path.Combine(root, "producer-ready"), "ready");
            await WaitFile(Path.Combine(root, "release-original"));
            var late = await observed.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (late.Outcome != ToolOutcome.Succeeded) return 4;
            Write(frozen.SequenceEqual(envelope.CopyRestrictedPayload()));
        }
        return 0;
        void Write(bool unchanged) => File.WriteAllText(Path.Combine(root, command + "-result.json"), JsonSerializer.Serialize(new RecoveryProcessObservation(
            Environment.ProcessId, result.Admission, result.Outcome?.Reason, result.Outcome?.Usage?.ToolInvocations?.Invoked ?? 0,
            result.History.Sum(r => r.Usage.ToolInvocations?.Invoked ?? 0), recovery.Queries, recovery.Invocations, counter.Effects, recovery.Effects,
            provider.Effects, provider.Imports, complete, historical, unchanged)));
    }
    private sealed class Observing(IRecoverableFunctionTool inner) : IRecoverableFunctionTool
    {
        internal TaskCompletionSource<ToolResult> Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ToolDescriptor Descriptor => inner.Descriptor;
        public ToolPreparation Prepare(ToolCall call) => inner.Prepare(call);
        public ToolError ValidateCapability(IToolCapability? capability) => inner.ValidateCapability(capability);
        public ToolError ValidateInvocation(PreparedToolInvocation p, ToolCall c, IToolCapability? cap) => inner.ValidateInvocation(p, c, cap);
        public ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation p, ToolCall c, IToolCapability? cap, CancellationToken token = default) =>
            cap is RecoveryCounterCapability target ? InvokeOperationAsync(p, new ToolOperation(Guid.NewGuid(), c,
                new("keyed_recovery", target.TargetSnapshot, target.Guarantee)), cap, token) : inner.InvokeAsync(p, c, cap, token);
        public ToolError ValidateRecoveryBinding(ToolRecoveryBinding binding, IToolCapability? capability) => inner.ValidateRecoveryBinding(binding, capability);
        public ToolError ValidateOperation(ToolOperation op, IToolCapability? cap) => inner.ValidateOperation(op, cap);
        public async ValueTask<ToolResult> InvokeOperationAsync(PreparedToolInvocation p, ToolOperation op, IToolCapability? cap, CancellationToken token = default)
        { var result = await inner.InvokeOperationAsync(p, op, cap, token); Returned.TrySetResult(result); return result; }
        public ValueTask<ToolRecoveryReceipt> QueryOutcomeAsync(ToolOperation op, IToolCapability? cap, CancellationToken token = default) => inner.QueryOutcomeAsync(op, cap, token);
    }
    private static async Task WaitFile(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!File.Exists(path)) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Child coordination expired."); await Task.Delay(10); }
    }
}

public sealed class PartialRecoveryProcessTests
{
    [Theory]
    [InlineData("read")]
    [InlineData("repeat")]
    [InlineData("query")]
    public async Task OverlappingProcessesPreserveEffectAndCompleteOriginalBatch(string mode)
    {
        var root = Root(); using var first = Start(root, "produce-" + mode);
        try
        {
            await Ready(root, first); Assert.False(first.HasExited);
            var resumed = await Run(root, "resume-" + mode);
            Assert.False(first.HasExited); Assert.NotEqual(first.Id, resumed.Pid);
            Assert.Equal(ContextAdmission.Supplied, resumed.Admission); Assert.Equal(AgentTerminationReason.Completed, resumed.Reason);
            Assert.True(resumed.CompleteBatch); Assert.Equal(mode == "query", resumed.HistoricalRecovered);
            Assert.Equal(2, resumed.CurrentEntries); Assert.Equal(2, resumed.HistoricalEntries);
            Assert.Equal(2, resumed.CounterEffects); Assert.Equal(mode == "read" ? 0 : 1, resumed.RecoveryEffects);
            Assert.Equal(mode == "query" ? 1 : 0, resumed.Queries); Assert.Equal(mode == "query" ? 0 : 1, resumed.OriginalEntries);
            Assert.Equal(1, resumed.ProviderEffects);
        }
        finally { File.WriteAllText(Path.Combine(root, "release-original"), "release"); await Exit(first); }
        var original = Read(root, "produce-" + mode); Assert.True(original.Frozen); Assert.Equal(AgentTerminationReason.ResourceLimit, original.Reason);
    }
    [Theory]
    [InlineData("missing-query")]
    [InlineData("changed-query")]
    [InlineData("unresolved-query")]
    public async Task ProcessRecoveryFailureDoesNotBlindlyRepeatOrRunSuffix(string command)
    {
        var root = Root(); using var first = Start(root, "produce-query");
        try
        {
            await Ready(root, first); var resumed = await Run(root, command);
            Assert.Equal(0, resumed.OriginalEntries); Assert.Equal(1, resumed.CounterEffects); Assert.Equal(1, resumed.RecoveryEffects);
            Assert.Equal(0, resumed.ProviderEffects);
            if (command == "unresolved-query")
            {
                Assert.Equal(1, resumed.CurrentEntries); Assert.Equal(1, resumed.Queries);
                var final = await Run(root, "resume-query"); Assert.Equal(AgentTerminationReason.Completed, final.Reason);
                Assert.Equal(3, final.HistoricalEntries); Assert.True(final.CompleteBatch); Assert.Equal(2, final.CurrentEntries);
            }
            else { Assert.Equal(ContextAdmission.Rejected, resumed.Admission); Assert.Equal(0, resumed.Imports); Assert.Equal(0, resumed.CurrentEntries); }
        }
        finally { File.WriteAllText(Path.Combine(root, "release-original"), "release"); await Exit(first); }
    }
    [Theory]
    [InlineData("unsafe")]
    [InlineData("unsafe-read")]
    public async Task UnknownOriginalWithoutRuntimeRecoveryEvidenceNeverRedispatches(string mode)
    {
        var root = Root(); using var first = Start(root, "produce-" + mode);
        try
        {
            await Ready(root, first); var result = await Run(root, "resume-" + mode);
            Assert.Equal(ContextAdmission.Rejected, result.Admission); Assert.Equal(0, result.CurrentEntries); Assert.Equal(0, result.Imports);
            Assert.Equal(0, result.OriginalEntries); Assert.Equal(0, result.Queries); Assert.Equal(1, result.CounterEffects);
            Assert.Equal(mode == "unsafe" ? 1 : 0, result.RecoveryEffects); Assert.Equal(0, result.ProviderEffects);
        }
        finally { File.WriteAllText(Path.Combine(root, "release-original"), "release"); await Exit(first); }
    }
    private static string Root() { var path = Path.Combine(AppContext.BaseDirectory, "recovery-fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static Process Start(string root, string command)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(PartialRecoveryProcessTests).Assembly.Location); start.ArgumentList.Add("partial-recovery-child");
        start.ArgumentList.Add(root); start.ArgumentList.Add(command); return Process.Start(start)!;
    }
    private static async Task Ready(string root, Process child)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(Path.Combine(root, "producer-ready")))
        { Assert.False(child.HasExited, "Producer exited before capture."); Assert.True(DateTime.UtcNow < deadline, "Producer capture timed out."); await Task.Delay(10); }
    }
    private static async Task Exit(Process process)
    {
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, $"Child failed: {process.ExitCode}; {await output}; {await error}");
    }
    private static RecoveryProcessObservation Read(string root, string command) => JsonSerializer.Deserialize<RecoveryProcessObservation>(File.ReadAllText(Path.Combine(root, command + "-result.json")))!;
    private static async Task<RecoveryProcessObservation> Run(string root, string command)
    { using var process = Start(root, command); await Exit(process); return Read(root, command); }
}

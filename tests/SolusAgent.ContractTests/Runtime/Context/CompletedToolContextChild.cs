using System.Diagnostics;
using System.Text.Json;
using CustomTools;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Context;

internal sealed record ToolTargets(Guid CounterId, Guid TransformId, long Total, int CounterEffects, int TransformEffects);
internal sealed record ToolProcessObservation(int Pid, ContextAdmission Admission, AgentTerminationReason? Reason, int ProviderEffects,
    int Imports, int ToolEntries, int CurrentInvoked, int HistoricalInvoked, long? HistoricalInput, long? CurrentInput,
    long ClockTicks, ToolTargets Targets, string? History, string? Fingerprint, string[] RawArguments, string?[] Results);

internal static class CompletedToolContextChild
{
    internal static async Task<int> Run(string[] args)
    {
        var directory = args[1]; var command = args[2]; Directory.CreateDirectory(directory);
        var producing = command.StartsWith("produce", StringComparison.Ordinal);
        var targetPath = Path.Combine(directory, "targets.json"); var selectedPath = Path.Combine(directory, "selected-targets.json");
        var targets = producing ? new ToolTargets(Guid.NewGuid(), Guid.NewGuid(), 0, 0, 0)
            : JsonSerializer.Deserialize<ToolTargets>(File.ReadAllText(targetPath))!;
        if (command == "changed-target") targets = targets with { CounterId = Guid.NewGuid(), Total = 0, CounterEffects = 0 };
        void Persist() => File.WriteAllText(targetPath, JsonSerializer.Serialize(targets));
        if (producing) Persist();
        var counter = new CounterCapability(initialTotal: targets.Total, initialEffects: targets.CounterEffects, observe: (total, effects) =>
        { targets = targets with { Total = total, CounterEffects = effects }; Persist(); File.AppendAllText(Path.Combine(directory, "tool-effects.txt"), $"counter:{Environment.ProcessId}\n"); });
        var transform = new TransformCapability(targets.TransformEffects, effects =>
        { targets = targets with { TransformEffects = effects }; Persist(); File.AppendAllText(Path.Combine(directory, "tool-effects.txt"), $"transform:{Environment.ProcessId}\n"); });
        var observedCounter = new ObservingTool(new CounterTool(maximumResultBytes: 100));
        var observedTransform = new ObservingTool(new TransformTool());
        RuntimeToolRegistration[] tools = [new(observedCounter, counter), new(observedTransform, transform)];
        var clock = new ControlledTimeProvider(); var provider = new PersistentProvider(tools: true);
        provider.Inspect = _ => { if (!producing && command.Contains("duration", StringComparison.Ordinal)) clock.Advance(TimeSpan.FromSeconds(25)); };
        ProviderResponse? original = null;
        provider.Respond = r => producing ? original = ToolContextFixture.Calls(r) : ToolContextFixture.Final(r);
        var request = ToolContextFixture.Request(limits: producing ? null : new(1, 1, 3, 2, 1, new(new(3, 2), 3, 2)));
        var options = new RuntimeOptions(timeProvider: clock, requireContinuation: true);
        ContextExecutionResult result;
        if (producing)
        {
            var sink = new RestrictedContextHost();
            result = await ToolContextFixture.Agent(provider, tools, options: options).ExecuteWithContextAsync(
                new(request, ContextExecutionIntent.Fresh), sink, new AfterBatch(() =>
                { if (command.Contains("duration", StringComparison.Ordinal)) clock.Advance(TimeSpan.FromSeconds(20)); }));
            if (result.CaptureStatus != ContextCaptureStatus.Delivered) return 3;
            FileContextAuthority.Save(directory, sink.CopyRestrictedContext(), result.Checkpoint!);
            File.WriteAllText(selectedPath, JsonSerializer.Serialize(targets));
            // The oracle uses actual producer calls/results; only model acceptance is replayed without dispatch.
            var accepted = ProviderResponse.RestoreToolCalls(provider.LastRequest!, original!);
            var expected = new ProviderRequest(provider.Scope, new(request.ExecutionId, Guid.NewGuid(), Guid.NewGuid(), 1),
                [ProviderInput.Instruction(request.Instructions), ProviderInput.FromModel(accepted),
                    ProviderInput.FromTool(observedCounter.Results.Single()), ProviderInput.FromTool(observedTransform.Results.Single())],
                tools.Select(t => t.Descriptor).ToArray(), accepted.Continuation,
                ProviderCapabilities.Continuation, history: new([accepted.Attempt]));
            File.WriteAllText(Path.Combine(directory, "actual-history.json"), ToolContextFixture.History(expected));
        }
        else
        {
            var envelope = FileContextAuthority.Read(directory); var selection = FileContextAuthority.Selection(directory);
            var grantPath = Path.Combine(directory, "tool-grant.json");
            var grant = command == "duplicate" ? JsonSerializer.Deserialize<ContextRoundGrant>(File.ReadAllText(grantPath))!
                : new ContextRoundGrant(Guid.NewGuid(), selection.Info, request.ExecutionId, Guid.NewGuid());
            if (command == "duplicate") request = new(grant.ExecutionId, request.Instructions, [], request.Bounds, request.RequiredCapabilities, request.UsageLimits);
            else File.WriteAllText(grantPath, JsonSerializer.Serialize(grant));
            var authority = new TargetAuthority(directory, targets);
            result = await ToolContextFixture.Agent(provider, tools, authority, options).ExecuteWithContextAsync(
                new(request, ContextExecutionIntent.ContinueRun, envelope, grant));
        }
        var history = producing ? File.ReadAllText(Path.Combine(directory, "actual-history.json"))
            : provider.LastRequest is { } next ? ToolContextFixture.History(next) : null;
        var model = provider.LastRequest?.Inputs.SingleOrDefault(i => i.Model is not null)?.Model;
        var observation = new ToolProcessObservation(Environment.ProcessId, result.Admission, result.Outcome?.Reason,
            provider.Effects, provider.Imports, observedCounter.Entries + observedTransform.Entries,
            result.Outcome?.Usage?.ToolInvocations?.Invoked ?? 0, result.History.Sum(r => r.Usage.ToolInvocations?.Invoked ?? 0),
            result.History.Count == 0 ? null : result.History.Sum(r => r.Usage.InputTokens.ObservedTokens), result.Outcome?.Usage?.InputTokens.ObservedTokens,
            clock.GetTimestamp(), targets, history, history is null ? null : ToolContextFixture.Fingerprint(history),
            producing ? original!.Calls.Select(c => c.ArgumentsJson).ToArray() : model?.Calls.Select(c => c.ArgumentsJson).ToArray() ?? [],
            producing ? new[] { observedCounter.Results.Single().Json, observedTransform.Results.Single().Json }
                : provider.LastRequest?.Inputs.Where(i => i.ToolResult is not null).Select(i => i.ToolResult!.Json).ToArray() ?? []);
        File.WriteAllText(Path.Combine(directory, command + "-result.json"), JsonSerializer.Serialize(observation));
        return 0;
    }
    private sealed class TargetAuthority(string directory, ToolTargets actual) : IRuntimeContextAuthority
    {
        private readonly FileContextAuthority claims = new(directory);
        public bool TryClaim(AgentContextEnvelope context, ContextRoundGrant grant) =>
            JsonSerializer.Deserialize<ToolTargets>(File.ReadAllText(Path.Combine(directory, "selected-targets.json"))) == actual
            && claims.TryClaim(context, grant);
    }
    private sealed class AfterBatch(Action callback) : IProgress<AgentProgress>
    { public void Report(AgentProgress value) => callback(); }
    private sealed class ObservingTool(IFunctionTool inner) : IFunctionTool
    {
        internal int Entries { get; private set; }
        internal List<ToolResult> Results { get; } = [];
        public ToolDescriptor Descriptor => inner.Descriptor;
        public ToolPreparation Prepare(ToolCall call) => inner.Prepare(call);
        public ToolError ValidateInvocation(PreparedToolInvocation prepared, ToolCall call, IToolCapability? capability) => inner.ValidateInvocation(prepared, call, capability);
        public async ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation prepared, ToolCall call, IToolCapability? capability, CancellationToken token = default)
        { Entries++; var result = await inner.InvokeAsync(prepared, call, capability, token); Results.Add(result); return result; }
    }
}

public sealed class CompletedToolProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualToolsCrossProcessWithoutRepeatingCompletedEffects(bool duration)
    {
        var root = Root(); var first = await Run(root, duration ? "produce-duration" : "produce");
        var second = await Run(root, duration ? "resume-duration" : "resume");
        Assert.NotEqual(first.Pid, second.Pid); Assert.Equal(ContextAdmission.Supplied, second.Admission);
        Assert.Equal(AgentTerminationReason.Completed, second.Reason); Assert.Equal(1, first.ProviderEffects); Assert.Equal(1, second.ProviderEffects);
        Assert.Equal(2, first.ToolEntries); Assert.Equal(0, second.ToolEntries); Assert.Equal(0, second.CurrentInvoked); Assert.Equal(2, second.HistoricalInvoked);
        Assert.Equal(3, first.CurrentInput); Assert.Equal(3, second.CurrentInput); Assert.Equal(3, second.HistoricalInput);
        Assert.Equal(first.Targets, second.Targets); Assert.Equal(7, second.Targets.Total);
        Assert.Equal(1, second.Targets.CounterEffects); Assert.Equal(1, second.Targets.TransformEffects);
        Assert.Equal(first.History, second.History); Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.RawArguments, second.RawArguments); Assert.Equal(first.Results, second.Results);
        Assert.Equal("{ \"amount\":7 }", second.RawArguments[0]); Assert.Equal("{\"total\":7}", second.Results[0]);
        Assert.Contains(ToolContextFixture.Arguments, second.RawArguments[1]); Assert.Contains(ToolContextFixture.Arguments, second.Results[1]!);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(root, "tool-effects.txt")).Length);
        if (duration) { Assert.Equal(TimeSpan.FromSeconds(20).Ticks, first.ClockTicks); Assert.Equal(TimeSpan.FromSeconds(25).Ticks, second.ClockTicks); }
        var duplicate = await Run(root, "duplicate");
        Assert.Equal(ContextAdmission.Rejected, duplicate.Admission); Assert.Equal(0, duplicate.ProviderEffects); Assert.Equal(0, duplicate.ToolEntries);
    }
    [Fact]
    public async Task SameDescriptorsWithDifferentActualTargetRejectBeforeImportOrEffects()
    {
        var root = Root(); await Run(root, "produce"); var changed = await Run(root, "changed-target");
        Assert.Equal(ContextAdmission.Rejected, changed.Admission); Assert.Equal(0, changed.Imports); Assert.Equal(0, changed.ProviderEffects); Assert.Equal(0, changed.ToolEntries);
        Assert.Empty(Directory.GetFiles(root, "*.claim")); Assert.Equal(2, File.ReadAllLines(Path.Combine(root, "tool-effects.txt")).Length);
    }
    private static string Root() => Path.Combine(AppContext.BaseDirectory, "tool-context-fixtures", Guid.NewGuid().ToString("N"));
    private static async Task<ToolProcessObservation> Run(string root, string command)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(CompletedToolProcessTests).Assembly.Location); start.ArgumentList.Add("tool-context-child"); start.ArgumentList.Add(root); start.ArgumentList.Add(command);
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, $"Tool child failed: {process.ExitCode}; {await output}; {await error}");
        return JsonSerializer.Deserialize<ToolProcessObservation>(File.ReadAllText(Path.Combine(root, command + "-result.json")))!;
    }
}

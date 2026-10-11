using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Context;

internal sealed record ChildObservation(int Pid, ContextAdmission Admission, ContextCaptureStatus Capture, AgentTerminationReason? Reason,
    int ProviderEffects, int HistoricalAttempts, long? HistoricalInput, long? CurrentInput, int? Ordinal, Guid? LogicalCall,
    Guid? PreviousAttempt, long ClockTicks, string[] InputKinds, Guid? PhysicalAttempt, string? RequestFingerprint);

internal static class OrdinaryContextChild
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "tool-context-child") return await CompletedToolContextChild.Run(args);
        if (args.Length != 3 || args[0] != "ordinary-context-child") return 2;
        var directory = args[1]; var command = args[2];
        Directory.CreateDirectory(directory);
        var clock = new ControlledTimeProvider();
        var producing = command.StartsWith("produce", StringComparison.Ordinal);
        var provider = new PersistentProvider(producing);
        provider.Inspect = _ =>
        {
            File.AppendAllText(Path.Combine(directory, "effect-journal.txt"), $"provider:{Environment.ProcessId}\n");
            if (command.Contains("duration", StringComparison.Ordinal)) clock.Advance(TimeSpan.FromSeconds(producing ? 20 : 25));
        };
        var request = OrdinaryFixture.Request();
        var options = new RuntimeOptions(timeProvider: clock, requireContinuation: true);
        ContextExecutionResult result;
        if (producing)
        {
            var sink = new RestrictedContextHost();
            result = await OrdinaryFixture.Agent(provider, options: options).ExecuteWithContextAsync(new(request, ContextExecutionIntent.Fresh), sink);
            if (result.CaptureStatus != ContextCaptureStatus.Delivered) return 3;
            FileContextAuthority.Save(directory, sink.CopyRestrictedContext(), result.Checkpoint!);
        }
        else
        {
            var envelope = FileContextAuthority.Read(directory);
            var selection = FileContextAuthority.Selection(directory);
            var grantFile = Path.Combine(directory, "grant.json");
            ContextRoundGrant grant;
            if (command.StartsWith("duplicate", StringComparison.Ordinal) || command == "competing")
            {
                grant = JsonSerializer.Deserialize<ContextRoundGrant>(File.ReadAllText(grantFile))!;
                if (command == "competing") grant = new(Guid.NewGuid(), grant.Source, Guid.NewGuid(), Guid.NewGuid());
                request = OrdinaryFixture.Request(grant.ExecutionId);
            }
            else
            {
                grant = new(Guid.NewGuid(), selection.Info, request.ExecutionId, Guid.NewGuid());
                if (command == "binding") grant = new(Guid.NewGuid(), selection.Info with { RoundId = Guid.NewGuid() }, request.ExecutionId, Guid.NewGuid());
                if (command == "tamper")
                {
                    var document = JsonNode.Parse(envelope.CopyRestrictedPayload())!;
                    document["Records"]![0]!["Text"] = "CHANGED_BUT_STRUCTURALLY_VALID";
                    envelope = new(envelope.ImplementationId, 1, 1, JsonSerializer.SerializeToUtf8Bytes(document));
                    // An attacker can compute this matching hash, but the trusted-selection file is unchanged.
                    File.WriteAllBytes(Path.Combine(directory, "untrusted-digest.bin"), OrdinaryFixture.Fingerprint(envelope));
                    request = OrdinaryFixture.Request(request.ExecutionId, instructions: "CHANGED_BUT_STRUCTURALLY_VALID");
                }
                File.WriteAllText(grantFile, JsonSerializer.Serialize(grant));
            }
            if (command.StartsWith("pause", StringComparison.Ordinal))
            {
                request = OrdinaryFixture.Request(request.ExecutionId, limits: new(1, 1, 3, 2, 1, new(new(3, 2), 3, 2), new(2, backoff: TimeSpan.FromSeconds(5))));
                var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                clock.TimerCreated = delay => { if (delay == TimeSpan.FromSeconds(5)) waiting.TrySetResult(); };
                using var cancellation = new CancellationTokenSource(); var sink = new RestrictedContextHost();
                var execution = OrdinaryFixture.Agent(provider, new FileContextAuthority(directory), options: options).ExecuteWithContextAsync(
                    new(request, ContextExecutionIntent.ContinueRun, envelope, grant), sink, cancellationToken: cancellation.Token).AsTask();
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (command == "pause-duration") clock.Advance(TimeSpan.FromSeconds(30)); else cancellation.Cancel();
                result = await execution;
                if (result.CaptureStatus != ContextCaptureStatus.Delivered) return 4;
                FileContextAuthority.Save(directory, sink.CopyRestrictedContext(), result.Checkpoint!);
            }
            else result = await OrdinaryFixture.Agent(provider, new FileContextAuthority(directory), options: options).ExecuteWithContextAsync(
                new(request, ContextExecutionIntent.ContinueRun, envelope, grant));
        }
        var current = result.Outcome?.Usage?.Attempts.LastOrDefault();
        var observed = new ChildObservation(Environment.ProcessId, result.Admission, result.CaptureStatus, result.Outcome?.Reason,
            provider.Effects, result.History.Sum(r => r.Usage.Attempts.Count),
            result.History.Count != 0 && result.History.All(r => r.Usage.InputTokens.ObservedTokens.HasValue)
                ? result.History.Sum(r => r.Usage.InputTokens.ObservedTokens!.Value) : null,
            result.Outcome?.Usage?.InputTokens.ObservedTokens, current?.AttemptNumber, current?.LogicalCallId,
            provider.LastRequest?.History?.OperationOrigin?.PhysicalAttemptId, clock.GetTimestamp(),
            provider.LastRequest?.Inputs.Select(i => i.Kind.ToString()).ToArray() ?? [], current?.PhysicalAttemptId,
            provider.LastRequest is { } next ? Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            {
                next.Scope, next.Bounds, next.RequiredCapabilities, next.Tools,
                Inputs = next.Inputs.Select(i => new { i.Kind, i.Text, ModelOrigin = i.Model?.Attempt }).ToArray(),
                Continuation = next.Continuation?.CopyReplayBytes()
            }))) : null);
        File.WriteAllText(Path.Combine(directory, command + "-result.json"), JsonSerializer.Serialize(observed));
        return 0;
    }
}

public sealed class OrdinaryProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealProcessRestartGetsFullAllowanceWithoutReplayingCompletedWork(bool duration)
    {
        var root = Root();
        var first = await Run(root, duration ? "produce-duration" : "produce");
        Assert.Equal(ContextAdmission.Fresh, first.Admission); Assert.Equal(AgentTerminationReason.ResourceLimit, first.Reason);
        Assert.Equal(3, first.CurrentInput); Assert.Equal(1, first.ProviderEffects);
        var second = await Run(root, duration ? "resume-duration" : "resume");
        Assert.NotEqual(first.Pid, second.Pid);
        Assert.Equal(ContextAdmission.Supplied, second.Admission); Assert.Equal(AgentTerminationReason.Completed, second.Reason);
        Assert.Equal(1, second.HistoricalAttempts); Assert.Equal(3, second.HistoricalInput); Assert.Equal(3, second.CurrentInput);
        Assert.Equal(first.LogicalCall, second.LogicalCall); Assert.Equal(2, second.Ordinal); Assert.NotNull(second.PreviousAttempt);
        Assert.Equal(first.PhysicalAttempt, second.PreviousAttempt); Assert.Equal(first.RequestFingerprint, second.RequestFingerprint);
        Assert.Equal(new[] { "HostInstruction" }, second.InputKinds);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(root, "effect-journal.txt")).Length);
        if (duration) { Assert.Equal(TimeSpan.FromSeconds(20).Ticks, first.ClockTicks); Assert.Equal(TimeSpan.FromSeconds(25).Ticks, second.ClockTicks); }
        var duplicate = await Run(root, "duplicate");
        Assert.NotEqual(second.Pid, duplicate.Pid); Assert.Equal(ContextAdmission.Rejected, duplicate.Admission);
        Assert.Equal(0, duplicate.ProviderEffects); Assert.Null(duplicate.Reason);
        var competing = await Run(root, "competing");
        Assert.Equal(ContextAdmission.Rejected, competing.Admission); Assert.Equal(0, competing.ProviderEffects);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(root, "effect-journal.txt")).Length);
    }
    [Theory]
    [InlineData("pause-cancel")]
    [InlineData("pause-duration")]
    public async Task ThreeRealProcessesContinueAfterMiddleRoundStopsWithoutAttempt(string command)
    {
        var root = Root(); var first = await Run(root, "produce"); var middle = await Run(root, command); var last = await Run(root, "resume");
        Assert.NotEqual(first.Pid, middle.Pid); Assert.NotEqual(middle.Pid, last.Pid);
        Assert.Equal(ContextCaptureStatus.Delivered, middle.Capture); Assert.Equal(0, middle.ProviderEffects); Assert.Equal(0, middle.CurrentInput);
        Assert.Equal(command == "pause-duration" ? AgentTerminationReason.ResourceLimit : AgentTerminationReason.Cancelled, middle.Reason);
        Assert.Equal(AgentTerminationReason.Completed, last.Reason); Assert.Equal(1, last.ProviderEffects); Assert.Equal(2, last.Ordinal);
        Assert.Equal(first.PhysicalAttempt, last.PreviousAttempt); Assert.Equal(first.LogicalCall, last.LogicalCall);
        Assert.Equal(first.RequestFingerprint, last.RequestFingerprint); Assert.Equal(1, last.HistoricalAttempts); Assert.Equal(3, last.HistoricalInput);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(root, "effect-journal.txt")).Length);
    }
    [Theory]
    [InlineData("tamper")]
    [InlineData("binding")]
    public async Task RealHostBoundaryRejectsAlteredBytesAndInconsistentSource(string command)
    {
        var root = Root(); await Run(root, "produce");
        var result = await Run(root, command);
        Assert.Equal(ContextAdmission.Rejected, result.Admission); Assert.Equal(0, result.ProviderEffects);
        Assert.Single(File.ReadAllLines(Path.Combine(root, "effect-journal.txt")));
        Assert.Empty(Directory.GetFiles(root, "*.claim"));
    }
    private static string Root() => Path.Combine(AppContext.BaseDirectory, "context-fixtures", Guid.NewGuid().ToString("N"));
    private static async Task<ChildObservation> Run(string root, string command)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(OrdinaryProcessTests).Assembly.Location);
        start.ArgumentList.Add("ordinary-context-child"); start.ArgumentList.Add(root); start.ArgumentList.Add(command);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, $"Child failed: {process.ExitCode}; {await output}; {await error}");
        return JsonSerializer.Deserialize<ChildObservation>(File.ReadAllText(Path.Combine(root, command + "-result.json")))!;
    }
}

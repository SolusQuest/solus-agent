using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using System.Security.Cryptography;
using AprHost;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Candidates;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Api.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace SolusAgent.ContractTests.Runtime.Consumption;

public sealed class M4AprFreshProcessTests(ITestOutputHelper output)
{
    private const string StageVariable = "SOLUS_APR_CHILD_STAGE";
    private const string DirectoryVariable = "SOLUS_APR_CHILD_DIRECTORY";
    internal sealed record Observation(string Stage, int Pid, ContextAdmission Admission, int Imports, int Dispatches,
        int Submissions, int Accepted, int Effects, int NewEffects, int HistoricalReceipts, int HistoricalAttempts,
        Guid? Source, int Claims, long CurrentCharge = 0, long HistoricalCharge = 0, long? MeasuredInput = null,
        long ClockTicks = 0, long[]? RunTimers = null, CandidateStopReason? Stop = null, bool HistoricalInputUnknown = false,
        Guid? Round = null, Guid[]? AcceptedIds = null, Guid[]? HistoricalIds = null, Guid[]? SubmittedIds = null);

    [Fact]
    public async Task SameCandidateCheckpointRecoversFixedSnapshotReadWhileOriginalInvocationIsOutstanding()
    {
        var directory = NewDirectory(); var running = Start(directory, "mixed-source-read");
        Observation resumed; Observation duplicate; Observation? source = null;
        try
        {
            await AprMixedProcessFixture.WaitFile(Path.Combine(directory, "source-ready"));
            var original = ReadMixed(directory, "mixed-source-read");
            Assert.Equal(1, original.CounterEffects); Assert.Equal(0, original.ReadMutations);
            Assert.Equal(1, original.Accepted); Assert.Equal(1, original.Effects);
            Assert.Equal(2, original.Dispatches); Assert.Equal(2, original.ToolCharges);
            resumed = await Run(directory, "mixed-resume"); duplicate = await Run(directory, "mixed-duplicate");
            Assert.Equal(ContextAdmission.Supplied, resumed.Admission);
            var recovered = ReadMixed(directory, "mixed-resume");
            Assert.Equal(1, recovered.Claims); Assert.Equal(1, recovered.Imports); Assert.Equal(1, recovered.Dispatches);
            Assert.Equal(0, recovered.OriginalEntries); Assert.Equal(1, recovered.Queries);
            Assert.Equal(1, recovered.CounterEntries);
            Assert.Equal(2, recovered.CounterEffects); Assert.Equal(8, recovered.CounterTotal); Assert.Equal(0, recovered.ReadMutations);
            Assert.Equal(2, recovered.ToolCharges); Assert.Equal(2, recovered.HistoricalToolCharges);
            Assert.True(recovered.CompleteBatch); Assert.True(recovered.PriorHostCommitted);
            Assert.Equal(original.OriginalSubmission, recovered.OriginalSubmission);
            Assert.Equal(1, recovered.HistoricalReceipts); Assert.Equal(1, recovered.Submissions);
            Assert.Equal(2, recovered.Accepted); Assert.Equal(2, recovered.Effects); Assert.Equal(1, recovered.NewEffects);
            Assert.Equal(ContextAdmission.Rejected, duplicate.Admission);
            var denied = ReadMixed(directory, "mixed-duplicate");
            Assert.Equal(1, denied.Claims); Assert.Equal(0, denied.Imports); Assert.Equal(0, denied.Dispatches);
            Assert.Equal(0, denied.OriginalEntries); Assert.Equal(0, denied.Queries); Assert.Equal(0, denied.CounterEntries);
            Assert.Equal(0, denied.Submissions); Assert.Equal(0, denied.NewEffects);
        }
        finally { File.WriteAllText(Path.Combine(directory, "release-original"), "release"); source = await Finish(running); }
        Assert.Equal(3, new[] { source.Pid, resumed.Pid, duplicate.Pid }.Distinct().Count());
        Assert.True(ReadMixed(directory, "mixed-source-read").FrozenAfterLateReturn);
    }

    [Theory]
    [InlineData("mutation", "resume")]
    [InlineData("metadata", "resume")]
    [InlineData("read", "wrong-target")]
    [InlineData("read", "wrong-authority")]
    [InlineData("read", "wrong-tool")]
    [InlineData("read", "insufficient")]
    public async Task MixedRecoveryDeniesUnsafeOrMismatchedContinuationBeforeNewEffects(string sourceKind, string transition)
    {
        var directory = NewDirectory(); var sourceStage = "mixed-source-" + sourceKind;
        var running = Start(directory, sourceStage);
        try
        {
            await AprMixedProcessFixture.WaitFile(Path.Combine(directory, "source-ready"));
            var denied = await Run(directory, "mixed-" + transition);
            Assert.Equal(transition == "insufficient" ? ContextAdmission.Supplied : ContextAdmission.Rejected, denied.Admission);
            var observation = ReadMixed(directory, "mixed-" + transition);
            Assert.Equal(transition == "insufficient" ? 1 : 0, observation.Imports);
            Assert.Equal(0, observation.Dispatches); Assert.Equal(0, observation.CounterEntries);
            Assert.Equal(0, observation.OriginalEntries); Assert.Equal(0, observation.Queries); Assert.Equal(0, observation.Submissions);
            Assert.Equal(0, observation.NewEffects); Assert.Equal(1, observation.CounterEffects); Assert.Equal(1, observation.Effects);
            Assert.Equal(sourceKind == "mutation" ? 1 : 0, observation.ReadMutations);
            Assert.Equal(transition is "wrong-authority" or "insufficient" ? 1 : 0, observation.Claims);
            if (transition == "insufficient")
            {
                Assert.Equal(CandidateStopReason.RuntimeLimit, denied.Stop);
                Assert.Equal(0, observation.ToolCharges);
            }
        }
        finally { File.WriteAllText(Path.Combine(directory, "release-original"), "release"); await Finish(running); }
        Assert.True(ReadMixed(directory, sourceStage).FrozenAfterLateReturn);
    }

    private static AprMixedProcessFixture.MixedObservation ReadMixed(string directory, string stage) =>
        JsonSerializer.Deserialize<AprMixedProcessFixture.MixedObservation>(File.ReadAllText(Path.Combine(directory, stage + "-mixed.json")))!;

    [Theory]
    [InlineData("wrong-input")]
    [InlineData("wrong-target")]
    [InlineData("wrong-authority")]
    public async Task MismatchedFailedSourceBlocksBeforeClaimImportDispatchAndHostEffects(string stage)
    {
        var directory = NewDirectory();
        var source = await Run(directory, "source-failed");
        var denied = await Run(directory, stage);
        Assert.NotEqual(source.Pid, denied.Pid);
        Assert.Equal(ContextAdmission.Rejected, denied.Admission);
        Assert.Equal(0, denied.Claims); Assert.Equal(0, denied.Imports); Assert.Equal(0, denied.Dispatches);
        Assert.Equal(0, denied.Submissions); Assert.Equal(0, denied.Accepted); Assert.Equal(0, denied.NewEffects);
        Assert.Single(File.ReadAllLines(Path.Combine(directory, "provider-entries.txt")));
    }

    [Fact]
    public async Task AccountingRoundRenewsFullAllowanceAndKeepsUnknownHistoricalMeasurement()
    {
        var directory = NewDirectory();
        var first = await Run(directory, "round-accounting-source");
        var second = await Run(directory, "round-accounting-resume");
        Assert.NotEqual(first.Pid, second.Pid);
        Assert.Equal(1, first.Dispatches); Assert.Equal(100000, first.CurrentCharge); Assert.Null(first.MeasuredInput);
        Assert.Equal(ContextAdmission.Supplied, second.Admission);
        Assert.Equal(1, second.Claims);
        Assert.Equal(1, second.Dispatches); Assert.Equal(100000, second.CurrentCharge);
        Assert.Equal(100000, second.HistoricalCharge); Assert.Null(second.MeasuredInput);
        Assert.True(second.HistoricalInputUnknown); Assert.NotEqual(first.Round, second.Round);
        Assert.Equal(1, second.Submissions); Assert.Equal(1, second.HistoricalAttempts);
        var neighbor = await Run(NewDirectory(), "round-accounting-retry");
        Assert.Equal(1, neighbor.Dispatches); Assert.Equal(60000, neighbor.CurrentCharge);
        Assert.Equal(CandidateStopReason.RuntimeLimit, neighbor.Stop);
    }

    [Fact]
    public async Task CancelledTwentyMinuteRoundRenewsThirtyMinutesWhilePhysicalRetryKeepsOriginalDeadline()
    {
        var directory = NewDirectory();
        var first = await Run(directory, "round-time-source");
        var second = await Run(directory, "round-time-resume");
        Assert.NotEqual(first.Pid, second.Pid);
        Assert.Equal(TimeSpan.FromMinutes(20).Ticks, first.ClockTicks);
        Assert.Equal(CandidateStopReason.Cancelled, first.Stop);
        Assert.NotEqual(first.Round, second.Round);
        Assert.Equal(ContextAdmission.Supplied, second.Admission);
        Assert.Equal(1, second.Claims);
        Assert.Equal(TimeSpan.FromMinutes(25).Ticks, second.ClockTicks);
        Assert.Equal(CandidateStopReason.Completed, second.Stop); Assert.Equal(1, second.Submissions);
        Assert.Contains(TimeSpan.FromMinutes(30).Ticks, first.RunTimers!);
        Assert.Contains(TimeSpan.FromMinutes(30).Ticks, second.RunTimers!);
        var retry = await Run(NewDirectory(), "round-time-retry");
        Assert.Equal(2, retry.Dispatches); Assert.Equal(TimeSpan.FromMinutes(31).Ticks, retry.ClockTicks);
        Assert.Equal(CandidateStopReason.DurationLimit, retry.Stop);
        Assert.Single(retry.RunTimers!, t => t == TimeSpan.FromMinutes(30).Ticks);
    }

    [Fact]
    public async Task UnknownHostReceiptAdmitsItsHistoryButBlocksNewDispatchSubmissionAndBusinessEffects()
    {
        var directory = NewDirectory();
        var first = await Run(directory, "source-unknown");
        var second = await Run(directory, "resume-unknown");
        Assert.NotEqual(first.Pid, second.Pid);
        Assert.Equal(1, first.Submissions); Assert.Equal(0, first.Accepted); Assert.Equal(0, first.NewEffects);
        Assert.Equal(CandidateStopReason.UnknownAcknowledgement, first.Stop);
        Assert.Equal(ContextAdmission.Supplied, second.Admission); Assert.Equal(1, second.Claims); Assert.Equal(1, second.Imports);
        Assert.Equal(CandidateStopReason.UnknownAcknowledgement, second.Stop);
        Assert.Equal(1, second.HistoricalReceipts); Assert.Equal(0, second.Dispatches);
        Assert.Equal(0, second.Submissions); Assert.Equal(0, second.Accepted); Assert.Equal(0, second.NewEffects);
        Assert.Single(File.ReadAllLines(Path.Combine(directory, "provider-entries.txt")));
    }

    [Fact]
    public async Task FailedCheckpointGrantAndAcceptedBusinessJournalSurviveIndependentProcesses()
    {
        var directory = NewDirectory();
        var source = await Run(directory, "source-failed");
        var resumed = await Run(directory, "resume");
        var duplicate = await Run(directory, "duplicate");
        Assert.Equal(3, new[] { source.Pid, resumed.Pid, duplicate.Pid }.Distinct().Count());
        Assert.Equal(ContextAdmission.Fresh, source.Admission);
        Assert.Equal(1, source.Dispatches); Assert.Equal(0, source.Submissions);
        Assert.Equal(ContextAdmission.Supplied, resumed.Admission);
        Assert.Equal(1, resumed.Imports); Assert.Equal(1, resumed.Dispatches); Assert.Equal(1, resumed.Submissions);
        Assert.Equal(1, resumed.Accepted); Assert.Equal(1, resumed.NewEffects); Assert.Equal(1, resumed.HistoricalAttempts);
        Assert.Equal(ContextAdmission.Rejected, duplicate.Admission);
        Assert.Equal(1, duplicate.Claims); Assert.Equal(0, duplicate.Imports);
        Assert.Equal(0, duplicate.Dispatches); Assert.Equal(0, duplicate.Submissions); Assert.Equal(0, duplicate.NewEffects);
        Assert.Equal(1, duplicate.Accepted); Assert.Equal(1, duplicate.Effects);
        Assert.Equal(resumed.Source, duplicate.Source);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(directory, "provider-entries.txt")).Length);
    }

    [Fact]
    public async Task NewHeadSelectsPriorAcceptedContextAndKeepsPriorHostEffectWithoutResubmission()
    {
        var directory = NewDirectory();
        var accepted = await Run(directory, "source-accepted");
        var failed = await Run(directory, "source-failed");
        var renewed = await Run(directory, "new-head");
        Assert.NotEqual(accepted.Pid, renewed.Pid); Assert.NotEqual(failed.Pid, renewed.Pid);
        Assert.Equal(1, accepted.NewEffects); Assert.Equal(0, failed.NewEffects);
        Assert.Equal(ContextAdmission.Supplied, renewed.Admission);
        Assert.Equal(accepted.Source, renewed.Source); Assert.NotEqual(failed.Source, renewed.Source);
        Assert.Equal(1, renewed.HistoricalReceipts); Assert.Equal(1, renewed.Submissions);
        var original = Assert.Single(accepted.AcceptedIds!);
        Assert.Contains(original, renewed.AcceptedIds!); Assert.Equal(original, Assert.Single(renewed.HistoricalIds!));
        Assert.NotEqual(original, Assert.Single(renewed.SubmittedIds!));
        Assert.Equal(2, renewed.Accepted); Assert.Equal(2, renewed.Effects); Assert.Equal(1, renewed.NewEffects);
        Assert.True(File.Exists(Path.Combine(directory, "source-accepted", "checkpoint.json")));
        Assert.True(File.Exists(Path.Combine(directory, "source-failed", "checkpoint.json")));
    }

    // This selected test runs in a separate testhost. In the ordinary full runner it performs no child stage.
    [Fact]
    public async Task ChildStage()
    {
        var stage = Environment.GetEnvironmentVariable(StageVariable);
        if (stage is null) return;
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable) ?? throw new InvalidOperationException("Missing APR child directory.");
        var result = stage.StartsWith("mixed-", StringComparison.Ordinal) ? await AprMixedProcessFixture.Execute(directory, stage)
            : stage.StartsWith("round-", StringComparison.Ordinal) ? await AprRoundProcessFixture.Execute(directory, stage)
            : await AprFreshProcessFixture.Execute(directory, stage);
        File.WriteAllText(Path.Combine(directory, stage + "-observation.json"), JsonSerializer.Serialize(result));
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "apr-process", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private async Task<Observation> Run(string directory, string stage)
        => await Finish(Start(directory, stage));

    private sealed record ChildRun(Process Process, Task<string> Output, Task<string> Error, string Directory, string Stage);

    private static ChildRun Start(string directory, string stage)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add("vstest"); info.ArgumentList.Add(typeof(M4AprFreshProcessTests).Assembly.Location);
        info.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=SolusAgent.ContractTests.Runtime.Consumption.M4AprFreshProcessTests.ChildStage");
        info.ArgumentList.Add("/Logger:trx;LogFileName=" + stage + ".trx"); info.ArgumentList.Add("/ResultsDirectory:" + directory);
        info.Environment[StageVariable] = stage; info.Environment[DirectoryVariable] = directory;
        var process = Process.Start(info) ?? throw new InvalidOperationException("APR child did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        return new(process, standardOutput, error, directory, stage);
    }

    private async Task<Observation> Finish(ChildRun child)
    {
        using var process = child.Process; var directory = child.Directory; var stage = child.Stage;
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, (await child.Output) + (await child.Error));
        var counters = XDocument.Load(Path.Combine(directory, stage + ".trx")).Descendants().Single(e => e.Name.LocalName == "Counters");
        Assert.Equal("1", counters.Attribute("executed")!.Value); Assert.Equal("1", counters.Attribute("passed")!.Value);
        var observation = JsonSerializer.Deserialize<Observation>(File.ReadAllText(Path.Combine(directory, stage + "-observation.json")))!;
        Assert.Equal(stage, observation.Stage); Assert.NotEqual(Environment.ProcessId, observation.Pid);
        output.WriteLine(JsonSerializer.Serialize(observation));
        if (stage.StartsWith("mixed-", StringComparison.Ordinal)) output.WriteLine("Mixed: " + JsonSerializer.Serialize(ReadMixed(directory, stage)));
        return observation;
    }
}

internal static class AprFreshProcessFixture
{
    internal static async Task<M4AprFreshProcessTests.Observation> Execute(string directory, string stage)
    {
        var source = stage.StartsWith("source-", StringComparison.Ordinal);
        var provider = new PersistentProvider(stage == "source-failed")
        {
            Inspect = _ => File.AppendAllText(Path.Combine(directory, "provider-entries.txt"), $"{stage}:{Environment.ProcessId}\n"),
            Respond = r => new(r.Scope, r.Attempt, ProviderFinish.Final, new AprItem("alpha", 7).ToPayload(), [],
                new(r.Scope, r.Attempt, "APR_PERSISTED_REPLAY"u8.ToArray())),
        };
        var authorization = Path.Combine(directory, "authorization");
        var authority = source ? null : new CountingAuthority(authorization);
        var agent = CandidateContextFixture.Agent(provider, authority);
        var host = new AprBusinessHost((IAgent)agent, contextCandidateAgent: agent,
            acceptance: new(Path.Combine(directory, "business")));
        var submissions = 0; var submittedIds = new List<Guid>();
        var channel = new ScriptedCandidateHost((s, _) =>
        {
            submissions++;
            submittedIds.Add(s.SubmissionId);
            if (stage == "source-unknown") return ValueTask.FromResult<CandidateFeedback?>(
                new(s.ExecutionId, s.SubmissionId, CandidateAcknowledgement.Unknown));
            return ValueTask.FromResult<CandidateFeedback?>(new(s.ExecutionId, s.SubmissionId,
                CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept, CandidateContinuation.End));
        });
        var request = CandidateContextFixture.Request(limits: new(maximumLogicalCalls: 4,
            maximumPhysicalDispatches: stage == "source-failed" ? 1 : 4, retryPolicy: new(2)));
        ContextExecutionRequest context;
        Guid? selectedSource = null;
        if (source) context = new(request, ContextExecutionIntent.Fresh);
        else
        {
            var grantPath = Path.Combine(directory, "grant.json");
            ContextRoundGrant grant;
            if (stage == "duplicate")
            {
                grant = JsonSerializer.Deserialize<ContextRoundGrant>(File.ReadAllText(grantPath))!;
                request = CandidateContextFixture.Request(id: grant.ExecutionId, limits: request.UsageLimits);
            }
            else
            {
                if (stage == "new-head") request = new(request.ExecutionId, "NEW_HEAD_INSTRUCTIONS",
                    [new(AgentInputSource.Repository, "NEW_HEAD_INPUT")], request.Bounds, request.RequiredCapabilities, request.UsageLimits);
                if (stage == "wrong-input") request = new(request.ExecutionId, "UNRELATED_INSTRUCTIONS", request.Data,
                    request.Bounds, request.RequiredCapabilities, request.UsageLimits);
                var recordPaths = Directory.GetFiles(directory, "source-*.record.json");
                var records = recordPaths.Select(p =>
                    JsonSerializer.Deserialize<AprCheckpointRecord>(File.ReadAllText(p))!).ToArray();
                var binding = new AprCheckpointBinding(stage == "new-head" ? "n+1" : "n",
                    InputDigest(request),
                    stage == "wrong-target" ? "other" : "snapshot", stage == "wrong-authority" ? "other" : "authority");
                AprCheckpointRecord selected;
                try { selected = AprCheckpointSelector.Select(records, binding, stage == "new-head"); }
                catch (InvalidOperationException) when (stage is "wrong-input" or "wrong-target" or "wrong-authority")
                {
                    return new(stage, Environment.ProcessId, ContextAdmission.Rejected, provider.Imports, provider.Effects, submissions,
                        host.Acceptance.AcceptedCount, host.Acceptance.EffectCount, 0, 0, 0, null, authority?.Claims ?? 0);
                }
                var recordPath = recordPaths.Single(p => JsonSerializer.Deserialize<AprCheckpointRecord>(File.ReadAllText(p))!.Source == selected.Source);
                var saved = Path.Combine(directory, Path.GetFileName(recordPath).Replace(".record.json", "", StringComparison.Ordinal));
                FileContextAuthority.Save(authorization, FileContextAuthority.Read(saved), selected.Source);
                grant = new(Guid.NewGuid(), selected.Source, request.ExecutionId, Guid.NewGuid());
                File.WriteAllText(grantPath, JsonSerializer.Serialize(grant));
            }
            selectedSource = grant.Source.CheckpointId;
            context = new(request, stage == "new-head" ? ContextExecutionIntent.NewRunFromContext : ContextExecutionIntent.ContinueRun,
                FileContextAuthority.Read(authorization), grant);
        }
        var sink = source ? new RestrictedContextHost() : null;
        var result = await host.ExecuteCandidatesWithContextAsync(context, new(3, 2, 2), channel, sink);
        var newEffects = result.Candidates is null ? 0 : host.Acceptance.ApplyEffects();
        if (source)
        {
            Assert.Equal(ContextCaptureStatus.Delivered, result.Context.CaptureStatus);
            FileContextAuthority.Save(Path.Combine(directory, stage), sink!.CopyRestrictedContext(), result.Context.Checkpoint!);
            var record = new AprCheckpointRecord(result.Context.Checkpoint!, new(stage == "source-accepted" ? "n-1" : "n",
                InputDigest(request), "snapshot", "authority"), stage == "source-accepted", stage == "source-accepted" ? 1 : 2);
            File.WriteAllText(Path.Combine(directory, stage + ".record.json"), JsonSerializer.Serialize(record));
            selectedSource = record.Source.CheckpointId;
        }
        return new(stage, Environment.ProcessId, result.Context.Admission, provider.Imports, provider.Effects,
            submissions, host.Acceptance.AcceptedCount, host.Acceptance.EffectCount, newEffects,
            result.Candidates?.HistoricalReceipts.Count ?? 0, result.Context.History.Sum(h => h.Usage.Attempts.Count), selectedSource, authority?.Claims ?? 0,
            Stop: result.Candidates?.StopReason,
            AcceptedIds: host.Acceptance.Accepted.Select(r => r.SubmissionId).ToArray(),
            HistoricalIds: result.Candidates?.HistoricalReceipts.Select(r => r.SubmissionId).ToArray(), SubmittedIds: submittedIds.ToArray());
    }

    private static string InputDigest(AgentRequest request) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { request.Instructions, request.Data })));

    internal sealed class CountingAuthority(string directory) : IRuntimeContextAuthority
    {
        private readonly FileContextAuthority inner = new(directory);
        internal int Claims { get; private set; }
        public bool TryClaim(AgentContextEnvelope context, ContextRoundGrant grant) { Claims++; return inner.TryClaim(context, grant); }
    }
}

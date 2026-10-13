using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.ContractTests.DeepSeek.Adapter;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.RuntimeIntegration;

public sealed class RestorationProcessTests
{
    [Theory]
    [InlineData("produce-query", 1, 0)]
    [InlineData("produce-repeat", 0, 1)]
    public async Task FreshProcessesRestoreOneMixedCandidateExecutionWithExactWireAndIndependentEffects(string command, int queries, int operations)
    {
        var directory = Root(); var a = await Run(directory, command); var b = await Run(directory, "consume");
        Assert.NotEqual(a.Pid, b.Pid); Assert.Equal(ContextAdmission.Fresh, a.Admission); Assert.Equal(ContextAdmission.Supplied, b.Admission);
        Assert.Equal(ContextCaptureStatus.Delivered, a.Capture); Assert.Equal(ContextCaptureStatus.Delivered, b.Capture);
        Assert.Equal(CandidateStopReason.ProductionFailed, a.Stop); Assert.Equal(CandidateStopReason.Completed, b.Stop);
        Assert.Equal(a.WorkId, b.WorkId); Assert.NotEqual(a.RoundId, b.RoundId); Assert.NotEqual(a.ExecutionId, b.ExecutionId);
        Assert.Equal(2, a.Sends); Assert.Equal(2, a.Exposures); Assert.Equal(2, a.Settlements);
        Assert.Equal(1, b.Sends); Assert.Equal(1, b.Exposures); Assert.Equal(1, b.Settlements);
        Assert.True(a.WireMatches); Assert.True(b.WireMatches); Assert.True(a.SettlementMatches); Assert.True(b.SettlementMatches);
        Assert.Equal(1, a.OrdinaryEntries); Assert.Equal(1, a.OperationEntries); Assert.Equal(0, a.QueryEntries);
        Assert.Equal(1, b.OrdinaryEntries); Assert.Equal(operations, b.OperationEntries); Assert.Equal(queries, b.QueryEntries);
        Assert.Equal(1, a.CounterEffects); Assert.Equal(7, a.CounterTotal); Assert.Equal(1, a.RecoveryEffects);
        Assert.Equal(2, b.CounterEffects); Assert.Equal(8, b.CounterTotal); Assert.Equal(1, b.RecoveryEffects);
        Assert.Equal(2, a.CurrentTools); Assert.Equal(2, b.CurrentTools); Assert.Equal(2, b.HistoricalTools);
        Assert.Equal(2, a.CurrentModels); Assert.Equal(1, b.CurrentModels); Assert.Equal(2, b.HistoricalModels);
        Assert.Equal(1, b.HistoricalRounds); Assert.Equal(1, a.CurrentRepairs); Assert.Equal(0, b.CurrentRepairs); Assert.Equal(1, b.HistoricalRepairs);
        Assert.Equal(1, a.CurrentReceipts); Assert.Equal(1, b.CurrentReceipts); Assert.Equal(1, b.HistoricalReceipts);
        Assert.Equal(1, a.Hosts); Assert.Equal(1, b.Hosts); Assert.Equal(0, a.HostEffects); Assert.Equal(1, b.HostEffects);
        Assert.Equal(a.RejectedSubmission, b.RepairsSubmissionId); Assert.Equal(a.ModelOrigins, b.AcceptedOrigins);
        Assert.Equal(203, a.CurrentInput); Assert.Equal(23, a.CurrentOutput); Assert.Equal(103, b.CurrentInput); Assert.Equal(13, b.CurrentOutput);
        Assert.Equal(203, b.HistoricalInput); Assert.Equal(23, b.HistoricalOutput);
        Assert.Equal(103, b.MeasuredInput); Assert.Equal(203, b.HistoricalMeasuredInput); Assert.Equal(512, b.InputAllowance);
        Assert.Equal(0, b.ReservedInput); Assert.Equal(0, b.UnresolvedInput); Assert.Equal(0, b.ContinuedCalls);
        Assert.Equal(1, b.Claims); Assert.Equal(1, b.Imports); Assert.True(a.CredentialsConfined); Assert.True(b.CredentialsConfined);
    }

    [Fact]
    public async Task ChangedCorrectionChangesOnlyItsClassifiedWireMessageAcrossRealProcesses()
    {
        var left = Root(); var right = Root();
        await Run(left, "produce-query"); await Run(right, "produce-correction-b");
        var a = await Run(left, "consume"); var b = await Run(right, "consume");
        Assert.True(a.WireMatches); Assert.True(b.WireMatches); Assert.NotEqual(a.WireHash, b.WireHash);
        var wireA = JsonNode.Parse(File.ReadAllText(Path.Combine(left, "consume-wire-1.json")))!;
        var wireB = JsonNode.Parse(File.ReadAllText(Path.Combine(right, "consume-wire-1.json")))!;
        Assert.Equal(RestorationChild.CorrectionA, wireA["messages"]![3]!["content"]!.GetValue<string>());
        Assert.Equal(RestorationChild.CorrectionB, wireB["messages"]![3]!["content"]!.GetValue<string>());
        wireB["messages"]![3]!["content"] = RestorationChild.CorrectionA;
        Assert.True(JsonNode.DeepEquals(wireA, wireB));
        Assert.Equal(a.CounterEffects, b.CounterEffects); Assert.Equal(a.RecoveryEffects, b.RecoveryEffects);
    }

    [Theory]
    [InlineData("consume-provider", 0, 0)]
    [InlineData("consume-model", 0, 0)]
    [InlineData("consume-tool", 0, 0)]
    [InlineData("consume-binding", 0, 0)]
    [InlineData("consume-lineage", 0, 0)]
    [InlineData("consume-correction", 0, 0)]
    [InlineData("consume-corrupt", 0, 0)]
    [InlineData("consume-truncated", 0, 0)]
    [InlineData("consume-oversized", 0, 0)]
    [InlineData("consume-target", 1, 0)]
    [InlineData("consume-stale", 1, 0)]
    [InlineData("consume-witness", 1, 1)]
    [InlineData("consume-config", 1, 1)]
    public async Task OwningAdmissionGateRejectsWithoutNewDispatchInvocationOrBusinessEffect(string command, int authorities, int claims)
    {
        var directory = Root(); var a = await Run(directory, "produce-query"); var b = await Run(directory, command);
        Assert.NotEqual(a.Pid, b.Pid); Assert.Equal(ContextAdmission.Rejected, b.Admission); Assert.NotEqual(ContextRejectionCode.None, b.Rejection);
        Assert.Null(b.Stop); Assert.Equal(ContextCaptureStatus.Unavailable, b.Capture);
        Assert.Equal(authorities, b.AuthorityCalls); Assert.Equal(claims, b.Claims); Assert.Equal(claims, b.Imports);
        Assert.Equal(0, b.Sends); Assert.Equal(0, b.Exposures); Assert.Equal(0, b.Settlements);
        Assert.Equal(0, b.OrdinaryEntries); Assert.Equal(0, b.OperationEntries); Assert.Equal(0, b.QueryEntries); Assert.Equal(0, b.Hosts);
        Assert.Equal(a.CounterEffects, b.CounterEffects); Assert.Equal(a.CounterTotal, b.CounterTotal); Assert.Equal(a.RecoveryEffects, b.RecoveryEffects);
        Assert.False(File.Exists(Path.Combine(directory, command + "-wire-1.json")));
        if (claims == 1)
        {
            var replay = await Run(directory, "consume-competing");
            Assert.Equal(ContextAdmission.Rejected, replay.Admission); Assert.Equal(0, replay.Claims); Assert.Equal(0, replay.Imports); Assert.Equal(0, replay.Sends);
        }
    }

    [Theory]
    [InlineData("consume-replay")]
    [InlineData("consume-competing")]
    public async Task SameGrantAndCompetingGrantCannotReplayConsumedSource(string command)
    {
        var directory = Root(); await Run(directory, "produce-query"); var b = await Run(directory, "consume"); var replay = await Run(directory, command);
        Assert.Equal(CandidateStopReason.Completed, b.Stop); Assert.Equal(ContextAdmission.Rejected, replay.Admission);
        Assert.Equal(1, replay.AuthorityCalls); Assert.Equal(0, replay.Claims); Assert.Equal(0, replay.Imports);
        Assert.Equal(0, replay.Sends); Assert.Equal(0, replay.OrdinaryEntries); Assert.Equal(0, replay.OperationEntries); Assert.Equal(0, replay.QueryEntries); Assert.Equal(0, replay.Hosts);
        Assert.Equal(b.CounterEffects, replay.CounterEffects); Assert.Equal(b.RecoveryEffects, replay.RecoveryEffects); Assert.Equal(1, replay.HostEffects);
    }

    [Fact]
    public async Task UnknownOrdinaryMutationCannotBecomeCandidateRecoveryAuthority()
    {
        var directory = Root(); var a = await Run(directory, "produce-unsafe"); var b = await Run(directory, "consume");
        Assert.Equal(1, a.CounterEffects); Assert.Equal(0, a.RecoveryEffects); Assert.Equal(ContextAdmission.Rejected, b.Admission);
        Assert.Equal(ContextRejectionCode.UnsupportedContext, b.Rejection); Assert.Equal(0, b.Claims); Assert.Equal(0, b.Imports);
        Assert.Equal(0, b.Sends); Assert.Equal(0, b.OrdinaryEntries); Assert.Equal(0, b.OperationEntries); Assert.Equal(0, b.QueryEntries); Assert.Equal(0, b.Hosts);
        Assert.Equal(a.CounterEffects, b.CounterEffects);
    }

    [Theory]
    [InlineData("consume-before", 0, 1, 1)]
    [InlineData("consume-cut", 2, 0, 0)]
    [InlineData("consume-unresolved", 1, 1, 1)]
    public async Task ARecoveryOnlyRoundCanBeCapturedAndLaterResumedWithoutDoubleAdmission(string command, int middleTools, int lastQueries, int lastOrdinary)
    {
        var directory = Root(); var a = await Run(directory, "produce-query"); var b = await Run(directory, command); var c = await Run(directory, "third");
        Assert.Equal(3, new[] { a.Pid, b.Pid, c.Pid }.Distinct().Count()); Assert.Equal(ContextAdmission.Supplied, b.Admission);
        Assert.Equal(0, b.Sends); Assert.Equal(0, b.Hosts); Assert.Equal(middleTools, b.CurrentTools); Assert.Equal(ContextCaptureStatus.Delivered, b.Capture);
        Assert.Equal(command == "consume-unresolved" ? CandidateStopReason.ProductionFailed : CandidateStopReason.Cancelled, b.Stop);
        Assert.Equal(ContextAdmission.Supplied, c.Admission); Assert.Equal(CandidateStopReason.Completed, c.Stop);
        Assert.Equal(a.WorkId, c.WorkId); Assert.NotEqual(b.RoundId, c.RoundId); Assert.Equal(2, c.HistoricalRounds);
        Assert.Equal(1, c.Sends); Assert.Equal(lastQueries, c.QueryEntries); Assert.Equal(lastOrdinary, c.OrdinaryEntries); Assert.Equal(0, c.OperationEntries);
        Assert.Equal(1, c.HistoricalRepairs); Assert.Equal(0, c.CurrentRepairs); Assert.Equal(1, c.HistoricalReceipts);
        Assert.Equal(2, c.CounterEffects); Assert.Equal(8, c.CounterTotal); Assert.Equal(1, c.RecoveryEffects); Assert.Equal(1, c.HostEffects);
        Assert.Equal(a.RejectedSubmission, c.RepairsSubmissionId); Assert.True(c.WireMatches); Assert.Equal(a.ModelOrigins, c.AcceptedOrigins);
    }

    [Fact]
    public async Task UnknownHostFeedbackIsRetainedWithoutAutomaticResubmission()
    {
        var directory = Root(); await Run(directory, "produce-query"); var b = await Run(directory, "consume-unknown"); var c = await Run(directory, "third");
        Assert.Equal(CandidateStopReason.UnknownAcknowledgement, b.Stop); Assert.Equal(1, b.Hosts); Assert.Equal(1, b.HostEffects);
        Assert.Equal(ContextAdmission.Supplied, c.Admission); Assert.Equal(CandidateStopReason.UnknownAcknowledgement, c.Stop);
        Assert.Equal(0, c.Sends); Assert.Equal(0, c.Hosts); Assert.Equal(0, c.OrdinaryEntries); Assert.Equal(0, c.OperationEntries); Assert.Equal(0, c.QueryEntries);
        Assert.Equal(1, c.HostEffects); Assert.Equal(b.CounterEffects, c.CounterEffects); Assert.Equal(b.RecoveryEffects, c.RecoveryEffects);
    }

    [Theory]
    [InlineData("produce-export-failure", ContextCaptureStatus.Unavailable, false)]
    [InlineData("produce-sink-before", ContextCaptureStatus.Failed, true)]
    [InlineData("produce-sink-after", ContextCaptureStatus.Failed, true)]
    public async Task CaptureFailureDoesNotEraseActualWorkOrInventDurableAuthority(string command, ContextCaptureStatus capture, bool prepared)
    {
        var directory = Root(); var a = await Run(directory, command);
        Assert.Equal(ContextAdmission.Fresh, a.Admission); Assert.Equal(CandidateStopReason.ProductionFailed, a.Stop);
        Assert.Equal(capture, a.Capture); Assert.Equal(prepared, a.CheckpointPrepared);
        Assert.Equal(2, a.Sends); Assert.Equal(2, a.Settlements); Assert.Equal(2, a.CurrentTools); Assert.Equal(1, a.Hosts);
        Assert.Equal(1, a.CounterEffects); Assert.Equal(1, a.RecoveryEffects); Assert.Equal(203, a.CurrentInput); Assert.True(a.SettlementMatches);
        Assert.False(Directory.Exists(Path.Combine(directory, "source")));
    }

    [Fact]
    public async Task DeliveredEnvelopeWithoutHostPersistenceCannotAuthorizeRestoration()
    {
        var directory = Root(); var a = await Run(directory, "produce-storage-failure"); var b = await Run(directory, "consume-storage");
        Assert.Equal(ContextCaptureStatus.Delivered, a.Capture); Assert.True(a.CheckpointPrepared);
        Assert.Equal(ContextAdmission.Rejected, b.Admission); Assert.Equal(1, b.AuthorityCalls); Assert.Equal(0, b.Claims); Assert.Equal(0, b.Imports);
        Assert.Equal(0, b.Sends); Assert.Equal(0, b.OrdinaryEntries); Assert.Equal(0, b.OperationEntries); Assert.Equal(0, b.QueryEntries); Assert.Equal(0, b.Hosts);
        Assert.Equal(a.CounterEffects, b.CounterEffects); Assert.Equal(a.RecoveryEffects, b.RecoveryEffects);
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".git")) && !Directory.Exists(Path.Combine(directory.FullName, ".git"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root unavailable."),
            ".local", "issue-73", "process-fixtures", Guid.NewGuid().ToString("N"));
    }

    private static async Task<RestorationObservation> Run(string directory, string command)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(RestorationProcessTests).Assembly.Location);
        start.ArgumentList.Add("deepseek-restoration-child"); start.ArgumentList.Add(directory); start.ArgumentList.Add(command);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var stdout = await output; var stderr = await error;
        foreach (var canary in new[] { RestorationChild.FinalReasoning, RestorationChild.ToolReasoning, RestorationChild.InitialFinal,
            RestorationChild.CorrectionA, RestorationChild.CorrectionB, AdapterFixture.Credential, RestorationChild.CredentialB })
            Assert.DoesNotContain(canary, stdout + stderr);
        Assert.True(process.ExitCode == 0, $"Child failed with exit {process.ExitCode}; restricted evidence is confined to {directory}.");
        Assert.Empty(stdout); Assert.Empty(stderr);
        return JsonSerializer.Deserialize<RestorationObservation>(File.ReadAllText(Path.Combine(directory, command + "-result.json")))!;
    }
}

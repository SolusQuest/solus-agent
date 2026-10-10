using System.Diagnostics;
using System.Text.Json;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.Adapter;

public sealed class PersistenceProcessTests
{
    [Theory]
    [InlineData("produce-final")]
    [InlineData("produce-empty")]
    [InlineData("produce-max")]
    public async Task SeparateProcessesRestoreActualToolsEnabledFinalAndExactRequiredReasoning(string command)
    {
        var directory = Root(); var a = await Run(directory, command); var b = await Run(directory, "consume-final");
        Assert.NotEqual(a.Pid, b.Pid); Assert.Equal(ContextAdmission.Fresh, a.Admission); Assert.Equal(ContextAdmission.Supplied, b.Admission);
        Assert.Equal(AgentTerminationReason.Completed, a.Reason); Assert.Equal(AgentTerminationReason.Completed, b.Reason);
        Assert.True(a.WireMatches); Assert.True(b.WireMatches); Assert.True(a.ResponseAssociated); Assert.True(b.ResponseAssociated);
        Assert.Equal(1, a.Sends); Assert.Equal(1, b.Sends); Assert.Equal(0, a.ToolEffects); Assert.Equal(0, b.ToolEffects);
        Assert.Equal(1, b.Claims); Assert.Equal(1, b.HistoricalAttempts); Assert.Equal(10, b.HistoricalInput); Assert.Equal(10, b.CurrentInput);
        Assert.NotEqual(a.Execution, b.Execution); Assert.NotEqual(a.Logical, b.Logical); Assert.NotEqual(a.Physical, b.Physical);
        Assert.Equal(1, b.Ordinal);
    }

    [Fact]
    public async Task SeparateProcessesContinueRealHttpFailureWithExactNewAttemptLineage()
    {
        var directory = Root(); var a = await Run(directory, "produce-retry"); var b = await Run(directory, "consume-retry");
        Assert.NotEqual(a.Pid, b.Pid); Assert.Equal(AgentTerminationReason.ResourceLimit, a.Reason);
        Assert.Equal(ContextAdmission.Supplied, b.Admission); Assert.Equal(AgentTerminationReason.Completed, b.Reason);
        Assert.True(a.WireMatches); Assert.True(b.WireMatches); Assert.Equal(a.WireHash, b.WireHash);
        Assert.Equal(a.Logical, b.Logical); Assert.Equal(2, b.Ordinal); Assert.NotEqual(a.Execution, b.Execution); Assert.NotEqual(a.Physical, b.Physical);
        Assert.Equal(a.Execution, b.PreviousExecution); Assert.Equal(a.Physical, b.PreviousPhysical); Assert.Equal(a.Ordinal, b.PreviousOrdinal);
        Assert.Equal(1, b.HistoricalAttempts); Assert.Equal(10, b.HistoricalInput); Assert.Equal(10, b.CurrentInput);
        Assert.True(b.ResponseAssociated); Assert.Equal(1, b.Sends); Assert.Equal(0, b.ToolEffects);
    }

    [Fact]
    public async Task ThreeProcessesRetainEarlierAcceptedReplayWhileRetryingLaterRound()
    {
        var directory = Root(); var a = await Run(directory, "produce-final"); var b = await Run(directory, "middle-failure");
        var c = await Run(directory, "last-retry");
        Assert.Equal(3, new[] { a.Pid, b.Pid, c.Pid }.Distinct().Count());
        Assert.Equal(AgentTerminationReason.ResourceLimit, b.Reason); Assert.Equal(AgentTerminationReason.Completed, c.Reason);
        Assert.Equal(ContextAdmission.Supplied, c.Admission); Assert.True(b.WireMatches); Assert.True(c.WireMatches);
        Assert.Equal(b.WireHash, c.WireHash); Assert.Equal(b.Logical, c.Logical); Assert.Equal(2, c.Ordinal);
        Assert.NotEqual(b.Physical, c.Physical); Assert.Equal(2, c.HistoricalAttempts); Assert.Equal(20, c.HistoricalInput);
        Assert.Equal(b.Execution, c.PreviousExecution); Assert.Equal(b.Physical, c.PreviousPhysical); Assert.Equal(b.Ordinal, c.PreviousOrdinal);
        Assert.Equal(10, c.CurrentInput); Assert.True(c.ResponseAssociated); Assert.Equal(1, c.Sends); Assert.Equal(0, c.ToolEffects);
    }

    [Theory]
    [InlineData("reject-corrupt", 1)]
    [InlineData("reject-provider-format", 1)]
    [InlineData("reject-config", 1)]
    [InlineData("reject-replay", 1)]
    [InlineData("reject-model", 0)]
    [InlineData("reject-origin", 0)]
    [InlineData("reject-outer-format", 0)]
    [InlineData("reject-host", 0)]
    public async Task RealProcessAdmissionRejectsAtOwningGateWithoutDispatchOrFreshFallback(string command, int claims)
    {
        var directory = Root(); var a = await Run(directory, "produce-final"); var b = await Run(directory, command);
        Assert.NotEqual(a.Pid, b.Pid); Assert.Equal(ContextAdmission.Rejected, b.Admission); Assert.Null(b.Reason);
        Assert.Equal(0, b.Sends); Assert.Equal(0, b.ToolEffects); Assert.Equal(claims, b.Claims);
        Assert.Equal(ContextCaptureStatus.Unavailable, b.Capture);
        if (claims == 1) Assert.Equal(ContextRejectionCode.IncompatibleContext, b.Rejection);
        Assert.False(File.Exists(Path.Combine(directory, command + "-restricted-wire.json")));
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".git")) && !Directory.Exists(Path.Combine(directory.FullName, ".git"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root unavailable."),
            ".local", "issue-69", "process-fixtures", Guid.NewGuid().ToString("N"));
    }

    private static async Task<PersistenceObservation> Run(string directory, string command)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(PersistenceProcessTests).Assembly.Location);
        start.ArgumentList.Add("deepseek-persistence-child"); start.ArgumentList.Add(directory); start.ArgumentList.Add(command);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var stdout = await output; var stderr = await error;
        // Exceptions and ordinary child output must not disclose restricted state, even on a test failure.
        Assert.DoesNotContain(PersistenceChild.Reasoning, stdout + stderr); Assert.DoesNotContain(PersistenceChild.Final, stdout + stderr);
        Assert.DoesNotContain(AdapterFixture.Credential, stdout + stderr); Assert.DoesNotContain("new-process-credential", stdout + stderr);
        Assert.True(process.ExitCode == 0, $"Child failed with exit {process.ExitCode}."); Assert.Empty(stdout); Assert.Empty(stderr);
        return JsonSerializer.Deserialize<PersistenceObservation>(File.ReadAllText(Path.Combine(directory, command + "-result.json")))!;
    }
}

using System.Diagnostics;
using System.Text.Json;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ContractTests.DeepSeek.Adapter;
using SolusAgent.ContractTests.Runtime.Tools;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.RuntimeIntegration;

public sealed class CorrectionContextProcessTests
{
    [Theory]
    [InlineData("produce-schema")] [InlineData("produce-path")]
    [InlineData("produce-schema-candidate")] [InlineData("produce-path-candidate")]
    public async Task ActualDeepSeekCorrectionRestoresInFreshProcessWithoutRepeatingHistoricalEffects(string command)
    {
        var directory = Root(); var first = await Run(directory, command); var second = await Run(directory, "consume");
        Assert.NotEqual(first.Pid, second.Pid); Assert.Equal(ContextAdmission.Fresh, first.Admission);
        Assert.Equal(AgentTerminationReason.Completed, first.Reason); Assert.Equal(3, first.Sends);
        Assert.Equal(4, first.CurrentAdmissions); Assert.Equal(2, first.CurrentInvocations); Assert.Equal(2, first.Entries);
        Assert.Equal(ContextAdmission.Supplied, second.Admission); Assert.Equal(AgentTerminationReason.Completed, second.Reason);
        Assert.Equal(1, second.Sends); Assert.Equal(1, second.Claims); Assert.Equal(1, second.Imports);
        Assert.Equal(0, second.CurrentAdmissions); Assert.Equal(0, second.CurrentInvocations); Assert.Equal(0, second.Entries);
        Assert.Equal(4, second.HistoricalAdmissions); Assert.Equal(2, second.HistoricalInvocations); Assert.Equal(3, second.HistoricalWork);
        Assert.True(first.WireMatches); Assert.True(second.WireMatches); Assert.True(second.HistoricalResults);
        Assert.Equal(0, second.Repairs); Assert.Equal(command.EndsWith("candidate", StringComparison.Ordinal) ? 1 : 0, second.Hosts);
        Assert.Equal(2, first.Effects); Assert.Equal(2, second.Effects);
        Assert.All(File.ReadAllLines(Path.Combine(directory, "effects")), line => Assert.Equal(first.Pid.ToString(), line));
        var competing = await Run(directory, "consume-competing");
        Assert.Equal(ContextAdmission.Rejected, competing.Admission); Assert.Equal(0, competing.Claims); Assert.Equal(0, competing.Imports);
        Assert.Equal(0, competing.Sends); Assert.Equal(0, competing.Entries); Assert.Equal(2, competing.Effects);
    }

    [Theory]
    [InlineData("consume-policy", 0, 0)] [InlineData("consume-target", 0, 0)] [InlineData("consume-options", 1, 1)]
    public async Task CurrentPolicyTargetAndProviderOptionsDenyAtTheirOwningGate(string command, int claims, int imports)
    {
        var directory = Root(); var first = await Run(directory, "produce-schema"); var rejected = await Run(directory, command);
        Assert.NotEqual(first.Pid, rejected.Pid); Assert.Equal(ContextAdmission.Rejected, rejected.Admission);
        Assert.NotEqual(ContextRejectionCode.None, rejected.Rejection); Assert.Null(rejected.Reason);
        Assert.Equal(claims, rejected.Claims); Assert.Equal(imports, rejected.Imports); Assert.Equal(0, rejected.Sends);
        Assert.Equal(0, rejected.Entries); Assert.Equal(2, rejected.Effects);
        var next = await Run(directory, "consume");
        Assert.Equal(claims == 1 ? ContextAdmission.Rejected : ContextAdmission.Supplied, next.Admission);
        Assert.Equal(claims == 1 ? 0 : 1, next.Sends); Assert.Equal(2, next.Effects);
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".git")) && !Directory.Exists(Path.Combine(directory.FullName, ".git"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root unavailable."),
            ".local", "g03-execution", "correction-process", Guid.NewGuid().ToString("N"));
    }

    private static async Task<CorrectionProcessObservation> Run(string directory, string command)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(CorrectionContextProcessTests).Assembly.Location);
        start.ArgumentList.Add("argument-correction-child"); start.ArgumentList.Add(directory); start.ArgumentList.Add(command);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); var stdout = await output; var stderr = await error;
        foreach (var canary in new[] { ArgumentCorrectionFixture.Canary, AdapterFixture.Credential,
            CorrectionContextFixture.RotatedCredential, CorrectionContextFixture.RejectedReplay }) Assert.DoesNotContain(canary, stdout + stderr);
        Assert.True(process.ExitCode == 0, $"Child failed with exit {process.ExitCode}; restricted evidence is confined to {directory}.");
        Assert.Empty(stdout); Assert.Empty(stderr);
        return JsonSerializer.Deserialize<CorrectionProcessObservation>(File.ReadAllText(Path.Combine(directory, command + "-result.json")))!;
    }
}

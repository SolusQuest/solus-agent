using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Tools;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Context;

internal sealed record StructuredProcessObservation(int Pid, ContextAdmission Admission, AgentTerminationReason Reason,
    int ProviderEffects, int Imports, int Entries, int CurrentInvocations, int HistoricalInvocations, string[] Arguments, string[] Results, bool[] Historical);

internal static class StructuredContextChild
{
    internal static async Task<int> Run(string[] args)
    {
        var directory = args[1]; Directory.CreateDirectory(directory);
        Action entered = () => File.AppendAllText(Path.Combine(directory, "tool-effects.txt"), Environment.ProcessId + "\n");
        StructuredProcessObservation observation;
        if (args[2] == "produce")
        {
            var source = await StructuredContextFixture.Capture(entered);
            FileContextAuthority.Save(directory, source.Envelope, source.Result.Checkpoint!);
            var members = JsonNode.Parse(source.Envelope.CopyRestrictedPayload())!["Members"]!.AsArray();
            observation = new(Environment.ProcessId, source.Result.Admission, source.Result.Outcome!.Reason, source.Provider.Effects, source.Provider.Imports,
                source.Capabilities.Sum(c => c.Entries), source.Result.Outcome.Usage!.ToolInvocations!.Invoked, 0,
                members.Select(m => m!["Call"]!["ArgumentsJson"]!.GetValue<string>()).ToArray(),
                members.Select(m => m!["Result"]!["Json"]!.GetValue<string>()).ToArray(), members.Select(_ => false).ToArray());
        }
        else
        {
            var envelope = FileContextAuthority.Read(directory); var selection = FileContextAuthority.Selection(directory);
            var (bindings, caps) = StructuredContextFixture.Bindings(entered);
            var provider = new PersistentProvider(tools: true) { Respond = StructuredContextFixture.Final };
            var request = StructuredContextFixture.Request(1);
            var result = await StructuredContextFixture.Agent(provider, bindings, new FileContextAuthority(directory)).ExecuteWithContextAsync(
                new(request, ContextExecutionIntent.ContinueRun, envelope, new(Guid.NewGuid(), selection.Info, request.ExecutionId, Guid.NewGuid())));
            Assert.Equal(ContextAdmission.Supplied, result.Admission); StructuredToolContextTests.AssertHistory(provider.LastRequest!);
            var historical = provider.LastRequest!.Inputs.Where(i => i.ToolResult is not null).Select(i => i.ToolResult!).ToArray();
            observation = new(Environment.ProcessId, result.Admission, result.Outcome!.Reason, provider.Effects, provider.Imports, caps.Sum(c => c.Entries),
                result.Outcome.Usage!.ToolInvocations!.Invoked, Assert.Single(result.History).Usage.ToolInvocations!.Invoked,
                historical.Select(r => r.Call.ArgumentsJson).ToArray(), historical.Select(r => r.Json!).ToArray(), historical.Select(r => r.IsHistorical).ToArray());
        }
        File.WriteAllText(Path.Combine(directory, args[2] + ".json"), JsonSerializer.Serialize(observation));
        return 0;
    }
}

public sealed class StructuredContextProcessTests
{
    [Fact]
    public async Task FreshProcessRestoresAllFiveCompleteStructuredResultsWithNoRepeatedEntries()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "structured-context", Guid.NewGuid().ToString("N"));
        var a = await Run(root, "produce"); var b = await Run(root, "restore");
        Assert.NotEqual(a.Pid, b.Pid); Assert.Equal(ContextAdmission.Supplied, b.Admission);
        Assert.Equal(AgentTerminationReason.ResourceLimit, a.Reason); Assert.Equal(AgentTerminationReason.Completed, b.Reason);
        Assert.Equal(2, a.ProviderEffects); Assert.Equal(1, b.ProviderEffects); Assert.Equal(1, b.Imports);
        Assert.Equal(5, a.Entries); Assert.Equal(0, b.Entries); Assert.Equal(5, a.CurrentInvocations); Assert.Equal(0, b.CurrentInvocations);
        Assert.Equal(5, b.HistoricalInvocations); Assert.Equal(a.Arguments, b.Arguments); Assert.Equal(a.Results, b.Results);
        Assert.All(a.Historical, value => Assert.False(value)); Assert.All(b.Historical, value => Assert.True(value));
        var journal = File.ReadAllLines(Path.Combine(root, "tool-effects.txt"));
        Assert.Equal(5, journal.Length); Assert.All(journal, line => Assert.Equal(a.Pid.ToString(), line));
    }

    private static async Task<StructuredProcessObservation> Run(string root, string command)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(StructuredContextProcessTests).Assembly.Location); start.ArgumentList.Add("structured-context-child");
        start.ArgumentList.Add(root); start.ArgumentList.Add(command);
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, await output + await error);
        return JsonSerializer.Deserialize<StructuredProcessObservation>(File.ReadAllText(Path.Combine(root, command + ".json")))!;
    }
}

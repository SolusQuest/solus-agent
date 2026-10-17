using System.Diagnostics;
using System.Text.Json;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.Runtime.Api.Configuration;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.RuntimeIntegration;

internal static class FeedbackVariantChild
{
    internal static async Task<int> Run(string[] args)
    {
        var directory = args[1]; var command = args[2]; Directory.CreateDirectory(directory);
        var producing = command.StartsWith("produce", StringComparison.Ordinal);
        if (producing)
        {
            File.WriteAllText(Path.Combine(directory, "mode"), command.Contains("field", StringComparison.Ordinal) ? "prefix_empty" : "domain");
            File.WriteAllText(Path.Combine(directory, "candidates"), command.EndsWith("candidate", StringComparison.Ordinal).ToString());
        }
        var mode = File.ReadAllText(Path.Combine(directory, "mode")); var candidates = bool.Parse(File.ReadAllText(Path.Combine(directory, "candidates")));
        var authority = new CountingAuthority(directory);
        using var fixture = new FeedbackVariantFixture(mode, producing, candidates, authority, changedPolicy: command == "consume-policy",
            restrict: command == "consume-allowlist", effect: () => File.AppendAllText(Path.Combine(directory, "effects"), Environment.ProcessId + Environment.NewLine));
        var request = FeedbackVariantFixture.Request(producing); var sink = new RestrictedContextHost();
        var execution = producing ? new ContextExecutionRequest(request, ContextExecutionIntent.Fresh)
            : new(request, ContextExecutionIntent.NewRunFromContext, FileContextAuthority.Read(directory),
                new(Guid.NewGuid(), FileContextAuthority.Selection(directory).Info, request.ExecutionId, Guid.NewGuid()));
        var executed = await fixture.Execute(execution, sink); var result = executed.Context;
        if (producing) { Assert.Equal(ContextCaptureStatus.Delivered, result.CaptureStatus); FileContextAuthority.Save(directory, sink.CopyRestrictedContext(), result.Checkpoint!); }
        var observation = new CorrectionProcessObservation(Environment.ProcessId, result.Admission, result.RejectionCode, result.Outcome?.Reason,
            fixture.Handler.Sends, fixture.Capability.Entries, authority.Claims, fixture.Provider.Imports,
            result.Outcome?.Usage?.ToolCallAdmissions?.Admitted ?? 0, result.Outcome?.Usage?.ToolInvocations?.Invoked ?? 0,
            result.History.Sum(r => r.Usage.ToolCallAdmissions?.Admitted ?? 0), result.History.Sum(r => r.Usage.ToolInvocations?.Invoked ?? 0),
            result.History.Sum(r => r.CompletedWorkUnits), fixture.Hosts, executed.Candidates?.RepairsAdmitted ?? 0, fixture.WireMatches,
            fixture.Provider.Requests.SelectMany(r => r.Inputs).Where(i => i.ToolResult is not null).All(i => producing || i.ToolResult!.IsHistorical),
            File.Exists(Path.Combine(directory, "effects")) ? File.ReadAllLines(Path.Combine(directory, "effects")).Length : 0);
        File.WriteAllText(Path.Combine(directory, command + "-result.json"), JsonSerializer.Serialize(observation));
        return 0;
    }
    private sealed class CountingAuthority(string directory) : IRuntimeContextAuthority
    {
        internal int Claims;
        public bool TryClaim(AgentContextEnvelope envelope, ContextRoundGrant grant)
        { var admitted = new FileContextAuthority(directory).TryClaim(envelope, grant); if (admitted) Claims++; return admitted; }
    }
}

public sealed class FeedbackVariantProcessTests
{
    [Theory]
    [InlineData("produce-domain")] [InlineData("produce-field")]
    [InlineData("produce-domain-candidate")] [InlineData("produce-field-candidate")]
    public async Task IndependentProcessesPreserveVariantsAndDoNotRepeatEffects(string command)
    {
        var directory = Root(); var first = await Run(directory, command);
        foreach (var denied in new[] { "consume-policy", "consume-allowlist" })
        {
            var negative = await Run(directory, denied); Assert.NotEqual(first.Pid, negative.Pid);
            Assert.Equal(ContextAdmission.Rejected, negative.Admission); Assert.Equal(0, negative.Claims); Assert.Equal(0, negative.Imports);
            Assert.Equal(0, negative.Sends); Assert.Equal(0, negative.Entries); Assert.Equal(2, negative.Effects);
        }
        var second = await Run(directory, "consume"); Assert.NotEqual(first.Pid, second.Pid);
        Assert.Equal(AgentTerminationReason.Completed, first.Reason); Assert.Equal(5, first.CurrentAdmissions); Assert.Equal(2, first.CurrentInvocations);
        Assert.Equal(ContextAdmission.Supplied, second.Admission); Assert.Equal(AgentTerminationReason.Completed, second.Reason);
        Assert.Equal(1, second.Claims); Assert.Equal(1, second.Imports); Assert.Equal(1, second.Sends);
        Assert.Equal(0, second.CurrentAdmissions); Assert.Equal(0, second.CurrentInvocations); Assert.Equal(0, second.Entries);
        Assert.Equal(5, second.HistoricalAdmissions); Assert.Equal(2, second.HistoricalInvocations); Assert.Equal(3, second.HistoricalWork);
        Assert.True(first.WireMatches); Assert.True(second.WireMatches); Assert.True(second.HistoricalResults); Assert.Equal(2, second.Effects);
        Assert.All(File.ReadAllLines(Path.Combine(directory, "effects")), line => Assert.Equal(first.Pid.ToString(), line));
        var competing = await Run(directory, "consume-competing"); Assert.Equal(ContextAdmission.Rejected, competing.Admission);
        Assert.Equal(0, competing.Claims); Assert.Equal(0, competing.Imports); Assert.Equal(0, competing.Sends); Assert.Equal(2, competing.Effects);
    }
    private static string Root() => Path.Combine(AppContext.BaseDirectory, "feedback-process", Guid.NewGuid().ToString("N"));
    private static async Task<CorrectionProcessObservation> Run(string directory, string command)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(FeedbackVariantProcessTests).Assembly.Location); start.ArgumentList.Add("feedback-variant-child");
        start.ArgumentList.Add(directory); start.ArgumentList.Add(command);
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); Assert.Empty(await output); Assert.Empty(await error); Assert.Equal(0, process.ExitCode);
        return JsonSerializer.Deserialize<CorrectionProcessObservation>(File.ReadAllText(Path.Combine(directory, command + "-result.json")))!;
    }
}

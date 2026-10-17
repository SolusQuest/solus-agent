using System.Text.Json;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.Runtime.Api.Configuration;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.RuntimeIntegration;

internal sealed record CorrectionProcessObservation(int Pid, ContextAdmission Admission, ContextRejectionCode Rejection,
    AgentTerminationReason? Reason, int Sends, int Entries, int Claims, int Imports, int CurrentAdmissions,
    int CurrentInvocations, int HistoricalAdmissions, int HistoricalInvocations, int HistoricalWork, int Hosts,
    int Repairs, bool WireMatches, bool HistoricalResults, int Effects);

internal static class CorrectionContextChild
{
    internal static async Task<int> Run(string[] args)
    {
        var directory = args[1]; var command = args[2]; Directory.CreateDirectory(directory);
        var producing = command.StartsWith("produce-", StringComparison.Ordinal);
        if (producing)
        {
            File.WriteAllText(Path.Combine(directory, "mode"), command.Contains("path", StringComparison.Ordinal) ? "path" : "schema");
            File.WriteAllText(Path.Combine(directory, "candidates"), command.EndsWith("candidate", StringComparison.Ordinal).ToString());
            File.WriteAllText(Path.Combine(directory, "selected-target"), Guid.NewGuid().ToString());
        }
        var mode = File.ReadAllText(Path.Combine(directory, "mode"));
        var candidates = bool.Parse(File.ReadAllText(Path.Combine(directory, "candidates")));
        var selectedTarget = Guid.Parse(File.ReadAllText(Path.Combine(directory, "selected-target")));
        var actualTarget = command == "consume-target" ? Guid.NewGuid() : selectedTarget;
        var authority = new TargetAuthority(directory, actualTarget);
        using var fixture = new CorrectionContextFixture(mode, producing, candidates, authority,
            toolEffect: () => File.AppendAllText(Path.Combine(directory, "effects"), Environment.ProcessId + Environment.NewLine),
            changedPolicy: command == "consume-policy", maximumTokens: command == "consume-options" ? 2049 : 2048);
        var request = CorrectionContextFixture.Request(producing); var sink = new RestrictedContextHost();
        ContextExecutionRequest execution;
        if (producing) execution = new(request, ContextExecutionIntent.Fresh);
        else
        {
            var envelope = FileContextAuthority.Read(directory); var selected = FileContextAuthority.Selection(directory);
            var grant = new ContextRoundGrant(Guid.NewGuid(), selected.Info, request.ExecutionId, Guid.NewGuid());
            execution = new(request, ContextExecutionIntent.NewRunFromContext, envelope, grant);
        }
        var executed = await fixture.Execute(execution, sink); var result = executed.Context;
        if (producing)
        {
            Assert.Equal(ContextCaptureStatus.Delivered, result.CaptureStatus);
            var envelope = sink.CopyRestrictedContext(); CorrectionContextTests.AssertSanitized(envelope, fixture.Host.BadArguments);
            FileContextAuthority.Save(directory, envelope, result.Checkpoint!);
        }
        var historicalResults = producing || fixture.Provider.Requests.Count == 0 ||
            fixture.Provider.Requests[0].Inputs.Where(i => i.ToolResult is not null).All(i => i.ToolResult!.IsHistorical);
        var observation = new CorrectionProcessObservation(Environment.ProcessId, result.Admission, result.RejectionCode,
            result.Outcome?.Reason, fixture.Handler.Sends, fixture.Host.Capability.Entries, authority.Claims,
            fixture.Provider.Imports, result.Outcome?.Usage?.ToolCallAdmissions?.Admitted ?? 0,
            result.Outcome?.Usage?.ToolInvocations?.Invoked ?? 0,
            result.History.Sum(r => r.Usage.ToolCallAdmissions?.Admitted ?? 0),
            result.History.Sum(r => r.Usage.ToolInvocations?.Invoked ?? 0), result.History.Sum(r => r.CompletedWorkUnits),
            fixture.Hosts, executed.Candidates?.RepairsAdmitted ?? 0, fixture.WireMatches, historicalResults,
            File.Exists(Path.Combine(directory, "effects")) ? File.ReadAllLines(Path.Combine(directory, "effects")).Length : 0);
        File.WriteAllText(Path.Combine(directory, command + "-result.json"), JsonSerializer.Serialize(observation));
        return 0;
    }

    private sealed class TargetAuthority(string directory, Guid actualTarget) : IRuntimeContextAuthority
    {
        internal int Claims;
        public bool TryClaim(AgentContextEnvelope envelope, ContextRoundGrant grant)
        {
            // The Host selects its actual external resource independently of the untrusted checkpoint.
            if (Guid.Parse(File.ReadAllText(Path.Combine(directory, "selected-target"))) != actualTarget) return false;
            var accepted = new FileContextAuthority(directory).TryClaim(envelope, grant);
            if (accepted) Claims++;
            return accepted;
        }
    }
}

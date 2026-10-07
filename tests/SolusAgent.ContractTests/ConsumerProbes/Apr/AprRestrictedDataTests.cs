using System.Text;
using System.Text.Json;
using AprHost;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using Xunit;

namespace SolusAgent.ContractTests.ConsumerProbes.Apr;

/// <summary>Restricted payload confinement and credential canaries over the composed APR scenario.</summary>
public sealed class AprRestrictedDataTests
{
    private const string RestrictedCanary = "APR_RESTRICTED_CANARY";
    private const string CorrectionCanary = "CORRECTION_CANARY expected-value=7";

    [Fact]
    public async Task RestrictedCanariesTravelOnlyOnTheRestrictedPayloadSurface()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetWorkUnits = 1, TargetProductions = 2 });
        var execution = AprFixtures.Request();
        var sink = new AprFixtures.CollectingSink();
        var supplied = AprFixtures.State(execution.ExecutionId, 0, 1, 0, RestrictedCanary);
        var feedback = new ScriptedAprFeedback((submission, index, _) => ValueTask.FromResult<CandidateFeedback?>(
            index == 0
                ? new CandidateFeedback(submission.ExecutionId, submission.SubmissionId,
                    CandidateAcknowledgement.Acknowledged, CandidateDecision.Reject, CandidateContinuation.Continue, CorrectionCanary)
                : AprDecisions.Accept(submission.ExecutionId, submission.SubmissionId)));
        var progress = new List<SolusAgent.Api.Execution.AgentProgress>();

        var run = await AprFixtures.WithTimeout(startup.Host.RunAsync(
            AprFixtures.ContinueRun(execution, supplied.ToEnvelope(AprScenarioAgent.ImplementationId)), sink,
            AprFixtures.Candidates(execution), feedback, new AprFixtures.InlineProgress(progress.Add)));

        // The restricted call-scoped path is nonempty and carries the restricted canary.
        var restricted = AprFixtures.RestrictedText(sink);
        Assert.Contains(RestrictedCanary, restricted, StringComparison.Ordinal);
        Assert.Equal(ContextCaptureStatus.Delivered, run.Context!.CaptureStatus);

        var ordinary = OrdinarySurfaces(run, progress, startup);
        Assert.NotEmpty(ordinary);
        Assert.DoesNotContain(RestrictedCanary, ordinary, StringComparison.Ordinal);
        Assert.DoesNotContain("CORRECTION_CANARY", ordinary, StringComparison.Ordinal);
        Assert.DoesNotContain("apr-correction", ordinary, StringComparison.Ordinal);
        Assert.DoesNotContain("apr-item:", ordinary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CredentialCanariesStayOutOfModelToolSavedAndOrdinaryData()
    {
        var startup = AprStartup.Create(new AprStartupOptions { TargetWorkUnits = 1, TargetProductions = 2 });
        var execution = AprFixtures.Request();
        var sink = new AprFixtures.CollectingSink();
        var supplied = AprFixtures.State(execution.ExecutionId, 0, 1, 0, RestrictedCanary);
        var feedback = new ScriptedAprFeedback((submission, index, _) => ValueTask.FromResult<CandidateFeedback?>(
            index == 0
                ? AprDecisions.RejectWithCorrection(submission.ExecutionId, submission.SubmissionId, 7)
                : AprDecisions.Accept(submission.ExecutionId, submission.SubmissionId)));
        var progress = new List<SolusAgent.Api.Execution.AgentProgress>();

        var run = await AprFixtures.WithTimeout(startup.Host.RunAsync(
            AprFixtures.ContinueRun(execution, supplied.ToEnvelope(AprScenarioAgent.ImplementationId)), sink,
            AprFixtures.Candidates(execution), feedback, new AprFixtures.InlineProgress(progress.Add)));

        // The synthetic credentials exist in their fixtures and were exercised by real hooks and provider calls.
        Assert.Equal("PRIVATE_PROVIDER_CREDENTIAL_CANARY", ConfigurationProvider.CredentialCanary);
        Assert.Equal("PRIVATE_HOST_CREDENTIAL_CANARY", ConfigurationHooks.CredentialCanary);
        Assert.NotEmpty(startup.Script.ModelVisibleTexts);
        Assert.Equal(3, startup.Script.ToolArguments.Count);

        var modelVisible = string.Join('\n', startup.Script.ModelVisibleTexts);
        var toolData = string.Join('\n', startup.Script.ToolArguments);
        var saved = AprFixtures.RestrictedText(sink);
        foreach (var canary in new[] { ConfigurationProvider.CredentialCanary, ConfigurationHooks.CredentialCanary })
        {
            Assert.DoesNotContain(canary, modelVisible, StringComparison.Ordinal);
            Assert.DoesNotContain(canary, toolData, StringComparison.Ordinal);
            Assert.DoesNotContain(canary, saved, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(ConfigurationProvider.CredentialCanary,
            OrdinarySurfaces(run, progress, startup), StringComparison.Ordinal);
    }

    private static string OrdinarySurfaces(AprHostRun run, IReadOnlyList<SolusAgent.Api.Execution.AgentProgress> progress, AprStartup startup)
    {
        var surfaces = new List<string>
        {
            Text(run),
            Text(run.Context!),
            Text(run.Context!.Outcome!),
            Text(run.Candidates!),
            Text(run.Candidates!.Outcome),
            Text(run.Acceptance),
            Text(startup.Configuration.Describe()),
        };
        surfaces.AddRange(run.Candidates!.Receipts.Select(Text));
        surfaces.AddRange(progress.Select(observation => JsonSerializer.Serialize(observation)));
        surfaces.AddRange(startup.Scenario.RecordedAttempts.Select(attempt => Text(attempt.Diagnostic)));
        return string.Join('\n', surfaces);
    }

    private static string Text(object value) => value.ToString() ?? string.Empty;
}

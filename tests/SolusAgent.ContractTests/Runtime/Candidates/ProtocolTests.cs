using System.Text.Json;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Candidates;

public sealed class ProtocolTests
{
    [Theory]
    [InlineData(CandidateDecision.Accept, CandidateContinuation.End, CandidateStopReason.Completed)]
    [InlineData(CandidateDecision.Reject, CandidateContinuation.End, CandidateStopReason.HostEnded)]
    [InlineData(CandidateDecision.Accept, CandidateContinuation.Continue, CandidateStopReason.ContinuationLimit)]
    [InlineData(CandidateDecision.Reject, CandidateContinuation.Continue, CandidateStopReason.RepairLimit)]
    public async Task FourHostCombinationsAtExactWorkAndSubmissionBounds(CandidateDecision decision, CandidateContinuation instruction,
        CandidateStopReason expected)
    {
        var provider = CandidateFixture.Provider();
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s, decision, instruction));
        var more = instruction == CandidateContinuation.Continue;
        var result = (await CandidateConsumer.RunAsync(CandidateFixture.Agent(provider),
            CandidateFixture.Request(units: more ? 8 : 1, submissions: more ? 8 : 1, repairs: 0, continuations: 0), host)).Result;
        Assert.Equal(expected, result.StopReason); Assert.Equal(1, result.Outcome.CompletedWorkUnits);
        Assert.Equal(decision == CandidateDecision.Accept ? 1 : 0, result.AcceptedCount);
        Assert.Equal(0, result.RepairsAdmitted + result.ContinuationsAdmitted);
        Assert.Equal(1, provider.Effects); Assert.Single(host.Submissions);
        Assert.Equal(decision, result.Receipts.Single().Decision); Assert.Equal(instruction, result.Receipts[0].Continuation);
        Assert.Equal(3, result.Outcome.Usage!.Attempts.Single().Usage.InputTokens);
    }

    [Fact]
    public async Task HostCorrectionCausallyChangesRealProviderRequestAndRepairThenAcceptedContinuation()
    {
        var a = await Run("CORRECTION_A change policy"); var b = await Run("CORRECTION_B change tools");
        Assert.NotEqual(a.Host.Submissions[1].Payload, b.Host.Submissions[1].Payload);
        Assert.Equal("repair:CORRECTION_A change policy", a.Host.Submissions[1].Payload);
        Assert.Equal("repair:CORRECTION_B change tools", b.Host.Submissions[1].Payload);
        foreach (var run in new[] { a, b })
        {
            var result = run.Result;
            Assert.Equal(CandidateStopReason.Completed, result.StopReason); Assert.Equal(3, result.Outcome.CompletedWorkUnits);
            Assert.Equal(2, result.AcceptedCount); Assert.Equal(1, result.RepairsAdmitted); Assert.Equal(1, result.ContinuationsAdmitted);
            Assert.Equal(3, result.Outcome.Usage!.Attempts.Count); Assert.All(result.Outcome.Usage.Attempts, x => Assert.Equal(3, x.Usage.InputTokens));
            Assert.Equal(3, run.Host.Submissions.Select(s => s.SubmissionId).Distinct().Count());
            Assert.Equal(run.Host.Submissions[0].SubmissionId, run.Host.Submissions[1].RepairsSubmissionId);
            Assert.Null(run.Host.Submissions[0].RepairsSubmissionId); Assert.Null(run.Host.Submissions[2].RepairsSubmissionId);
            var safe = JsonSerializer.Serialize(result) + JsonSerializer.Serialize(run.Progress);
            Assert.DoesNotContain("CORRECTION_", safe); Assert.DoesNotContain("PAYLOAD_CANARY", safe);
            Assert.DoesNotContain("CONTINUATION_CANARY", safe); Assert.DoesNotContain("SCRIPTED_PRIVATE_CREDENTIAL_CANARY", safe);
        }
        async Task<(CandidateExecutionResult Result, ScriptedCandidateHost Host, IReadOnlyList<AgentProgress> Progress)> Run(string correction)
        {
            ProviderContinuation? prior = null;
            var provider = new ScriptedProvider([
                (r, o, _) => { o.CaptureUsage(new(3, 2)); prior = new(r.Scope, r.Attempt, "CONTINUATION_CANARY"u8);
                    return ValueTask.FromResult(RuntimeFixture.Final(r, "PAYLOAD_CANARY", prior)); },
                (r, o, _) => { o.CaptureUsage(new(3, 2)); Assert.Same(prior, r.Continuation);
                    Assert.Equal("i", r.Inputs[0].Text); Assert.Equal(ProviderInputKind.HostInstruction, r.Inputs[0].Kind);
                    Assert.Equal(correction, r.Inputs[^1].Text); Assert.Equal(ProviderInputKind.InputData, r.Inputs[^1].Kind);
                    Assert.Equal("PAYLOAD_CANARY", r.Inputs[1].Model!.Text); Assert.Empty(r.Tools);
                    Assert.Equal("p", r.Scope.Provider); Assert.Equal("m", r.Scope.Model);
                    return ValueTask.FromResult(RuntimeFixture.Final(r, "repair:" + r.Inputs[^1].Text)); },
                ScriptedProvider.Final]);
            var count = 0;
            var host = new ScriptedCandidateHost((s, _) => ++count switch
            {
                1 => CandidateFixture.Feedback(s, CandidateDecision.Reject, CandidateContinuation.Continue, correction),
                2 => CandidateFixture.Feedback(s, instruction: CandidateContinuation.Continue),
                _ => CandidateFixture.Feedback(s),
            });
            var observed = await CandidateConsumer.RunAsync(CandidateFixture.Agent(provider, options: new(requireContinuation: true)),
                CandidateFixture.Request(units: 3, submissions: 3, repairs: 1, continuations: 1), host);
            return (observed.Result, host, observed.Progress);
        }
    }

    [Theory]
    [InlineData("missing", CandidateStopReason.MissingAcknowledgement, CandidateAcknowledgement.Missing)]
    [InlineData("failed", CandidateStopReason.FailedAcknowledgement, CandidateAcknowledgement.Failed)]
    [InlineData("throw", CandidateStopReason.FailedAcknowledgement, CandidateAcknowledgement.Failed)]
    [InlineData("async-fault", CandidateStopReason.FailedAcknowledgement, CandidateAcknowledgement.Failed)]
    [InlineData("unknown", CandidateStopReason.UnknownAcknowledgement, CandidateAcknowledgement.Unknown)]
    [InlineData("wrong-run", CandidateStopReason.MismatchedFeedback, CandidateAcknowledgement.Mismatched)]
    [InlineData("wrong-id", CandidateStopReason.MismatchedFeedback, CandidateAcknowledgement.Mismatched)]
    [InlineData("duplicate", CandidateStopReason.DuplicateFeedback, CandidateAcknowledgement.Duplicate)]
    public async Task UncertainOrInvalidFeedbackPreservesEarlierAcceptanceAndEffects(string mode, CandidateStopReason stop,
        CandidateAcknowledgement acknowledgement)
    {
        CandidateSubmission? first = null;
        ScriptedCandidateHost? host = null;
        host = new((s, _) =>
        {
            host!.ApplyEffect();
            if (first is null) { first = s; return CandidateFixture.Feedback(s, instruction: CandidateContinuation.Continue); }
            return mode switch
            {
                "missing" => ValueTask.FromResult<CandidateFeedback?>(null),
                "throw" => throw new InvalidOperationException("EXCEPTION_CANARY"),
                "async-fault" => new(Task.FromException<CandidateFeedback?>(new InvalidOperationException("EXCEPTION_CANARY"))),
                "failed" or "unknown" => ValueTask.FromResult<CandidateFeedback?>(new(s.ExecutionId, s.SubmissionId,
                    mode == "failed" ? CandidateAcknowledgement.Failed : CandidateAcknowledgement.Unknown)),
                _ => ValueTask.FromResult<CandidateFeedback?>(new(mode == "wrong-run" ? Guid.NewGuid() : s.ExecutionId,
                    mode == "duplicate" ? first.SubmissionId : Guid.NewGuid(), CandidateAcknowledgement.Acknowledged,
                    CandidateDecision.Accept, CandidateContinuation.Continue, "FOREIGN_CORRECTION_CANARY")),
            };
        });
        var provider = CandidateFixture.Provider();
        var result = (await CandidateConsumer.RunAsync(CandidateFixture.Agent(provider), CandidateFixture.Request(), host)).Result;
        Assert.Equal(stop, result.StopReason); Assert.Equal(1, result.AcceptedCount); Assert.Equal(2, result.Receipts.Count);
        Assert.Equal(acknowledgement, result.Receipts[^1].Acknowledgement); Assert.Null(result.Receipts[^1].Decision);
        Assert.Equal(host.Submissions[1].SubmissionId, result.Receipts[^1].SubmissionId);
        Assert.Equal(2, host.Effects); Assert.Equal(2, provider.Effects); Assert.Equal(1, result.ContinuationsAdmitted);
        Assert.Equal(2, result.Outcome.Usage!.Attempts.Count); Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task ProgressPrecedesHostAndItsFailureRetainsAcceptedProviderWorkOnly()
    {
        var provider = CandidateFixture.Provider(); var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s));
        var result = await CandidateFixture.Agent(provider).ExecuteCandidatesAsync(CandidateFixture.Request(), host,
            new InlineProgress(p => { Assert.Empty(host.Submissions); Assert.Equal(1, p.CompletedWorkUnits);
                throw new InvalidOperationException("PROGRESS_EXCEPTION_CANARY"); }));
        Assert.Equal(CandidateStopReason.ProgressObserverFailed, result.StopReason); Assert.Empty(result.Receipts);
        Assert.Equal(1, result.Outcome.CompletedWorkUnits); Assert.Equal(3, result.Outcome.Usage!.Attempts.Single().Usage.InputTokens);
        Assert.Equal(0, host.Effects); Assert.Empty(host.Submissions); Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NullRejectionCorrectionStillRepairsAndAcceptanceCorrectionDoesNotBecomeData(bool reject)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final, (r, o, _) =>
        {
            o.CaptureUsage(new(3, 2)); Assert.Equal(2, r.Inputs.Count);
            Assert.Equal(ProviderInputKind.ModelData, r.Inputs[^1].Kind); Assert.Equal("i", r.Inputs[0].Text);
            return ValueTask.FromResult(RuntimeFixture.Final(r));
        }]);
        var count = 0;
        var host = new ScriptedCandidateHost((s, _) => ++count == 1 ? CandidateFixture.Feedback(s,
            reject ? CandidateDecision.Reject : CandidateDecision.Accept, CandidateContinuation.Continue,
            reject ? null : "ACCEPT_CORRECTION_CANARY") : CandidateFixture.Feedback(s));
        var result = await CandidateFixture.Agent(provider).ExecuteCandidatesAsync(CandidateFixture.Request(), host);
        Assert.Equal(CandidateStopReason.Completed, result.StopReason); Assert.Equal(2, provider.Effects);
        Assert.Equal(reject ? 1 : 0, result.RepairsAdmitted); Assert.Equal(reject ? 0 : 1, result.ContinuationsAdmitted);
        Assert.Equal(reject ? host.Submissions[0].SubmissionId : (Guid?)null, host.Submissions[1].RepairsSubmissionId);
    }

    [Theory]
    [InlineData(AgentCapability.UsageThresholds)] [InlineData(AgentCapability.DispatchLimits | AgentCapability.UsageThresholds)]
    public async Task UnsupportedRequirementsWinBeforeCancellationAndAllEffects(AgentCapability required)
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var hooks = new RuntimeHooks(); var provider = CandidateFixture.Provider();
        var host = new ScriptedCandidateHost((_, _) => throw new InvalidOperationException());
        var result = await CandidateFixture.Agent(provider, hooks).ExecuteCandidatesAsync(CandidateFixture.Request(required: required), host,
            new InlineProgress(_ => throw new InvalidOperationException()), cancellation.Token);
        Assert.Equal(CandidateStopReason.UnsupportedCapability, result.StopReason); Assert.Equal(AgentCapability.UsageThresholds, result.Outcome.UnsupportedCapabilities);
        Assert.Empty(result.Outcome.Usage!.Attempts); Assert.Empty(result.Receipts); Assert.Empty(hooks.Exposures); Assert.Empty(host.Submissions);
        Assert.Equal(0, provider.Effects);
    }
}

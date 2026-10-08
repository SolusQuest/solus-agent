using CustomTools;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Candidates;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Tools;

public sealed class RuntimeEntryIntegrationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OneFactoryInstancePreservesBothEntryPathsAndFreshHistoryInEitherOrder(bool candidateFirst)
    {
        var cap = new CounterCapability(); var tool = new CounterTool(maximumResultBytes: 64); var id = Guid.NewGuid();
        ProviderRequest? ordinaryNext = null, candidateNext = null; ProviderContinuation? candidateReplay = null;
        Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>>[] ordinarySteps = [
            (r, o, _) => { Assert.Single(r.Inputs); Assert.Null(r.Continuation); return ToolFixture.Calls(r, o, [ToolFixture.Counter("same")]); },
            (r, o, token) => { ordinaryNext = r; Assert.Equal(1, cap.Effects); return ScriptedProvider.Final(r, o, token); }];
        Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>>[] candidateSteps = [
            (r, o, _) => { Assert.Single(r.Inputs); Assert.Null(r.Continuation); o.CaptureUsage(new(3, 2));
                candidateReplay = new(r.Scope, r.Attempt, [9, 8]); return ValueTask.FromResult(RuntimeFixture.Final(r, "candidate", candidateReplay)); },
            (r, o, _) => { candidateNext = r; o.CaptureUsage(new(3, 2)); Assert.Same(candidateReplay, r.Continuation);
                Assert.Equal("correction", r.Inputs[^1].Text); return ValueTask.FromResult(RuntimeFixture.Final(r, "repair:" + r.Inputs[^1].Text)); }];
        var provider = new ScriptedProvider(candidateFirst ? [.. candidateSteps, .. ordinarySteps] : [.. ordinarySteps, .. candidateSteps]);
        var agent = ToolFixture.Agent(provider, [new(tool, cap)], new(requireContinuation: true)); var candidateAgent = Assert.IsAssignableFrom<ICandidateAgent>(agent);
        var submitted = 0; var host = new ScriptedCandidateHost((submission, _) => ++submitted == 1
            ? CandidateFixture.Feedback(submission, CandidateDecision.Reject, CandidateContinuation.Continue, "correction")
            : CandidateFixture.Feedback(submission));
        AgentOutcome ordinary; CandidateExecutionResult candidate;
        if (candidateFirst)
        { candidate = await RunCandidate(); ordinary = await agent.ExecuteAsync(RuntimeFixture.Request(2, executionId: id)); }
        else
        { ordinary = await agent.ExecuteAsync(RuntimeFixture.Request(2, executionId: id)); candidate = await RunCandidate(); }
        Assert.Equal(AgentTerminationReason.Completed, ordinary.Reason); Assert.Equal(CandidateStopReason.Completed, candidate.StopReason);
        Assert.Equal(2, ordinary.CompletedWorkUnits); Assert.Equal(2, candidate.Outcome.CompletedWorkUnits); Assert.Equal(1, candidate.RepairsAdmitted);
        Assert.Equal(2, host.Submissions.Count); Assert.Equal("repair:correction", host.Submissions[1].Payload);
        Assert.Equal(host.Submissions[0].SubmissionId, host.Submissions[1].RepairsSubmissionId);
        Assert.Equal(1, cap.Effects); Assert.Equal(4, provider.Effects);
        Assert.Equal(new[] { ProviderInputKind.HostInstruction, ProviderInputKind.ModelData, ProviderInputKind.ToolResultData }, ordinaryNext!.Inputs.Select(input => input.Kind));
        Assert.Equal(new[] { ProviderInputKind.HostInstruction, ProviderInputKind.ModelData, ProviderInputKind.InputData }, candidateNext!.Inputs.Select(input => input.Kind));
        Assert.Equal("{\"total\":1}", ordinaryNext.Inputs[^1].ToolResult!.Json);
        Assert.NotEqual(ordinary.Usage!.Attempts[0].LogicalCallId, candidate.Outcome.Usage!.Attempts[0].LogicalCallId);
        Assert.All(ordinary.Usage.Attempts.Concat(candidate.Outcome.Usage.Attempts), attempt => Assert.Equal(3, attempt.Usage.InputTokens));
        ValueTask<CandidateExecutionResult> RunCandidate() => candidateAgent.ExecuteCandidatesAsync(
            CandidateFixture.Request(units: 2, submissions: 2, repairs: 1, continuations: 0, executionId: id), host);
    }
}

using CustomTools;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.ContractTests.Runtime.Tools;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Candidates;

public sealed class RetryCompositionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RetriesPreserveToolHistoryContinuationAndCandidateEpisodeBoundaries(bool candidate)
    {
        var a = await Run("CORRECTION_A"); var b = await Run("CORRECTION_B");
        if (candidate) Assert.NotEqual(a.Submissions[1].Payload, b.Submissions[1].Payload);

        async Task<ScriptedCandidateHost> Run(string correction)
        {
            var requests = new List<ProviderRequest>(); var tools = new CounterCapability();
            var provider = new DelegateProvider(new("p", "m"), (r, o, _) =>
            {
                requests.Add(r); o.ObserveDispatch(DispatchExposure.Dispatched); o.CaptureUsage(new(3, 2));
                if (r.Attempt.AttemptNumber == 1) throw new ProviderFailureException(new(ProviderRetryKind.Transient));
                var continuation = new ProviderContinuation(r.Scope, r.Attempt, [0, 255, (byte)requests.Count]);
                if (requests.Count == 2) return ValueTask.FromResult(new ProviderResponse(r.Scope, r.Attempt, ProviderFinish.ToolCalls,
                    null, [ToolFixture.Counter()], continuation));
                var data = r.Inputs.LastOrDefault(input => input.Kind == ProviderInputKind.InputData)?.Text;
                return ValueTask.FromResult(RuntimeFixture.Final(r, data is null ? "first" : "repair:" + data, continuation));
            });
            var agent = AccountingTests.Agent(provider, new AccountingHostProbe { ReverseAccountingInventory = true }, tools,
                options: new(requireContinuation: true));
            var request = RetryTests.Request(new(maximumLogicalCalls: candidate ? 4 : 2, maximumPhysicalDispatches: candidate ? 8 : 4,
                maximumToolInvocations: 1, accountingPolicy: new(new(5, 4), 100, 100), retryPolicy: new(2)), units: candidate ? 4 : 2);
            var feedbackCount = 0; ScriptedCandidateHost? host = null;
            host = new((s, _) =>
            {
                host!.ApplyEffect();
                return ++feedbackCount switch
                {
                    1 => CandidateFixture.Feedback(s, CandidateDecision.Reject, CandidateContinuation.Continue, correction),
                    2 => CandidateFixture.Feedback(s, instruction: CandidateContinuation.Continue),
                    _ => CandidateFixture.Feedback(s),
                };
            });
            AgentOutcome result;
            if (candidate)
            {
                var observed = await CandidateConsumer.RunAsync((ICandidateAgent)agent, new(request, new(3, 1, 1)), host);
                result = observed.Result.Outcome;
                Assert.Equal(1, observed.Result.RepairsAdmitted); Assert.Equal(1, observed.Result.ContinuationsAdmitted);
                Assert.Equal(3, host.Submissions.Count); Assert.Equal(3, host.Effects);
                Assert.Equal("repair:" + correction, host.Submissions[1].Payload);
                Assert.Equal(host.Submissions[0].SubmissionId, host.Submissions[1].RepairsSubmissionId);
                Assert.Equal(4, observed.Progress.Count);
                Assert.All(requests.Skip(4), r => Assert.Single(r.Inputs, input => input.Text == correction));
                Assert.Equal(new[] { 1, 1, 3, 3, 5, 5, 6, 6 }, requests.Select(r => r.Inputs.Count));
            }
            else result = await agent.ExecuteAsync(request);
            Assert.Equal(AgentTerminationReason.Completed, result.Reason); Assert.Equal(candidate ? 4 : 2, result.CompletedWorkUnits);
            Assert.Equal(1, tools.Effects); Assert.Equal(candidate ? 8 : 4, requests.Count);
            Assert.Equal(requests.Count, result.Usage!.Attempts.Count);
            Assert.Equal(requests.Count * 3, result.Usage.Accounting!.Input.MeasuredTokens);
            Assert.Equal(candidate ? 4 : 2, requests.Select(r => r.Attempt.LogicalCallId).Distinct().Count());
            for (var index = 0; index < requests.Count; index += 2)
            {
                var first = requests[index]; var retry = requests[index + 1];
                Assert.Equal(first.Attempt.LogicalCallId, retry.Attempt.LogicalCallId);
                Assert.NotEqual(first.Attempt.PhysicalAttemptId, retry.Attempt.PhysicalAttemptId);
                Assert.Equal(1, first.Attempt.AttemptNumber); Assert.Equal(2, retry.Attempt.AttemptNumber);
                Assert.Equal(first.Inputs, retry.Inputs); Assert.Equal(first.Tools, retry.Tools);
                Assert.Same(first.Continuation, retry.Continuation); Assert.Same(first.Bounds, retry.Bounds);
                Assert.Equal(first.RequiredCapabilities, retry.RequiredCapabilities); Assert.Same(first.Scope, retry.Scope);
                Assert.NotSame(first.Observation, retry.Observation);
                if (index > 0)
                {
                    Assert.NotNull(first.Continuation);
                    Assert.True(requests[index - 1].Attempt.Matches(first.Continuation.Origin));
                    Assert.True(first.Inputs.Last(i => i.Model is not null).Model!.Attempt.Matches(first.Continuation.Origin));
                }
            }
            return host;
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExhaustedLogicalOrWorkAllowanceStillDeniesNewProductionAfterSuccessfulRetry(bool candidate)
    {
        foreach (var workLimit in new[] { false, true })
        {
            var tools = new CounterCapability();
            var provider = new ScriptedProvider([ScriptedProvider.Failure(new(ProviderRetryKind.Transient)),
                (r, o, _) => ToolFixture.Calls(r, o, [ToolFixture.Counter()]), ScriptedProvider.Final]);
            var result = await AccountingTests.Execute(AccountingTests.Agent(provider, new RuntimeHooks(), tools),
                RetryTests.Request(new(maximumLogicalCalls: workLimit ? 8 : 1, retryPolicy: new(2)), units: workLimit ? 1 : 8), candidate);
            Assert.Equal(AgentTerminationReason.ResourceLimit, result.Reason); Assert.Equal(2, provider.Effects);
            Assert.Equal(1, result.CompletedWorkUnits); Assert.Equal(1, tools.Effects); Assert.Equal(2, result.Usage!.Attempts.Count);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ObserverFailureAfterRetryPreservesAcceptedWorkWithoutReplay(bool candidate)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Failure(new(ProviderRetryKind.Transient)), ScriptedProvider.Final, ScriptedProvider.Final]);
        var agent = AccountingTests.Agent(provider, new RuntimeHooks()); var progress = new InlineProgress(_ => throw new InvalidOperationException());
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s));
        var request = RetryTests.Request();
        var result = candidate ? (await ((ICandidateAgent)agent).ExecuteCandidatesAsync(new(request, new(2, 1, 1)), host, progress)).Outcome
            : await agent.ExecuteAsync(request, progress);
        Assert.Equal(AgentFailureCode.ProgressObserverFailed, result.FailureCode); Assert.Equal(AgentTerminationReason.Failed, result.Reason);
        Assert.Equal(2, provider.Effects); Assert.Equal(1, result.CompletedWorkUnits); Assert.Equal(2, result.Usage!.Attempts.Count);
        Assert.Empty(host.Submissions);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OverlappingEqualExecutionIdsDoNotShareRetryPolicyOrLedger(bool candidate)
    {
        var entered = RuntimeFixture.Barrier(); var release = RuntimeFixture.Barrier(); var firstAttempts = 0;
        var provider = new DelegateProvider(new("p", "m"), async (r, o, _) =>
        {
            o.ObserveDispatch(DispatchExposure.Dispatched); o.CaptureUsage(new(3, 2));
            if (r.Attempt.AttemptNumber == 1)
            { if (Interlocked.Increment(ref firstAttempts) == 2) entered.SetResult(); await release.Task; }
            if (r.Attempt.AttemptNumber < 3) throw new ProviderFailureException(new(ProviderRetryKind.Transient));
            return RuntimeFixture.Final(r);
        });
        var agent = AccountingTests.Agent(provider, new RuntimeHooks()); var id = Guid.NewGuid();
        var a = AccountingTests.Execute(agent, RetryTests.Request(new(accountingPolicy: new(new(5, 4), 100, 100), retryPolicy: new(2)), id: id), candidate);
        var b = AccountingTests.Execute(agent, RetryTests.Request(new(accountingPolicy: new(new(5, 4), 100, 100), retryPolicy: new(3)), id: id), candidate);
        await RuntimeFixture.Await(entered.Task); Assert.False(a.IsCompleted); Assert.False(b.IsCompleted); release.SetResult();
        var outcomes = await RuntimeFixture.Await(Task.WhenAll(a, b));
        Assert.Equal(AgentTerminationReason.Failed, outcomes[0].Reason); Assert.Equal(AgentTerminationReason.Completed, outcomes[1].Reason);
        Assert.Equal(6, outcomes[0].Usage!.Accounting!.Input.MeasuredTokens); Assert.Equal(9, outcomes[1].Usage!.Accounting!.Input.MeasuredTokens);
        Assert.NotEqual(outcomes[0].Usage!.Attempts[0].LogicalCallId, outcomes[1].Usage!.Attempts[0].LogicalCallId);
    }
}

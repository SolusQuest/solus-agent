using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Candidates;

public sealed class BoundsAndSettlementTests
{
    [Theory]
    [InlineData("work", CandidateStopReason.WorkUnitLimit)]
    [InlineData("submissions", CandidateStopReason.SubmissionLimit)]
    [InlineData("attempts", CandidateStopReason.RuntimeLimit)]
    [InlineData("records", CandidateStopReason.RuntimeLimit)]
    public async Task ExactCeilingPreservesAcknowledgementAndBlocksOnlyNextAdmission(string bound, CandidateStopReason stop)
    {
        var provider = CandidateFixture.Provider(); var hooks = new RuntimeHooks();
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s, instruction: CandidateContinuation.Continue));
        var options = new RuntimeOptions(maximumAttempts: bound == "attempts" ? 2 : 64, maximumRecords: bound == "records" ? 3 : 64);
        var result = await CandidateFixture.Agent(provider, hooks, options).ExecuteCandidatesAsync(
            CandidateFixture.Request(units: bound == "work" ? 2 : 8, submissions: bound == "submissions" ? 2 : 8), host);
        Assert.Equal(stop, result.StopReason); Assert.Equal(2, result.AcceptedCount); Assert.Equal(2, result.Outcome.CompletedWorkUnits);
        Assert.Equal(1, result.ContinuationsAdmitted); Assert.Equal(2, provider.Effects); Assert.Equal(2, host.Submissions.Count);
        Assert.Equal(2, hooks.Exposures.Count); Assert.Equal(2, result.Outcome.Usage!.Attempts.Count);
    }

    [Theory]
    [InlineData(CandidateDecision.Accept, 0)] [InlineData(CandidateDecision.Reject, 0)]
    [InlineData(CandidateDecision.Accept, 1)] [InlineData(CandidateDecision.Reject, 1)]
    [InlineData(CandidateDecision.Accept, 2)] [InlineData(CandidateDecision.Reject, 2)]
    public async Task ZeroAndExactFollowOnAllowancesCountOnlyAdmittedProduction(CandidateDecision decision, int allowance)
    {
        var provider = CandidateFixture.Provider();
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s, decision, CandidateContinuation.Continue,
            decision == CandidateDecision.Reject ? "fix" : null));
        var result = await CandidateFixture.Agent(provider).ExecuteCandidatesAsync(CandidateFixture.Request(repairs: allowance,
            continuations: allowance), host);
        Assert.Equal(decision == CandidateDecision.Reject ? CandidateStopReason.RepairLimit : CandidateStopReason.ContinuationLimit, result.StopReason);
        Assert.Equal(allowance + 1, provider.Effects); Assert.Equal(allowance + 1, result.Receipts.Count);
        Assert.Equal(allowance, decision == CandidateDecision.Reject ? result.RepairsAdmitted : result.ContinuationsAdmitted);
        Assert.Equal(allowance + 1, result.Outcome.CompletedWorkUnits);
        Assert.Equal(decision == CandidateDecision.Accept ? allowance + 1 : 0, result.AcceptedCount);
    }

    [Theory]
    [InlineData(CandidateDecision.Accept)] [InlineData(CandidateDecision.Reject)]
    public async Task FailedAlreadyAdmittedFollowOnKeepsCountsReceiptsAndUsage(CandidateDecision decision)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final, (_, observation, _) =>
        { observation.CaptureUsage(new(9, null)); throw new InvalidOperationException("PROVIDER_EXCEPTION_CANARY"); }]);
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s, decision, CandidateContinuation.Continue, "fix"));
        var result = await CandidateFixture.Agent(provider).ExecuteCandidatesAsync(CandidateFixture.Request(), host);
        Assert.Equal(CandidateStopReason.ProductionFailed, result.StopReason); Assert.Equal(1, result.Outcome.CompletedWorkUnits);
        Assert.Single(result.Receipts); Assert.Equal(1, result.RepairsAdmitted + result.ContinuationsAdmitted);
        Assert.Equal(decision == CandidateDecision.Accept ? 1 : 0, result.AcceptedCount);
        Assert.Equal(2, provider.Effects); Assert.Single(host.Submissions);
        Assert.Equal(3, result.Outcome.Usage!.Attempts[0].Usage.InputTokens); Assert.Equal(9, result.Outcome.Usage.Attempts[1].Usage.InputTokens);
        Assert.Null(result.Outcome.Usage.Attempts[1].Usage.OutputTokens);
    }

    [Theory]
    [InlineData("record", 3)] [InlineData("record", 4)]
    [InlineData("inputs", 2)] [InlineData("inputs", 3)]
    [InlineData("request-bytes", 7)] [InlineData("request-bytes", 8)]
    [InlineData("retained-bytes", 7)] [InlineData("retained-bytes", 8)]
    [InlineData("retained-bytes", 10)] [InlineData("retained-bytes", 11)]
    public async Task CorrectionCapacityNeighborsChargeRealUtf8AndReserveNextResponse(string bound, int capacity)
    {
        // Initial request is 3 bytes (p,m,i), each Final 3 bytes (p,m,f); correction é is 2 bytes.
        var provider = CandidateFixture.Provider(); var calls = 0;
        var host = new ScriptedCandidateHost((s, _) => ++calls == 1
            ? CandidateFixture.Feedback(s, CandidateDecision.Reject, CandidateContinuation.Continue, "é")
            : CandidateFixture.Feedback(s));
        var options = new RuntimeOptions(maximumRecords: bound == "record" ? capacity : 64,
            maximumRetainedBytes: bound == "retained-bytes" ? capacity : 196608);
        var bounds = new ProviderExchangeBounds(maximumInputs: bound == "inputs" ? capacity : ProviderLimits.Inputs,
            maximumRequestBytes: bound == "request-bytes" ? capacity : ProviderLimits.RequestBytes);
        var result = await CandidateFixture.Agent(provider, options: options, bounds: bounds).ExecuteCandidatesAsync(CandidateFixture.Request(), host);
        var succeeds = bound switch { "record" => capacity == 4, "inputs" => capacity == 3, "request-bytes" => capacity == 8,
            _ => capacity == 11 };
        Assert.Equal(succeeds ? CandidateStopReason.Completed : CandidateStopReason.RuntimeLimit, result.StopReason);
        var admitted = succeeds || bound == "retained-bytes" && capacity == 10;
        Assert.Equal(admitted ? 2 : 1, provider.Effects); Assert.Equal(succeeds ? 2 : 1, result.Receipts.Count);
        Assert.Equal(admitted ? 1 : 0, result.RepairsAdmitted);
        Assert.Equal(3, result.Outcome.Usage!.Attempts[0].Usage.InputTokens);
    }

    [Theory]
    [InlineData("continue", CandidateStopReason.Completed, 1)]
    [InlineData("stop", CandidateStopReason.ProductionStopped, 0)]
    [InlineData("missing", CandidateStopReason.ProductionFailed, 0)]
    [InlineData("unknown", CandidateStopReason.ProductionFailed, 0)]
    [InlineData("failed", CandidateStopReason.ProductionFailed, 0)]
    [InlineData("wrong", CandidateStopReason.ProductionFailed, 0)]
    [InlineData("throw", CandidateStopReason.ProductionFailed, 0)]
    public async Task SettlementMustPermitCandidateDeliveryEvenForAcceptedFinal(string mode, CandidateStopReason stop, int submissions)
    {
        var hooks = new RuntimeHooks { After = (s, _) => mode switch
        {
            "continue" => ValueTask.FromResult<SettlementAcknowledgement?>(RuntimeHooks.Continue(s)),
            "stop" => ValueTask.FromResult<SettlementAcknowledgement?>(new(s.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Stop)),
            "missing" => ValueTask.FromResult<SettlementAcknowledgement?>(null),
            "unknown" or "failed" => ValueTask.FromResult<SettlementAcknowledgement?>(new(s.Exposure,
                mode == "unknown" ? RuntimeHookStatus.Unknown : RuntimeHookStatus.Failed)),
            "wrong" => ValueTask.FromResult<SettlementAcknowledgement?>(new(new(s.Exposure.Scope,
                new(s.Exposure.Attempt.ExecutionId, Guid.NewGuid(), Guid.NewGuid()), s.Exposure.RequiredAcknowledgement),
                RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue)),
            _ => throw new InvalidOperationException("SETTLEMENT_EXCEPTION_CANARY"),
        } };
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s)); var provider = CandidateFixture.Provider();
        var result = await CandidateFixture.Agent(provider, hooks).ExecuteCandidatesAsync(CandidateFixture.Request(), host);
        Assert.Equal(stop, result.StopReason); Assert.Equal(submissions, host.Submissions.Count);
        Assert.Equal(1, result.Outcome.CompletedWorkUnits); Assert.Equal(3, result.Outcome.Usage!.Attempts.Single().Usage.InputTokens);
        Assert.Single(hooks.Settlements); Assert.Equal(1, provider.Effects);
    }

    [Fact]
    public async Task ExposureDeniedIsProductionStopWithNoInventedHostReceipt()
    {
        var hooks = new RuntimeHooks { Before = (e, _) => ValueTask.FromResult<ExposureAcknowledgement?>(
            new(e, RuntimeHookStatus.Acknowledged, ExposureDecision.Deny, e.RequiredAcknowledgement)) };
        var provider = CandidateFixture.Provider(); var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s));
        var result = await CandidateFixture.Agent(provider, hooks).ExecuteCandidatesAsync(CandidateFixture.Request(), host);
        Assert.Equal(CandidateStopReason.ProductionStopped, result.StopReason); Assert.Empty(result.Receipts); Assert.Equal(0, provider.Effects);
        Assert.Empty(host.Submissions); Assert.Equal(0, result.Outcome.CompletedWorkUnits); Assert.Single(result.Outcome.Usage!.Attempts);
    }
}

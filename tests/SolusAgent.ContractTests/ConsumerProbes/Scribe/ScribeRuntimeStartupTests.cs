using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ConsumerProbes.ScribeHost;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.ConsumerProbes.Scribe;

/// <summary>AC4: actual provider/tool/configuration startup with ordered exposure, retained usage and settlement identity.</summary>
public sealed class ScribeRuntimeStartupTests
{
    [Fact]
    public async Task ValidExposureAcknowledgementPrecedesDispatchAndSettlementCarriesFullAttemptIdentity()
    {
        var host = ScribeFixtures.CreateHost([new ScribeExchangePlan(true)]);
        var request = host.CreateFreshRequest().Request;
        var runtime = new ScribeRuntimeStartup(request);
        var attempt = new ProviderAttempt(request.ExecutionId, Guid.NewGuid(), Guid.NewGuid());

        var result = await runtime.RunAttemptAsync(attempt);

        Assert.Equal(RuntimeStop.None, result.AdmissionStop);
        Assert.Equal(RuntimeStop.None, result.SettlementStop);
        Assert.Equal(new[] { "expose", "ack", "dispatch", "settle" }, runtime.Hooks.Events);
        Assert.NotNull(runtime.Hooks.LastSettlement);
        var settlement = runtime.Hooks.LastSettlement!;
        Assert.True(settlement.Exposure.Attempt.Matches(attempt));
        Assert.Equal(attempt.ExecutionId, settlement.Observation.ExecutionId);
        Assert.Equal(attempt.LogicalCallId, settlement.Observation.LogicalCallId);
        Assert.Equal(attempt.PhysicalAttemptId, settlement.Observation.PhysicalAttemptId);
        Assert.Equal(attempt.AttemptNumber, settlement.Observation.AttemptNumber);
        Assert.Equal(DispatchExposure.Dispatched, settlement.Observation.Exposure);
    }

    [Fact]
    public async Task HeldExposureAcknowledgementProvesZeroDispatchBeforePermission()
    {
        var host = ScribeFixtures.CreateHost([new ScribeExchangePlan(true)]);
        var request = host.CreateFreshRequest().Request;
        var runtime = new ScribeRuntimeStartup(request);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var permission = new TaskCompletionSource<ExposureAcknowledgement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        RuntimeExposure? observedExposure = null;
        runtime.Hooks.Exposure = (exposure, _) =>
        {
            observedExposure = exposure;
            started.TrySetResult(true);
            return new ValueTask<ExposureAcknowledgement?>(permission.Task);
        };

        var running = runtime.RunAttemptAsync(new ProviderAttempt(request.ExecutionId, Guid.NewGuid(), Guid.NewGuid())).AsTask();
        await started.Task;
        Assert.Equal(0, runtime.Provider.Effects);
        Assert.Equal(new[] { "expose" }, runtime.Hooks.Events);

        permission.SetResult(new ExposureAcknowledgement(observedExposure!, RuntimeHookStatus.Acknowledged,
            ExposureDecision.Permit, ExposureStrength.Volatile));
        var result = await running;

        Assert.Equal(RuntimeStop.None, result.AdmissionStop);
        Assert.Equal(1, runtime.Provider.Effects);
        Assert.Equal(new[] { "expose", "ack", "dispatch", "settle" }, runtime.Hooks.Events);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DeniedMissingAndUnknownExposureFailClosedWithoutDispatch(int mode)
    {
        var host = ScribeFixtures.CreateHost([new ScribeExchangePlan(true)]);
        var request = host.CreateFreshRequest().Request;
        var runtime = new ScribeRuntimeStartup(request);
        runtime.Hooks.Exposure = (exposure, _) => mode switch
        {
            0 => new ValueTask<ExposureAcknowledgement?>(new ExposureAcknowledgement(exposure, RuntimeHookStatus.Acknowledged, ExposureDecision.Deny)),
            1 => new ValueTask<ExposureAcknowledgement?>((ExposureAcknowledgement?)null),
            _ => new ValueTask<ExposureAcknowledgement?>(new ExposureAcknowledgement(exposure, RuntimeHookStatus.Unknown)),
        };

        var result = await runtime.RunAttemptAsync(new ProviderAttempt(request.ExecutionId, Guid.NewGuid(), Guid.NewGuid()));

        var expected = mode switch
        {
            0 => RuntimeStop.ExposureDenied,
            1 => RuntimeStop.ExposureMissing,
            _ => RuntimeStop.ExposureUnknown,
        };
        Assert.Equal(expected, result.AdmissionStop);
        Assert.Equal(0, runtime.Provider.Effects);
        Assert.Equal(DispatchExposure.NotDispatched, result.Observation.Exposure);
        Assert.Equal(UsageCompleteness.Unavailable, result.Observation.Usage.Completeness);

        // The actual stopped attempt projection is retained separately from ordinary diagnostics.
        var record = Assert.Single(runtime.Productions);
        Assert.Equal(expected, record.Attempt.Diagnostic.AdmissionStop);
    }

    [Fact]
    public async Task ExposureFailurePreventsCandidateProductionWithoutRepairLimitMislabeling()
    {
        var host = ScribeFixtures.CreateHost(
        [
            new ScribeExchangePlan(true, ScribeDelivery.Delivered, CandidateContinuation.Continue),
            new ScribeExchangePlan(false, ScribeDelivery.Delivered, CandidateContinuation.Continue),
            new ScribeExchangePlan(false, ScribeDelivery.Delivered, CandidateContinuation.Continue),
        ]);
        var round = new ScribeRound(host,
        [
            new ScribeProductionStep("intro", "INTRO_FACT_CANARY blocked production"),
            new ScribeProductionStep("usage", "USAGE_FACT_CANARY blocked production"),
            new ScribeProductionStep("usage", "USAGE_FACT_CANARY blocked production"),
        ]);
        round.Runtime.Hooks.Exposure = (_, _) => new ValueTask<ExposureAcknowledgement?>((ExposureAcknowledgement?)null);

        var observed = await round.RunAsync();

        Assert.Equal(CandidateStopReason.ProductionFailed, observed.Result.StopReason);
        Assert.Equal(AgentTerminationReason.Failed, observed.Result.Outcome.Reason);
        Assert.NotEqual(CandidateStopReason.RepairLimit, observed.Result.StopReason);
        Assert.Empty(observed.Result.Receipts);
        Assert.Equal(0, round.Runtime.Provider.Effects);
        var diagnostic = Assert.Single(round.Runtime.Diagnostics);
        Assert.Equal(RuntimeStop.ExposureMissing, diagnostic.AdmissionStop);
    }

    [Fact]
    public async Task RejectedCandidateRetainsKnownProviderUsageWhileUnavailableNeighborStaysUnknown()
    {
        var host = ScribeFixtures.CreateHost([new ScribeExchangePlan(false, ScribeDelivery.Delivered, CandidateContinuation.End)]);
        var round = new ScribeRound(host, [new ScribeProductionStep("intro", "INTRO_FACT_CANARY rejected after dispatch")]);
        var observed = await round.RunAsync();
        Assert.Equal(CandidateStopReason.HostEnded, observed.Result.StopReason);
        Assert.Empty(host.Progress.AcceptedFacts);

        // Honest usage: the known (3,2) provider observation survives the later candidate rejection.
        var known = Assert.Single(round.Runtime.Productions).Attempt.Observation;
        Assert.Equal(DispatchExposure.Dispatched, known.Exposure);
        Assert.Equal(3, known.Usage.InputTokens);
        Assert.Equal(2, known.Usage.OutputTokens);
        Assert.Equal(UsageCompleteness.Complete, known.Usage.Completeness);

        // The CaptureUsage=false neighbor stays unavailable rather than fabricating zero.
        var quietHost = ScribeFixtures.CreateHost([new ScribeExchangePlan(true)]);
        var quietRound = new ScribeRound(quietHost, [new ScribeProductionStep("intro", "INTRO_FACT_CANARY no measurement")], captureUsage: false);
        await quietRound.RunAsync();
        var unknown = Assert.Single(quietRound.Runtime.Productions).Attempt.Observation;
        Assert.Equal(DispatchExposure.Dispatched, unknown.Exposure);
        Assert.Equal(UsageCompleteness.Unavailable, unknown.Usage.Completeness);
        Assert.Null(unknown.Usage.InputTokens);
        Assert.Null(unknown.Usage.OutputTokens);
    }

    [Fact]
    public async Task UnknownSettlementRetainsDispatchedObservationAndStopsLaterAdmission()
    {
        var host = ScribeFixtures.CreateHost([new ScribeExchangePlan(true)]);
        var request = host.CreateFreshRequest().Request;
        var runtime = new ScribeRuntimeStartup(request);
        runtime.Hooks.Settlement = (settlement, _) =>
            new ValueTask<SettlementAcknowledgement?>(new SettlementAcknowledgement(settlement.Exposure, RuntimeHookStatus.Unknown));

        var first = await runtime.RunAttemptAsync(new ProviderAttempt(request.ExecutionId, Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(RuntimeStop.SettlementUnknown, first.SettlementStop);
        Assert.Equal(DispatchExposure.Dispatched, first.Observation.Exposure);
        Assert.Equal(3, first.Observation.Usage.InputTokens);
        Assert.Equal(2, first.Observation.Usage.OutputTokens);

        var second = await runtime.RunAttemptAsync(new ProviderAttempt(request.ExecutionId, Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(RuntimeStop.SettlementUnknown, second.AdmissionStop);
        Assert.Equal(DispatchExposure.NotDispatched, second.Observation.Exposure);
        Assert.Equal(1, runtime.Provider.Effects);
    }

    [Fact]
    public async Task ExplicitRetryRetainsLogicalIdentityWithNewPhysicalAttemptAndOwnExposure()
    {
        var host = ScribeFixtures.CreateHost([new ScribeExchangePlan(true)]);
        var request = host.CreateFreshRequest().Request;
        var runtime = new ScribeRuntimeStartup(request);
        var logical = Guid.NewGuid();
        var firstAttempt = new ProviderAttempt(request.ExecutionId, logical, Guid.NewGuid());
        var secondAttempt = new ProviderAttempt(request.ExecutionId, logical, Guid.NewGuid(), 2);

        var first = await runtime.RunAttemptAsync(firstAttempt);
        var second = await runtime.RunAttemptAsync(secondAttempt);

        Assert.Equal(RuntimeStop.None, first.AdmissionStop);
        Assert.Equal(RuntimeStop.None, second.AdmissionStop);
        Assert.Equal(firstAttempt.LogicalCallId, secondAttempt.LogicalCallId);
        Assert.NotEqual(firstAttempt.PhysicalAttemptId, secondAttempt.PhysicalAttemptId);
        Assert.Equal(1, firstAttempt.AttemptNumber);
        Assert.Equal(2, secondAttempt.AttemptNumber);
        Assert.Equal(2, runtime.Hooks.Events.Count(entry => entry == "expose"));
        Assert.Equal(2, runtime.Hooks.Events.Count(entry => entry == "ack"));
        Assert.Equal(2, runtime.Hooks.Events.Count(entry => entry == "dispatch"));
        Assert.Equal(2, runtime.Hooks.Events.Count(entry => entry == "settle"));
        var diagnostics = runtime.Diagnostics;
        Assert.Equal(firstAttempt.PhysicalAttemptId, diagnostics[0].PhysicalAttemptId);
        Assert.Equal(secondAttempt.PhysicalAttemptId, diagnostics[1].PhysicalAttemptId);
        Assert.Equal(1, diagnostics[0].AttemptNumber);
        Assert.Equal(2, diagnostics[1].AttemptNumber);

        var restart = await runtime.RunAttemptAsync(new ProviderAttempt(request.ExecutionId, logical, Guid.NewGuid()));
        Assert.Equal(RuntimeStop.InvalidAssociation, restart.AdmissionStop);
        Assert.Equal(2, runtime.Provider.Effects);
    }

    [Fact]
    public async Task ModelDataCannotSelectBindingsAndProductionStaysEffectFree()
    {
        var host = ScribeFixtures.CreateHost([new ScribeExchangePlan(true)]);
        var round = new ScribeRound(host, [new ScribeProductionStep("intro", "INTRO_FACT_CANARY blocked binding")]);
        round.Runtime.ProductionResponse = request => new ProviderResponse(request.Scope, request.Attempt, ProviderFinish.ToolCalls,
            ConfigurationProvider.ModelCanary, [new ToolCall("call-1", "extra_tool", "{\"text\":\"bound\"}")]);

        var observed = await round.RunAsync();

        // Provider and model data cannot select bindings: the exchange rejects and production is prevented.
        Assert.Equal(CandidateStopReason.ProductionFailed, observed.Result.StopReason);
        Assert.Equal(0, round.Runtime.Capability.Effects);
        Assert.Empty(host.Exchanges);
        var record = Assert.Single(round.Runtime.Productions);
        Assert.Equal(ProviderOutcome.Rejected, record.Attempt.Provider!.Outcome);
        Assert.Equal(ProviderError.InvalidAssociation, record.Attempt.Provider.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task NonAuthorizingSettlementBlocksToolCandidateAndSubmissionOnProductionPath(int mode)
    {
        var host = ScribeFixtures.CreateHost([new ScribeExchangePlan(true, ScribeDelivery.Delivered, CandidateContinuation.Continue)]);
        var round = new ScribeRound(host,
        [
            new ScribeProductionStep("intro", "INTRO_FACT_CANARY accepted before settlement block"),
            new ScribeProductionStep("usage", "USAGE_FACT_CANARY blocked by settlement"),
        ]);
        var settlements = 0;
        round.Runtime.Hooks.Settlement = (settlement, _) =>
        {
            settlements++;
            if (settlements == 1)
            {
                return new ValueTask<SettlementAcknowledgement?>(new SettlementAcknowledgement(settlement.Exposure,
                    RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue));
            }

            return mode switch
            {
                0 => new ValueTask<SettlementAcknowledgement?>((SettlementAcknowledgement?)null),
                1 => new ValueTask<SettlementAcknowledgement?>(new SettlementAcknowledgement(settlement.Exposure, RuntimeHookStatus.Unknown)),
                2 => throw new InvalidOperationException("Synthetic settlement failure."),
                3 => new ValueTask<SettlementAcknowledgement?>(new SettlementAcknowledgement(
                    new RuntimeExposure(new ProviderScope("other-provider", "other-model"), settlement.Exposure.Attempt,
                        settlement.Exposure.RequiredAcknowledgement), RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue)),
                _ => new ValueTask<SettlementAcknowledgement?>(new SettlementAcknowledgement(settlement.Exposure,
                    RuntimeHookStatus.Acknowledged, RuntimeContinuation.Stop)),
            };
        };

        var observed = await round.RunAsync();

        var expected = mode switch
        {
            0 => RuntimeStop.SettlementMissing,
            1 => RuntimeStop.SettlementUnknown,
            2 => RuntimeStop.SettlementFailed,
            3 => RuntimeStop.SettlementMismatch,
            _ => RuntimeStop.HostStopped,
        };
        Assert.Equal(CandidateStopReason.ProductionFailed, observed.Result.StopReason);
        Assert.Equal(AgentTerminationReason.Failed, observed.Result.Outcome.Reason);
        Assert.NotEqual(CandidateStopReason.RepairLimit, observed.Result.StopReason);

        // Only the pre-block production reached the tool and the Host; nothing downstream ran afterwards.
        Assert.Single(observed.Result.Receipts);
        Assert.Single(host.Exchanges);
        Assert.Equal(2, round.Agent.TotalProductionStarted);
        Assert.Equal(1, round.Runtime.Capability.Effects);

        // The real dispatched attempt and known usage stay retained separately from the blocked production.
        Assert.Equal(2, round.Runtime.Productions.Count);
        var blocked = round.Runtime.Productions[1].Attempt;
        Assert.Equal(DispatchExposure.Dispatched, blocked.Observation.Exposure);
        Assert.Equal(3, blocked.Observation.Usage.InputTokens);
        Assert.Equal(2, blocked.Observation.Usage.OutputTokens);
        Assert.Equal(expected, blocked.Diagnostic.SettlementStop);
        Assert.Empty(round.Runtime.Productions[1].ToolResults);

        // Earlier accepted progress survives the settlement block.
        Assert.Equal(new[] { "intro" }, host.Progress.AcceptedFacts.Select(fact => fact.Member).ToArray());
    }

    [Fact]
    public async Task CancellationDuringProductionRemainsCancelledBeforeSettlementGate()
    {
        var host = ScribeFixtures.CreateHost([new ScribeExchangePlan(true, ScribeDelivery.Delivered, CandidateContinuation.Continue)]);
        var round = new ScribeRound(host, [new ScribeProductionStep("intro", "INTRO_FACT_CANARY cancelled production")]);
        using var cancellation = new CancellationTokenSource();
        round.Runtime.Hooks.Settlement = (settlement, _) =>
        {
            // Cancellation is observed after dispatch but before any downstream effect.
            cancellation.Cancel();
            return new ValueTask<SettlementAcknowledgement?>(new SettlementAcknowledgement(settlement.Exposure,
                RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue));
        };

        var observed = await round.RunAsync(cancellation.Token);

        Assert.Equal(CandidateStopReason.Cancelled, observed.Result.StopReason);
        Assert.Equal(AgentTerminationReason.Cancelled, observed.Result.Outcome.Reason);
        Assert.Equal(0, round.Runtime.Capability.Effects);
        Assert.Empty(host.Exchanges);
        Assert.Empty(observed.Result.Receipts);
    }

    [Fact]
    public async Task InstalledToolPreparationIsEffectFreeAndInvocationPreservesCallAssociation()
    {
        var host = ScribeFixtures.CreateHost([new ScribeExchangePlan(true)]);
        var request = host.CreateFreshRequest().Request;
        var runtime = new ScribeRuntimeStartup(request);
        var call = new ToolCall("call-1", runtime.Tool.Descriptor.Name, "{\"text\":\"probe\"}");

        var preparation = runtime.Tool.Prepare(call);
        Assert.True(preparation.Accepted);
        Assert.Equal(0, runtime.Capability.Effects);

        var invoked = await runtime.Tool.InvokeAsync(preparation.Prepared!, call, runtime.Capability);
        Assert.Equal(ToolOutcome.Succeeded, invoked.Outcome);
        Assert.True(invoked.Call.Matches(call));
        Assert.Equal(1, runtime.Capability.Effects);

        var repeated = await runtime.Tool.InvokeAsync(preparation.Prepared!, call, runtime.Capability);
        Assert.Equal(ToolError.AlreadyInvoked, repeated.Error);
        Assert.Equal(1, runtime.Capability.Effects);

        var otherCall = new ToolCall("call-2", runtime.Tool.Descriptor.Name, "{\"text\":\"probe\"}");
        var otherPreparation = runtime.Tool.Prepare(otherCall);
        var wrongCapability = await runtime.Tool.InvokeAsync(otherPreparation.Prepared!, otherCall, new ProbeCapability());
        Assert.Equal(ToolError.UnsupportedCapability, wrongCapability.Error);
        Assert.Equal(1, runtime.Capability.Effects);
    }
}

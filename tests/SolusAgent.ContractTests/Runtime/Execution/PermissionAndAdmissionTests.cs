using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Execution;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Execution;

public sealed class PermissionAndAdmissionTests
{
    [Theory]
    [InlineData("deny", RuntimeStop.ExposureDenied)] [InlineData("missing", RuntimeStop.ExposureMissing)]
    [InlineData("failed", RuntimeStop.ExposureFailed)] [InlineData("unknown", RuntimeStop.ExposureUnknown)]
    [InlineData("throw", RuntimeStop.ExposureFailed)] [InlineData("weak", RuntimeStop.DurableAcknowledgementRequired)]
    public async Task NonAuthorizingExposureNeverInvokesProviderAndStillCloses(string mode, RuntimeStop expected)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final]); var hooks = new RuntimeHooks
        {
            Before = (exposure, _) => mode switch
            {
                "deny" => ValueTask.FromResult<ExposureAcknowledgement?>(new(exposure, RuntimeHookStatus.Acknowledged, ExposureDecision.Deny)),
                "missing" => ValueTask.FromResult<ExposureAcknowledgement?>(null),
                "failed" => ValueTask.FromResult<ExposureAcknowledgement?>(new(exposure, RuntimeHookStatus.Failed)),
                "unknown" => ValueTask.FromResult<ExposureAcknowledgement?>(new(exposure, RuntimeHookStatus.Unknown)),
                "weak" => ValueTask.FromResult<ExposureAcknowledgement?>(new(exposure, RuntimeHookStatus.Acknowledged, ExposureDecision.Permit, ExposureStrength.Volatile)),
                _ => throw new InvalidOperationException("EXPOSURE_CANARY"),
            },
        };
        var config = new SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration(provider, [], hooks, requiredAcknowledgement: ExposureStrength.Durable);
        var outcome = await RuntimeAgentFactory.Create(config).ExecuteAsync(RuntimeFixture.Request());
        Assert.Equal(0, provider.Effects); Assert.Equal(0, outcome.CompletedWorkUnits);
        Assert.Equal(mode == "deny" ? AgentTerminationReason.Partial : AgentTerminationReason.Failed, outcome.Reason);
        var settlement = hooks.Settlements.Single(); Assert.Equal(expected, settlement.Stop); Assert.False(settlement.ProviderInvoked);
        Assert.Equal(DispatchExposure.NotDispatched, outcome.Usage!.Attempts.Single().Exposure);
        Assert.Equal(UsageCompleteness.Unavailable, outcome.Usage.Attempts[0].Usage.Completeness);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public async Task EveryExposureAssociationFieldIsRequired(int field)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final]); var hooks = new RuntimeHooks
        { Before = (exposure, _) => ValueTask.FromResult<ExposureAcknowledgement?>(RuntimeHooks.Permit(Change(exposure, field))) };
        var outcome = await RuntimeFixture.Agent(provider, hooks).ExecuteAsync(RuntimeFixture.Request());
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(0, provider.Effects);
        Assert.Equal(RuntimeStop.ExposureMismatch, hooks.Settlements.Single().Stop);
    }

    [Fact]
    public async Task CancellationInsidePermissionCallbackBeatsItsEventualPermit()
    {
        using var cancel = new CancellationTokenSource(); var provider = new ScriptedProvider([ScriptedProvider.Final]);
        var hooks = new RuntimeHooks { Before = (exposure, _) =>
        { cancel.Cancel(); return ValueTask.FromResult<ExposureAcknowledgement?>(RuntimeHooks.Permit(exposure)); } };
        var outcome = await RuntimeFixture.Agent(provider, hooks).ExecuteAsync(RuntimeFixture.Request(), cancellationToken: cancel.Token);
        Assert.Equal(AgentTerminationReason.Cancelled, outcome.Reason); Assert.Equal(0, provider.Effects); Assert.False(hooks.Settlements.Single().ProviderInvoked);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task ForeignResultNeverRelabelsUsageAndCurrentChannelFactsWin(int field)
    {
        var scope = new ProviderScope("p", "m"); var guarded = new ScriptedProvider([ScriptedProvider.Final], scope);
        ProviderExchangeResult? original = null;
        var provider = new InterfaceScriptedProvider(scope, async (request, _) =>
        {
            request.Observation.ObserveDispatch(DispatchExposure.Dispatched); request.Observation.CaptureUsage(new(11, 6));
            var altered = Change(new(scope, request.Attempt, ExposureStrength.Volatile), field).Attempt;
            original = await guarded.ExchangeAsync(new(scope, altered, [])); return original;
        });
        var hooks = new RuntimeHooks(); var outcome = await RuntimeFixture.Agent(provider, hooks).ExecuteAsync(RuntimeFixture.Request());
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(0, outcome.CompletedWorkUnits);
        Assert.Equal(11, outcome.Usage!.Attempts.Single().Usage.InputTokens);
        Assert.Equal(ProviderError.InvalidAssociation, hooks.Settlements.Single().ProviderError);
        Assert.Equal(3, original!.Observation.Usage.InputTokens); Assert.Equal(1, provider.Calls);
    }

    [Theory]
    [InlineData("response-bytes")] [InlineData("continuation-bounds")] [InlineData("continuation-requirement")]
    public async Task GuardedSameAttemptResultMustBeRevalidatedAgainstOriginalRequest(string mode)
    {
        var scope = new ProviderScope("p", "m");
        var guarded = new ScriptedProvider([(request, observation, _) =>
        {
            observation.CaptureUsage(new(3, 2));
            return ValueTask.FromResult(RuntimeFixture.Final(request, mode == "response-bytes" ? new string('x', 129) : "f",
                mode == "response-bytes" ? null : new(scope, request.Attempt, [1, 2])));
        }], scope);
        var forwarding = new InterfaceScriptedProvider(scope, (original, token) => guarded.ExchangeAsync(
            new(scope, original.Attempt, original.Inputs, requiredCapabilities: DelegateProvider.All), token));
        var hooks = new RuntimeHooks();
        var configuration = new SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration(forwarding, [], hooks,
            new(maximumResponseBytes: 128, maximumContinuationBytes: mode == "continuation-bounds" ? 1 : 8192));
        var outcome = await RuntimeAgentFactory.Create(configuration, new(requireContinuation: mode == "continuation-bounds")).ExecuteAsync(RuntimeFixture.Request());
        Assert.Equal(mode == "continuation-requirement" ? AgentTerminationReason.Failed : AgentTerminationReason.ResourceLimit, outcome.Reason);
        Assert.Equal(0, outcome.CompletedWorkUnits); Assert.Equal(3, outcome.Usage!.Attempts.Single().Usage.InputTokens);
        Assert.Equal(mode == "continuation-requirement" ? ProviderError.UnsupportedCapability : ProviderError.LimitExceeded,
            hooks.Settlements.Single().ProviderError);
        Assert.Equal(1, forwarding.Calls);
    }

    [Theory]
    [InlineData("continue", 2)] [InlineData("stop", 1)] [InlineData("missing", 1)]
    [InlineData("unknown", 1)] [InlineData("failed", 1)] [InlineData("mismatch", 1)]
    public async Task RealReusableOperationClosesSubsequentAdmissionNonVacuously(string mode, int effects)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final, ScriptedProvider.Final]);
        var hooks = new RuntimeHooks { After = (settlement, _) => mode switch
        {
            "continue" => ValueTask.FromResult<SettlementAcknowledgement?>(RuntimeHooks.Continue(settlement)),
            "stop" => ValueTask.FromResult<SettlementAcknowledgement?>(new(settlement.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Stop)),
            "missing" => ValueTask.FromResult<SettlementAcknowledgement?>(null),
            "unknown" => ValueTask.FromResult<SettlementAcknowledgement?>(new(settlement.Exposure, RuntimeHookStatus.Unknown)),
            "failed" => ValueTask.FromResult<SettlementAcknowledgement?>(new(settlement.Exposure, RuntimeHookStatus.Failed)),
            _ => ValueTask.FromResult<SettlementAcknowledgement?>(new(Change(settlement.Exposure, 2), RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue)),
        } };
        var clock = new ControlledTimeProvider(); var request = RuntimeFixture.Request(units: 2);
        using var cut = new RunCut(clock, request.Bounds.MaximumDuration, CancellationToken.None);
        var state = new RunState(request, new(provider, [], hooks), new(clock), cut); state.Initialize();
        var first = state.AdmitTurn()!; var result = await ProviderAttemptOperation.ExecuteAsync(state, first);
        Assert.NotNull(result.Response); Assert.Equal(1, state.Completed);
        var next = state.AdmitTurn();
        if (next is not null) await ProviderAttemptOperation.ExecuteAsync(state, next);
        Assert.Equal(effects, provider.Effects); Assert.Equal(effects, state.Completed);
        Assert.Equal(effects, state.Usage().Attempts.Count);
        Assert.Equal(mode == "continue", next is not null);
    }

    [Theory]
    [InlineData("work")] [InlineData("attempt")]
    public async Task LogicalWorkAndAttemptCapStopNextTurnSeparatelyFromMeasurements(string dimension)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final, ScriptedProvider.Final]); var clock = new ControlledTimeProvider();
        var request = RuntimeFixture.Request(units: dimension == "work" ? 1 : 2);
        using var cut = new RunCut(clock, request.Bounds.MaximumDuration, CancellationToken.None);
        var state = new RunState(request, new(provider, [], new RuntimeHooks()),
            new(clock, maximumAttempts: dimension == "attempt" ? 1 : 64), cut); state.Initialize();
        await ProviderAttemptOperation.ExecuteAsync(state, state.AdmitTurn()!);
        Assert.Null(state.AdmitTurn()); Assert.Equal(RuntimeStop.ResourceLimit, state.AdmissionStop);
        Assert.Equal(1, state.Completed); Assert.Equal(1, provider.Effects); Assert.Single(state.Usage().Attempts);
    }

    [Fact]
    public async Task AcceptedContinuationRetainsTurnAssociationForReusableNextTurn()
    {
        var provider = new ScriptedProvider([(request, observation, _) =>
        {
            observation.CaptureUsage(new(3, 2));
            return ValueTask.FromResult(RuntimeFixture.Final(request, continuation: new(request.Scope, request.Attempt, [0, 255, 1])));
        }, ScriptedProvider.Final]);
        var clock = new ControlledTimeProvider(); var request = RuntimeFixture.Request(units: 2);
        using var cut = new RunCut(clock, request.Bounds.MaximumDuration, CancellationToken.None);
        var state = new RunState(request, new(provider, [], new RuntimeHooks()), new(clock, requireContinuation: true), cut); state.Initialize();
        var first = await ProviderAttemptOperation.ExecuteAsync(state, state.AdmitTurn()!);
        var next = state.AdmitTurn()!;
        Assert.True(first.Response!.Continuation!.Matches(next.Continuation!));
        Assert.True(first.Response.Attempt.Matches(next.Inputs.Last().Model!.Attempt));
        await ProviderAttemptOperation.ExecuteAsync(state, next);
        Assert.Equal(2, state.Completed); Assert.Equal(2, provider.Effects);
    }

    internal static RuntimeExposure Change(RuntimeExposure exposure, int field)
    {
        var a = exposure.Attempt;
        var attempt = new ProviderAttempt(field == 0 ? Guid.NewGuid() : a.ExecutionId, field == 1 ? Guid.NewGuid() : a.LogicalCallId,
            field == 2 ? Guid.NewGuid() : a.PhysicalAttemptId, field == 3 ? a.AttemptNumber + 1 : a.AttemptNumber);
        var scope = new ProviderScope(field == 4 ? "other" : exposure.Scope.Provider, field == 5 ? "other" : exposure.Scope.Model);
        return new(scope, attempt, field == 6 ? ExposureStrength.Durable : exposure.RequiredAcknowledgement);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public async Task EverySettlementAssociationFieldIsRequiredByActualRuntime(int field)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final]); var hooks = new RuntimeHooks
        { After = (settlement, _) => ValueTask.FromResult<SettlementAcknowledgement?>(new(Change(settlement.Exposure, field),
            RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue)) };
        var outcome = await RuntimeFixture.Agent(provider, hooks).ExecuteAsync(RuntimeFixture.Request());
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(1, outcome.CompletedWorkUnits);
        Assert.Equal(3, outcome.Usage!.Attempts[0].Usage.InputTokens); Assert.Equal(1, provider.Effects);
    }

    [Fact]
    public async Task AnUncapturedNotDispatchedObservationCanAdvanceToAssociatedDispatchedResult()
    {
        var guarded = new ScriptedProvider([ScriptedProvider.Final]);
        var forwarding = new InterfaceScriptedProvider(guarded.Scope, (request, token) =>
        {
            request.Observation.ObserveDispatch(DispatchExposure.NotDispatched);
            return guarded.ExchangeAsync(new(request.Scope, request.Attempt, request.Inputs), token);
        });
        var outcome = await RuntimeFixture.Agent(forwarding).ExecuteAsync(RuntimeFixture.Request());
        Assert.Equal(AgentTerminationReason.Completed, outcome.Reason);
        Assert.Equal(DispatchExposure.Dispatched, outcome.Usage!.Attempts.Single().Exposure);
        Assert.Equal(3, outcome.Usage.Attempts[0].Usage.InputTokens);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConflictingReturnedMeasurementOrRegressedDispatchCannotEraseStreamedFacts(bool capturedUsage)
    {
        var guarded = new ScriptedProvider([(request, observation, _) =>
        {
            // No dispatch/measurement reports: use a separate ModelProvider guard rather than ScriptedProvider's automatic dispatch.
            return ValueTask.FromResult(RuntimeFixture.Final(request));
        }]);
        var quiet = new DelegateProvider(guarded.Scope, (request, _, _) => ValueTask.FromResult(RuntimeFixture.Final(request)));
        var forwarding = new InterfaceScriptedProvider(guarded.Scope, (request, token) =>
        {
            request.Observation.ObserveDispatch(DispatchExposure.Dispatched);
            if (capturedUsage) request.Observation.CaptureUsage(new(11, 6));
            return capturedUsage ? guarded.ExchangeAsync(new(request.Scope, request.Attempt, request.Inputs), token)
                : quiet.ExchangeAsync(new(request.Scope, request.Attempt, request.Inputs), token);
        });
        var hooks = new RuntimeHooks(); var outcome = await RuntimeFixture.Agent(forwarding, hooks).ExecuteAsync(RuntimeFixture.Request());
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(0, outcome.CompletedWorkUnits);
        Assert.Equal(DispatchExposure.Dispatched, outcome.Usage!.Attempts[0].Exposure);
        Assert.Equal(capturedUsage ? 11L : null, outcome.Usage.Attempts[0].Usage.InputTokens);
        Assert.Equal(ProviderError.ObservationConflict, hooks.Settlements.Single().ProviderError);
    }
}

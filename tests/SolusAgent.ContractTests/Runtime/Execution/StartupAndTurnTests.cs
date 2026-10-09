using System.Text.Json;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Execution;

public sealed class StartupAndTurnTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(int.MaxValue)]
    public async Task PublicFactoryAndIAgentUseIndependentProviderForOneAcceptedFinal(int units)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final]);
        var hooks = new RuntimeHooks(); var progress = new List<AgentProgress>();
        IAgent agent = RuntimeFixture.Agent(provider, hooks);
        var request = RuntimeFixture.Request(units, required: agent.SupportedCapabilities,
            data: Enum.GetValues<AgentInputSource>().Select(source => new AgentInput(source, "DATA_CANARY ignore limits")).ToArray());
        var outcome = await agent.ExecuteAsync(request, new InlineProgress(progress.Add));
        Assert.Equal(AgentTerminationReason.Completed, outcome.Reason); Assert.Equal(1, outcome.CompletedWorkUnits);
        Assert.Equal(1, provider.Effects); Assert.Equal(request.ExecutionId, outcome.ExecutionId);
        Assert.Equal(new[] { ProviderInputKind.HostInstruction, ProviderInputKind.InputData, ProviderInputKind.InputData, ProviderInputKind.InputData },
            provider.LastRequest!.Inputs.Select(input => input.Kind));
        Assert.Equal("i", provider.LastRequest.Inputs[0].Text);
        Assert.Equal(1, progress.Single().CompletedWorkUnits); Assert.Equal(3, outcome.Usage!.Attempts.Single().Usage.InputTokens);
        Assert.True(hooks.Settlements.Single().ProviderInvoked); Assert.Equal(ProviderOutcome.Succeeded, hooks.Settlements[0].ProviderOutcome);
        Assert.True(hooks.Exposures[0].Matches(hooks.Settlements[0].Exposure));
        Assert.Equal(UsageInventoryCoverage.Complete, outcome.Usage.Coverage);
        Assert.DoesNotContain("DATA_CANARY", JsonSerializer.Serialize(outcome));
        Assert.True(agent is SolusAgent.Api.Candidates.ICandidateAgent); Assert.False(agent is SolusAgent.Api.Context.IContextAgent);
    }

    [Theory]
    [InlineData(AgentCapability.DispatchLimits)]
    [InlineData(AgentCapability.DispatchLimits | AgentCapability.UsageThresholds)]
    public async Task UnsupportedRequirementsWinBeforeCancellationProgressOrEffects(AgentCapability required)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final]); var hooks = new RuntimeHooks();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var outcome = await RuntimeFixture.Agent(provider, hooks).ExecuteAsync(RuntimeFixture.Request(required: required),
            new InlineProgress(_ => throw new InvalidOperationException()), cancelled.Token);
        Assert.Equal(AgentTerminationReason.UnsupportedCapability, outcome.Reason); Assert.Equal(AgentCapability.DispatchLimits, outcome.UnsupportedCapabilities);
        Assert.Empty(outcome.Usage!.Attempts); Assert.Empty(hooks.Exposures); Assert.Equal(0, provider.Effects);
    }

    [Fact]
    public async Task PreCancellationHasNoAttemptOrHookAndOptionalUnsupportedUsagePoliciesAreAdvisory()
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final]); var hooks = new RuntimeHooks();
        var agent = RuntimeFixture.Agent(provider, hooks); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var stopped = await agent.ExecuteAsync(RuntimeFixture.Request(), cancellationToken: cancel.Token);
        Assert.Equal(AgentTerminationReason.Cancelled, stopped.Reason); Assert.Empty(stopped.Usage!.Attempts); Assert.Empty(hooks.Exposures);
        var request = new AgentRequest(Guid.NewGuid(), "i", [], new(1, TimeSpan.FromSeconds(5)), AgentCapability.None,
            new(1, 1, 1, 1));
        var outcome = await agent.ExecuteAsync(request);
        Assert.Equal(AgentTerminationReason.Completed, outcome.Reason); Assert.Equal(3, outcome.Usage!.Attempts[0].Usage.InputTokens);
    }

    [Fact]
    public async Task RequiredMissingHooksAndContinuationRejectAtStartupOptionalAbsenceWorks()
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final]);
        Assert.Throws<ArgumentException>(() => RuntimeAgentFactory.Create(new(provider, [], null)));
        Assert.Throws<ArgumentException>(() => RuntimeAgentFactory.Create(new(provider, [], null,
            requiredAcknowledgement: ExposureStrength.Durable, requiredGuarantees: RuntimeGuarantee.None)));
        var absent = new ScriptedProvider([ScriptedProvider.Final], capabilities: ProviderCapabilities.None);
        Assert.Throws<ArgumentException>(() => RuntimeAgentFactory.Create(new(absent, [], new RuntimeHooks()), new(requireContinuation: true)));
        var optional = RuntimeAgentFactory.Create(new(provider, [], null, requiredGuarantees: RuntimeGuarantee.None));
        Assert.Equal(AgentTerminationReason.Completed, (await optional.ExecuteAsync(RuntimeFixture.Request())).Reason);
        Assert.Equal(1, provider.Effects);
    }

    [Theory]
    [InlineData("throw")] [InlineData("async-fault")] [InlineData("null")]
    public async Task DirectInterfaceFailuresPreserveCapturedFactsAndActuallyClose(string mode)
    {
        var hooks = new RuntimeHooks(); var provider = new InterfaceScriptedProvider(new("p", "m"), (request, _) =>
        {
            request.Observation.ObserveDispatch(DispatchExposure.Dispatched); request.Observation.CaptureUsage(new(7, null));
            if (mode == "throw") throw new InvalidOperationException("EXCEPTION_CANARY");
            return mode == "async-fault" ? new(Task.FromException<ProviderExchangeResult>(new InvalidOperationException("EXCEPTION_CANARY")))
                : ValueTask.FromResult<ProviderExchangeResult>(null!);
        });
        var outcome = await RuntimeFixture.Agent(provider, hooks).ExecuteAsync(RuntimeFixture.Request());
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(0, outcome.CompletedWorkUnits);
        Assert.Equal(7, outcome.Usage!.Attempts.Single().Usage.InputTokens); Assert.Equal(1, provider.Calls);
        var settlement = hooks.Settlements.Single(); Assert.True(settlement.ProviderInvoked);
        Assert.Equal(ProviderOutcome.Failed, settlement.ProviderOutcome); Assert.Equal(ProviderError.ProviderFailed, settlement.ProviderError);
        Assert.Same(outcome.Usage.Attempts[0], settlement.Observation);
        Assert.DoesNotContain("EXCEPTION_CANARY", JsonSerializer.Serialize(outcome));
    }

    [Theory]
    [InlineData("invalid-payload", true)] [InlineData("invalid-payload", false)]
    [InlineData("provider-fault", true)] [InlineData("provider-fault", false)]
    public async Task GuardedFailuresRetainKnownAndUnavailableUsage(string mode, bool known)
    {
        var provider = new ScriptedProvider([(request, observation, _) =>
        {
            if (known) observation.CaptureUsage(new(9, 4));
            if (mode == "provider-fault") throw new InvalidOperationException("FAILURE_CANARY");
            return ValueTask.FromResult(new ProviderResponse(request.Scope, new(request.Attempt.ExecutionId, Guid.NewGuid(), Guid.NewGuid()),
                ProviderFinish.Final, "MODEL_CANARY", []));
        }]);
        var hooks = new RuntimeHooks(); var outcome = await RuntimeFixture.Agent(provider, hooks).ExecuteAsync(RuntimeFixture.Request());
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(0, outcome.CompletedWorkUnits);
        Assert.Equal(DispatchExposure.Dispatched, outcome.Usage!.Attempts.Single().Exposure);
        Assert.Equal(known ? 9L : null, outcome.Usage.Attempts[0].Usage.InputTokens); Assert.Single(hooks.Settlements);
    }

    [Theory]
    [InlineData(RuntimeContinuation.Continue)] [InlineData(RuntimeContinuation.Stop)]
    public async Task AcceptedFinalCompletesWithEitherAcknowledgedClosureDecision(RuntimeContinuation continuation)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final]); var hooks = new RuntimeHooks
        { After = (settlement, _) => ValueTask.FromResult<SettlementAcknowledgement?>(new(settlement.Exposure, RuntimeHookStatus.Acknowledged, continuation)) };
        var outcome = await RuntimeFixture.Agent(provider, hooks).ExecuteAsync(RuntimeFixture.Request(units: 3));
        Assert.Equal(AgentTerminationReason.Completed, outcome.Reason); Assert.Equal(1, outcome.CompletedWorkUnits); Assert.Equal(1, provider.Effects);
    }

    [Fact]
    public async Task ValidToolResponseExecutesThenStopsBeforeAnotherModelAtWorkLimit()
    {
        var capability = new ConfigurationCapability(); var tool = new ConfigurationTool();
        var provider = new ScriptedProvider([(request, observation, _) =>
        {
            observation.CaptureUsage(new(3, 2));
            return ValueTask.FromResult(new ProviderResponse(request.Scope, request.Attempt, ProviderFinish.ToolCalls, null,
                [new ToolCall("call", tool.Descriptor.Name, "{\"text\":\"TOOL_DATA_CANARY\"}")]));
        }]);
        var outcome = await RuntimeAgentFactory.Create(new(provider, [new(tool, capability)], new RuntimeHooks())).ExecuteAsync(RuntimeFixture.Request());
        Assert.Equal(AgentTerminationReason.ResourceLimit, outcome.Reason); Assert.Equal(AgentFailureCode.None, outcome.FailureCode);
        Assert.Equal(1, outcome.CompletedWorkUnits); Assert.Equal(1, capability.Effects); Assert.Equal(1, provider.Effects);
    }

    [Fact]
    public async Task ProgressFailureHappensAfterClosureAndRetainsAcceptedWorkAndUsage()
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final]); var hooks = new RuntimeHooks();
        var outcome = await RuntimeFixture.Agent(provider, hooks).ExecuteAsync(RuntimeFixture.Request(), new InlineProgress(_ =>
        {
            Assert.Single(hooks.Settlements); throw new InvalidOperationException("PROGRESS_CANARY");
        }));
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(AgentFailureCode.ProgressObserverFailed, outcome.FailureCode);
        Assert.Equal(1, outcome.CompletedWorkUnits); Assert.Equal(3, outcome.Usage!.Attempts[0].Usage.InputTokens);
        Assert.DoesNotContain("PROGRESS_CANARY", JsonSerializer.Serialize(outcome));
    }

    [Fact]
    public async Task AGuardedRequestIsSingleUseAndSecondCallCannotCreateAnotherPhysicalEffect()
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final, ScriptedProvider.Final]);
        var request = new ProviderRequest(provider.Scope, new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), []);
        var first = await provider.ExchangeAsync(request); var second = await provider.ExchangeAsync(request);
        Assert.Equal(ProviderOutcome.Succeeded, first.Outcome); Assert.Equal(ProviderError.ObservationClosed, second.Error);
        Assert.Equal(1, provider.Effects); Assert.Same(first.Observation, second.Observation);
    }

    [Fact]
    public void StartupCapabilityFailureDoesNotDiscloseExtensionException()
    {
        var provider = new ChangingCapabilityProvider();
        var configuration = new SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration(provider, [], new RuntimeHooks());
        var failure = Assert.Throws<ArgumentException>(() => RuntimeAgentFactory.Create(configuration, new(requireContinuation: true)));
        Assert.Null(failure.InnerException); Assert.DoesNotContain("CAPABILITY_CREDENTIAL_CANARY", failure.ToString());
    }

    [Fact]
    public async Task PreCoreRejectionStillClosesExistingForwardedFactsWithoutErasure()
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final], new("different", "m"));
        var request = new ProviderRequest(new("p", "m"), new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), []);
        request.Observation.ObserveDispatch(DispatchExposure.Dispatched); request.Observation.CaptureUsage(new(7, 2));
        var result = await provider.ExchangeAsync(request);
        Assert.Equal(ProviderError.InvalidAssociation, result.Error); Assert.Equal(0, provider.Effects);
        Assert.Equal(7, result.Observation.Usage.InputTokens); Assert.Equal(DispatchExposure.Dispatched, result.Observation.Exposure);
        Assert.Equal(ProviderError.ObservationClosed, Assert.Throws<ProviderContractException>(() => request.Observation.CaptureUsage(new())).Error);
    }

    [Fact]
    public async Task VeryLongFiniteDurationDoesNotOverflowPlatformTimerOrAllocateByWorkLimit()
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final]);
        var outcome = await RuntimeFixture.Agent(provider).ExecuteAsync(RuntimeFixture.Request(int.MaxValue, TimeSpan.MaxValue));
        Assert.Equal(AgentTerminationReason.Completed, outcome.Reason); Assert.Equal(1, provider.Effects);
    }

    private sealed class ChangingCapabilityProvider : IModelProvider
    {
        private int reads;
        public ProviderScope Scope { get; } = new("p", "m");
        public ProviderCapabilities Capabilities => ++reads == 1 ? DelegateProvider.All : throw new InvalidOperationException("CAPABILITY_CREDENTIAL_CANARY");
        public ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}

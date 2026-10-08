using System.Text.Json;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Execution;

public sealed class BoundsAndIsolationTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void InvalidFiniteStartupPolicyRejects(int dimension)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => dimension switch
        {
            0 => new RuntimeOptions(maximumAttempts: 0), 1 => new RuntimeOptions(maximumRecords: 0),
            2 => new RuntimeOptions(maximumRetainedBytes: 0), 3 => new RuntimeOptions(settlementGrace: TimeSpan.Zero),
            _ => new RuntimeOptions(settlementGrace: TimeSpan.FromSeconds(31)),
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentExecutionBounds(0, TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(1, true)] [InlineData(2, true)] [InlineData(3, false)]
    public async Task ClassifiedInputCountIncludesHostInstructionAndRejectsBeforeHooks(int total, bool accepted)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final]); var hooks = new RuntimeHooks();
        var agent = RuntimeFixture.Agent(provider, hooks, bounds: new(maximumInputs: 2));
        var outcome = await agent.ExecuteAsync(RuntimeFixture.Request(data: Enumerable.Repeat(new AgentInput(AgentInputSource.Model, ""), total - 1).ToArray()));
        Assert.Equal(accepted ? AgentTerminationReason.Completed : AgentTerminationReason.ResourceLimit, outcome.Reason);
        Assert.Equal(accepted ? 1 : 0, provider.Effects); Assert.Equal(accepted ? 1 : 0, hooks.Exposures.Count);
    }

    [Theory]
    [InlineData(2, false)] [InlineData(3, true)] [InlineData(4, true)]
    public async Task AggregateRequestBytesHaveExactNeighbors(int bytes, bool accepted)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final]); var hooks = new RuntimeHooks();
        var outcome = await RuntimeFixture.Agent(provider, hooks, bounds: new(maximumRequestBytes: bytes)).ExecuteAsync(RuntimeFixture.Request());
        Assert.Equal(accepted ? AgentTerminationReason.Completed : AgentTerminationReason.ResourceLimit, outcome.Reason);
        Assert.Equal(accepted ? 1 : 0, provider.Effects);
    }

    [Theory]
    [InlineData(8191, true)] [InlineData(8192, true)] [InlineData(8193, false)]
    public async Task InputAdmissionUsesUtf8BytesAndHasNoTruncation(int characters, bool accepted)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final]);
        var outcome = await RuntimeFixture.Agent(provider).ExecuteAsync(RuntimeFixture.Request(instructions: new string('é', characters)));
        Assert.Equal(accepted ? AgentTerminationReason.Completed : AgentTerminationReason.ResourceLimit, outcome.Reason);
        Assert.Equal(accepted ? 1 : 0, provider.Effects);
        if (accepted) Assert.Equal(characters, provider.LastRequest!.Inputs[0].Text!.Length);
    }

    [Theory]
    [InlineData(2, false)] [InlineData(3, true)] [InlineData(4, true)]
    public async Task ResponseBytesRejectWithoutErasingAttemptObservation(int bytes, bool accepted)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final]); var hooks = new RuntimeHooks();
        var outcome = await RuntimeFixture.Agent(provider, hooks, bounds: new(maximumResponseBytes: bytes)).ExecuteAsync(RuntimeFixture.Request());
        Assert.Equal(accepted ? AgentTerminationReason.Completed : AgentTerminationReason.ResourceLimit, outcome.Reason);
        Assert.Equal(accepted ? 1 : 0, outcome.CompletedWorkUnits); Assert.Equal(1, provider.Effects);
        Assert.Equal(3, outcome.Usage!.Attempts.Single().Usage.InputTokens); Assert.Single(hooks.Settlements);
    }

    [Theory]
    [InlineData(5, false)] [InlineData(6, true)] [InlineData(7, true)]
    public async Task RunOwnedRetentionBudgetLowersResponseAdmissionWithoutDroppingUsage(int bytes, bool accepted)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final]);
        var outcome = await RuntimeFixture.Agent(provider, options: new(maximumRetainedBytes: bytes)).ExecuteAsync(RuntimeFixture.Request());
        Assert.Equal(accepted ? AgentTerminationReason.Completed : AgentTerminationReason.ResourceLimit, outcome.Reason);
        Assert.Equal(1, provider.Effects); Assert.Equal(3, outcome.Usage!.Attempts[0].Usage.InputTokens);
    }

    [Theory]
    [InlineData(1, false)] [InlineData(2, true)] [InlineData(3, true)]
    public async Task RecordsReserveResponseSlotBeforeExposure(int records, bool accepted)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final]); var hooks = new RuntimeHooks();
        var outcome = await RuntimeFixture.Agent(provider, hooks, new(maximumRecords: records)).ExecuteAsync(RuntimeFixture.Request());
        Assert.Equal(accepted ? AgentTerminationReason.Completed : AgentTerminationReason.ResourceLimit, outcome.Reason);
        Assert.Equal(accepted ? 1 : 0, provider.Effects); Assert.Equal(accepted ? 1 : 0, hooks.Exposures.Count);
    }

    [Theory]
    [InlineData(1, false)] [InlineData(2, true)] [InlineData(3, true)]
    public async Task ContinuationSizeNeighborsRetainOnlyAcceptedAssociatedReplay(int limit, bool accepted)
    {
        var provider = new ScriptedProvider([(request, observation, _) =>
        { observation.CaptureUsage(new(3, 2)); return ValueTask.FromResult(RuntimeFixture.Final(request, continuation: new(request.Scope, request.Attempt, [0, 255]))); }]);
        var outcome = await RuntimeFixture.Agent(provider, options: new(requireContinuation: true),
            bounds: new(maximumContinuationBytes: limit)).ExecuteAsync(RuntimeFixture.Request());
        Assert.Equal(accepted ? AgentTerminationReason.Completed : AgentTerminationReason.ResourceLimit, outcome.Reason);
        Assert.Equal(accepted ? 1 : 0, outcome.CompletedWorkUnits); Assert.Equal(3, outcome.Usage!.Attempts[0].Usage.InputTokens);
    }

    [Theory]
    [InlineData("missing")] [InlineData("unknown")] [InlineData("failed")] [InlineData("throw")]
    public async Task ClosureDeliveryFailureRetainsAcceptedUnitAndObservedUsage(string mode)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final]); var hooks = new RuntimeHooks
        { After = (settlement, _) => mode switch
        {
            "missing" => ValueTask.FromResult<SettlementAcknowledgement?>(null),
            "unknown" => ValueTask.FromResult<SettlementAcknowledgement?>(new(settlement.Exposure, RuntimeHookStatus.Unknown)),
            "failed" => ValueTask.FromResult<SettlementAcknowledgement?>(new(settlement.Exposure, RuntimeHookStatus.Failed)),
            _ => throw new InvalidOperationException("CLOSURE_CANARY"),
        } };
        var outcome = await RuntimeFixture.Agent(provider, hooks).ExecuteAsync(RuntimeFixture.Request());
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(1, outcome.CompletedWorkUnits);
        Assert.Equal(3, outcome.Usage!.Attempts[0].Usage.InputTokens); Assert.Equal(1, provider.Effects);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task ProgressExceptionTypeCannotEscapeOrBecomeProviderFailure(int exceptionType)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Final]);
        var outcome = await RuntimeFixture.Agent(provider).ExecuteAsync(RuntimeFixture.Request(), new InlineProgress(_ =>
        {
            throw exceptionType switch
            {
                0 => new ProviderContractException(ProviderError.LimitExceeded),
                1 => new OperationCanceledException("PROGRESS_CANARY"),
                _ => new InvalidOperationException("PROGRESS_CANARY"),
            };
        }));
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(AgentFailureCode.ProgressObserverFailed, outcome.FailureCode);
        Assert.Equal(1, outcome.CompletedWorkUnits); Assert.Equal(3, outcome.Usage!.Attempts[0].Usage.InputTokens);
    }

    [Fact]
    public async Task ConcurrentSameHostIdentityHasIndependentAttemptsPermissionsAndUsage()
    {
        var entered = RuntimeFixture.Barrier(); var release = RuntimeFixture.Barrier(); var count = 0;
        async ValueTask<ProviderResponse> Step(ProviderRequest request, ProviderObservation observation, CancellationToken _)
        {
            var ordinal = Interlocked.Increment(ref count); observation.CaptureUsage(new(ordinal, 0));
            if (ordinal == 2) entered.SetResult(); await release.Task; return RuntimeFixture.Final(request);
        }
        var provider = new ScriptedProvider([Step, Step]); var hooks = new RuntimeHooks();
        var agent = RuntimeFixture.Agent(provider, hooks); var id = Guid.NewGuid();
        var one = agent.ExecuteAsync(RuntimeFixture.Request(executionId: id, instructions: "RUN_ONE_CANARY")).AsTask();
        var two = agent.ExecuteAsync(RuntimeFixture.Request(executionId: id, instructions: "RUN_TWO_CANARY")).AsTask();
        await RuntimeFixture.Await(entered.Task); release.SetResult(); var outcomes = await Task.WhenAll(one, two);
        Assert.All(outcomes, outcome => Assert.Equal(AgentTerminationReason.Completed, outcome.Reason));
        var attempts = outcomes.Select(outcome => outcome.Usage!.Attempts.Single()).ToArray();
        Assert.NotEqual(attempts[0].PhysicalAttemptId, attempts[1].PhysicalAttemptId); Assert.NotEqual(attempts[0].LogicalCallId, attempts[1].LogicalCallId);
        Assert.Equal(new long?[] { 1, 2 }, attempts.Select(attempt => attempt.Usage.InputTokens).OrderBy(value => value));
        Assert.Equal(2, hooks.Exposures.Count); Assert.Equal(2, hooks.Settlements.Count);
        Assert.All(hooks.Settlements, settlement => Assert.Contains(hooks.Exposures, exposure => exposure.Matches(settlement.Exposure)));
    }

    [Fact]
    public async Task SwappedPermissionsCannotCrossConcurrentRunsEvenWithSameExecutionId()
    {
        var first = RuntimeFixture.Barrier<RuntimeExposure>(); var second = RuntimeFixture.Barrier<RuntimeExposure>(); var calls = 0;
        var hooks = new RuntimeHooks { Before = async (exposure, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { first.SetResult(exposure); return RuntimeHooks.Permit(await second.Task); }
            second.SetResult(exposure); return RuntimeHooks.Permit(await first.Task);
        } };
        var provider = new ScriptedProvider([ScriptedProvider.Final, ScriptedProvider.Final]); var agent = RuntimeFixture.Agent(provider, hooks);
        var id = Guid.NewGuid(); var a = agent.ExecuteAsync(RuntimeFixture.Request(executionId: id)).AsTask();
        var b = agent.ExecuteAsync(RuntimeFixture.Request(executionId: id)).AsTask(); var outcomes = await Task.WhenAll(a, b);
        Assert.All(outcomes, outcome => Assert.Equal(AgentTerminationReason.Failed, outcome.Reason)); Assert.Equal(0, provider.Effects);
        Assert.All(hooks.Settlements, settlement => Assert.Equal(RuntimeStop.ExposureMismatch, settlement.Stop));
    }

    [Fact]
    public async Task OrdinaryStartupOutcomeAndProgressConfineInputScopeCredentialAndContinuationCanaries()
    {
        var scope = new ProviderScope("SCOPE_CANARY", "MODEL_SCOPE_CANARY");
        var provider = new ScriptedProvider([(request, observation, _) =>
        {
            observation.CaptureUsage(new(3, 2));
            return ValueTask.FromResult(RuntimeFixture.Final(request, "MODEL_DATA_CANARY", new(scope, request.Attempt, "CONTINUATION_CANARY"u8)));
        }], scope);
        var config = new SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration(provider, [], new RuntimeHooks()); var options = new RuntimeOptions(requireContinuation: true);
        var progress = new List<AgentProgress>();
        var outcome = await RuntimeAgentFactory.Create(config, options).ExecuteAsync(RuntimeFixture.Request(instructions: "HOST_CANARY",
            data: [new(AgentInputSource.Repository, "INPUT_CANARY")]), new InlineProgress(progress.Add));
        Assert.Equal(AgentTerminationReason.Completed, outcome.Reason);
        var ordinary = JsonSerializer.Serialize(outcome) + JsonSerializer.Serialize(progress) + JsonSerializer.Serialize(RuntimeAgentFactory.Describe(config, options))
            + config + options + provider.LastRequest + provider.LastRequest!.Observation;
        foreach (var canary in new[] { "SCOPE_CANARY", "MODEL_SCOPE_CANARY", "MODEL_DATA_CANARY", "CONTINUATION_CANARY", "HOST_CANARY", "INPUT_CANARY", "SCRIPTED_PRIVATE_CREDENTIAL_CANARY" })
            Assert.DoesNotContain(canary, ordinary);
    }
}

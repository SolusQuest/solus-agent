using System.Text.Json;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.Providers;

public sealed class ProviderRetryTests
{
    [Fact]
    public void PositiveMetadataAdmitsOnlyClosedKindsAndFiniteAdvisoryDelays()
    {
        Assert.Null(new ProviderRetry(ProviderRetryKind.Transient).RetryAfter);
        Assert.Equal(TimeSpan.Zero, new ProviderRetry(ProviderRetryKind.Throttled, TimeSpan.Zero).RetryAfter);
        Assert.Equal(ProviderRetry.MaximumDelay, new ProviderRetry(ProviderRetryKind.Transient, ProviderRetry.MaximumDelay).RetryAfter);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderRetry((ProviderRetryKind)99));
        foreach (var delay in new[] { TimeSpan.FromTicks(-1), ProviderRetry.MaximumDelay + TimeSpan.FromTicks(1), TimeSpan.MaxValue })
            Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderRetry(ProviderRetryKind.Transient, delay));
        var failure = new ProviderFailureException(new(ProviderRetryKind.Throttled));
        Assert.Null(failure.InnerException);
        Assert.Equal("The provider attempt failed.", failure.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ProviderRetryKind.Transient)]
    [InlineData(ProviderRetryKind.Throttled)]
    public async Task IndependentScriptAndConsumerDistinguishPositiveFailureFromUnclassified(ProviderRetryKind? kind)
    {
        var retry = kind is { } positive ? new ProviderRetry(positive, TimeSpan.FromSeconds(2)) : null;
        var provider = new ScriptedProvider([ScriptedProvider.Failure(retry)]);
        var request = new ProviderRequest(provider.Scope, ProviderExchangeTests.Attempt(), []);
        var result = await CustomProviderConsumer.ExchangeAsync(provider, request);
        Assert.Equal(ProviderOutcome.Failed, result.Outcome); Assert.Equal(ProviderError.ProviderFailed, result.Error);
        Assert.Same(retry, result.Retry); Assert.Equal(kind, result.Diagnostic.RetryKind);
        Assert.Equal(retry?.RetryAfter, result.Diagnostic.RetryAfter); Assert.Null(result.Response);
        Assert.Equal(3, result.Observation.Usage.InputTokens); Assert.Equal(2, result.Observation.Usage.OutputTokens);
        Assert.Equal(1, provider.Effects);
        Assert.DoesNotContain("SCRIPTED_PRIVATE", result + JsonSerializer.Serialize(result.Diagnostic));
        var reused = await provider.ExchangeAsync(request);
        Assert.Equal(ProviderOutcome.Rejected, reused.Outcome); Assert.Null(reused.Retry); Assert.Equal(1, provider.Effects);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("contract")]
    [InlineData("association")]
    [InlineData("generic")]
    [InlineData("unrelated-cancel")]
    [InlineData("caller-cancel")]
    [InlineData("cancelled-positive-failure")]
    public async Task GuardNeverInfersEligibilityFromSuccessRejectionGenericFailureOrCallerCancellation(string mode)
    {
        using var cancel = new CancellationTokenSource();
        var provider = new DelegateProvider(ProviderExchangeTests.Scope, (request, observation, token) =>
        {
            observation.ObserveDispatch(DispatchExposure.Dispatched); observation.CaptureUsage(new(7, 4));
            if (mode == "success") return ValueTask.FromResult(ProviderExchangeTests.Final(request));
            if (mode == "association") return ValueTask.FromResult(new ProviderResponse(request.Scope, ProviderExchangeTests.Attempt(), ProviderFinish.Final, "f", []));
            if (mode == "contract") throw new ProviderContractException(ProviderError.InvalidEncoding);
            if (mode == "unrelated-cancel") throw new OperationCanceledException("CANARY transient retry", new CancellationToken(true));
            if (mode is "caller-cancel" or "cancelled-positive-failure") cancel.Cancel();
            if (mode == "caller-cancel") throw new OperationCanceledException(token);
            if (mode == "cancelled-positive-failure") throw new ProviderFailureException(new(ProviderRetryKind.Transient));
            throw new InvalidOperationException("CANARY transient throttled retry");
        });
        var request = ProviderExchangeTests.Request();
        var result = await provider.ExchangeAsync(request, cancel.Token);
        Assert.Null(result.Retry); Assert.Null(result.Diagnostic.RetryKind); Assert.Null(result.Diagnostic.RetryAfter);
        Assert.Equal(7, result.Observation.Usage.InputTokens); Assert.Equal(4, result.Observation.Usage.OutputTokens);
        result.ValidateFor(request);
        Assert.DoesNotContain("CANARY", result + JsonSerializer.Serialize(result.Diagnostic));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task ForwardedPositiveFailureCannotImportAnotherAttemptsUsageOrRetry(int field)
    {
        var scope = new ProviderScope("p", "m");
        var guarded = new ScriptedProvider([ScriptedProvider.Failure(new(ProviderRetryKind.Throttled))], scope);
        ProviderExchangeResult? foreign = null;
        var forwarding = new InterfaceScriptedProvider(scope, async (request, token) =>
        {
            var a = request.Attempt;
            var wrong = new ProviderAttempt(field == 0 ? Guid.NewGuid() : a.ExecutionId, field == 1 ? Guid.NewGuid() : a.LogicalCallId,
                field == 2 ? Guid.NewGuid() : a.PhysicalAttemptId, field == 3 ? a.AttemptNumber + 1 : a.AttemptNumber);
            foreign = await guarded.ExchangeAsync(new(scope, wrong, []), token);
            return foreign;
        });
        var hostRequest = RuntimeFixture.Request();
        var consumer = new ConfigurationConsumer(new(forwarding, [], new RuntimeHooks()), hostRequest);
        var request = consumer.CreateRequest(new(hostRequest.ExecutionId, Guid.NewGuid(), Guid.NewGuid()));
        var result = await consumer.RunAsync(request);
        Assert.Equal(ProviderError.InvalidAssociation, result.Settlement!.ProviderError); Assert.Null(result.Provider);
        Assert.Equal(DispatchExposure.Unknown, result.Observation.Exposure); Assert.Null(result.Observation.Usage.InputTokens);
        Assert.Equal(ProviderRetryKind.Throttled, foreign!.Retry!.Kind); Assert.Equal(1, forwarding.Calls);
        Assert.Equal(ProviderError.InvalidAssociation, Assert.Throws<ProviderContractException>(() => foreign.ValidateFor(request)).Error);
    }

    [Fact]
    public async Task ForwardedAcceptedPayloadUsesConsumersBoundsWhileKeepingItsAssociatedUsage()
    {
        var scope = new ProviderScope("p", "m");
        var guarded = new ScriptedProvider([(request, observation, _) =>
        {
            observation.CaptureUsage(new(3, 2));
            return ValueTask.FromResult(new ProviderResponse(scope, request.Attempt, ProviderFinish.Final, new('x', 129), []));
        }], scope);
        var forwarding = new InterfaceScriptedProvider(scope, (request, token) => guarded.ExchangeAsync(new(scope, request.Attempt, request.Inputs), token));
        var hostRequest = RuntimeFixture.Request();
        var consumer = new ConfigurationConsumer(new(forwarding, [], new RuntimeHooks(), new(maximumResponseBytes: 128)), hostRequest);
        var result = await consumer.RunAsync(consumer.CreateRequest(new(hostRequest.ExecutionId, Guid.NewGuid(), Guid.NewGuid())));
        Assert.Equal(ProviderError.LimitExceeded, result.Settlement!.ProviderError); Assert.Null(result.Provider);
        Assert.Equal(3, result.Observation.Usage.InputTokens); Assert.Equal(2, result.Observation.Usage.OutputTokens);
        Assert.Equal(1, forwarding.Calls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task ForwardedPositiveFailureCannotImportAnotherProviderScopeWithTheSameAttempt(int field)
    {
        var declared = new ProviderScope("p", "m");
        var actual = new ProviderScope(field == 1 ? "p" : "foreign-provider", field == 0 ? "m" : "foreign-model");
        var guarded = new ScriptedProvider([ScriptedProvider.Failure(new(ProviderRetryKind.Throttled))], actual);
        ProviderExchangeResult? foreign = null;
        var forwarding = new InterfaceScriptedProvider(declared, async (request, token) =>
        {
            foreign = await guarded.ExchangeAsync(new(actual, request.Attempt, request.Inputs), token); return foreign;
        });
        var hostRequest = RuntimeFixture.Request();
        var consumer = new ConfigurationConsumer(new(forwarding, [], new RuntimeHooks()), hostRequest);
        var request = consumer.CreateRequest(new(hostRequest.ExecutionId, Guid.NewGuid(), Guid.NewGuid()));
        var result = await consumer.RunAsync(request);
        Assert.Null(result.Provider); Assert.Equal(ProviderError.InvalidAssociation, result.Settlement!.ProviderError);
        Assert.Null(result.Observation.Usage.InputTokens); Assert.Equal(DispatchExposure.Unknown, result.Observation.Exposure);
        Assert.Equal(1, forwarding.Calls);
        Assert.Same(actual, foreign!.Scope); Assert.Equal(ProviderRetryKind.Throttled, foreign.Retry!.Kind);
        Assert.Equal(ProviderError.InvalidAssociation, Assert.Throws<ProviderContractException>(() => foreign.ValidateFor(request)).Error);
        var replayed = new InterfaceScriptedProvider(declared, (_, _) => ValueTask.FromResult(foreign));
        await Assert.ThrowsAsync<ProviderContractException>(() => CustomProviderConsumer.ExchangeAsync(replayed, request).AsTask());
        Assert.DoesNotContain("foreign-", JsonSerializer.Serialize(foreign.Diagnostic));
    }

    [Fact]
    public async Task ProductionRuntimeDoesNotEnableAutomaticRetryFromScriptedMetadata()
    {
        var provider = new ScriptedProvider([ScriptedProvider.Failure(new(ProviderRetryKind.Transient)), ScriptedProvider.Final]);
        var result = await RuntimeFixture.Agent(provider).ExecuteAsync(RuntimeFixture.Request(units: 4));
        Assert.Equal(AgentTerminationReason.Failed, result.Reason); Assert.Equal(1, provider.Effects);
        Assert.Equal(3, Assert.Single(result.Usage!.Attempts).Usage.InputTokens);
    }
}

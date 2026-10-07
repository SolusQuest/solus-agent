using System.Text.Json;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.RuntimeConfiguration;

public sealed class SettlementProtocolTests
{
    [Theory]
    [InlineData("missing", RuntimeStop.SettlementMissing)]
    [InlineData("failed", RuntimeStop.SettlementFailed)]
    [InlineData("unknown", RuntimeStop.SettlementUnknown)]
    [InlineData("exception", RuntimeStop.SettlementFailed)]
    [InlineData("unrelated-cancel", RuntimeStop.SettlementFailed)]
    [InlineData("stop", RuntimeStop.HostStopped)]
    public async Task DeliveryStopPreservesProviderEvidenceAndPreventsSubsequentAdmission(string kind, RuntimeStop expected)
    {
        var f = new ConfigurationFixture();
        f.Hooks.Settlement = (s, _) => kind switch
        {
            "missing" => ValueTask.FromResult<SettlementAcknowledgement?>(null),
            "failed" => ValueTask.FromResult<SettlementAcknowledgement?>(new(s.Exposure, RuntimeHookStatus.Failed)),
            "unknown" => ValueTask.FromResult<SettlementAcknowledgement?>(new(s.Exposure, RuntimeHookStatus.Unknown)),
            "exception" => throw new InvalidOperationException(ConfigurationHooks.CredentialCanary),
            "unrelated-cancel" => throw new OperationCanceledException(ConfigurationHooks.CredentialCanary, CancellationToken.None),
            _ => ValueTask.FromResult<SettlementAcknowledgement?>(new(s.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Stop)),
        };
        var result = await f.Consumer.RunAsync(f.Exchange);
        Assert.Equal(expected, result.SettlementStop); Assert.Equal(RuntimeStop.None, result.AdmissionStop);
        Assert.Equal(1, f.Provider.Effects); Assert.Equal(ProviderOutcome.Succeeded, result.Provider!.Outcome);
        Assert.Equal(DispatchExposure.Dispatched, result.Observation.Exposure);
        Assert.Equal(3, result.Observation.Usage.InputTokens); Assert.Equal(2, result.Observation.Usage.OutputTokens);
        Assert.Same(result.Observation, f.Hooks.LastSettlement!.Observation);
        Assert.DoesNotContain(ConfigurationHooks.CredentialCanary, JsonSerializer.Serialize(result.Diagnostic));
        var next = await f.Consumer.RunAsync(f.Consumer.CreateRequest(f.Next()));
        Assert.Equal(expected, next.AdmissionStop); Assert.Equal(1, f.Provider.Effects);
        Assert.Equal(1, f.Hooks.Events.Count(x => x == "expose"));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public async Task MismatchedSettlementCannotControlTheRealAttempt(int field)
    {
        var f = new ConfigurationFixture();
        f.Hooks.Settlement = (s, _) => ValueTask.FromResult<SettlementAcknowledgement?>(new(ConfigurationFixture.Change(s.Exposure, field),
            RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue));
        var result = await f.Consumer.RunAsync(f.Exchange);
        Assert.Equal(RuntimeStop.SettlementMismatch, result.SettlementStop); Assert.Equal(1, f.Provider.Effects);
        Assert.True(f.Attempt.Matches(result.Settlement!.Exposure.Attempt)); Assert.Equal(3, result.Observation.Usage.InputTokens);
        Assert.Equal(RuntimeStop.SettlementMismatch, (await f.Consumer.RunAsync(f.Consumer.CreateRequest(f.Next()))).AdmissionStop);
        Assert.Equal(1, f.Provider.Effects);
    }

    [Fact]
    public async Task NonCooperatingSettlementCancellationRetainsUsageAndLateContinueCannotReopenAdmission()
    {
        var f = new ConfigurationFixture(); using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource<RuntimeSettlement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receipt = new TaskCompletionSource<SettlementAcknowledgement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Hooks.Settlement = (s, _) => { entered.SetResult(s); return new(receipt.Task); };
        var run = f.Consumer.RunAsync(f.Exchange, settlementToken: cancel.Token).AsTask();
        var settlement = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, f.Provider.Effects); Assert.Equal(3, settlement.Observation.Usage.InputTokens);
        cancel.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RuntimeStop.SettlementUnknown, result.SettlementStop); Assert.Same(settlement.Observation, result.Observation);
        receipt.SetResult(new(settlement.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue)); await receipt.Task;
        Assert.Equal(RuntimeStop.SettlementUnknown, result.SettlementStop);
        Assert.Equal(RuntimeStop.SettlementUnknown, (await f.Consumer.RunAsync(f.Consumer.CreateRequest(f.Next()))).AdmissionStop);
        Assert.Equal(1, f.Provider.Effects);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task LaterProviderRejectionRetainsKnownOrUnavailableUsageIndependently(bool known)
    {
        var f = new ConfigurationFixture(); f.Provider.CaptureUsage = known;
        f.Provider.Response = r => new(r.Scope, new(r.Attempt.ExecutionId, r.Attempt.LogicalCallId, Guid.NewGuid()), ProviderFinish.Final, "invalid association", []);
        f.Hooks.Settlement = (s, _) => ValueTask.FromResult<SettlementAcknowledgement?>(new(s.Exposure, RuntimeHookStatus.Failed));
        var result = await f.Consumer.RunAsync(f.Exchange);
        Assert.Equal(ProviderOutcome.Rejected, result.Provider!.Outcome); Assert.Equal(ProviderError.InvalidAssociation, result.Provider.Error);
        Assert.Equal(DispatchExposure.Dispatched, result.Observation.Exposure);
        Assert.Equal(known ? UsageCompleteness.Complete : UsageCompleteness.Unavailable, result.Observation.Usage.Completeness);
        Assert.Equal(known ? 3L : (long?)null, result.Observation.Usage.InputTokens);
        Assert.Equal(RuntimeStop.SettlementFailed, result.SettlementStop); Assert.Same(result.Observation, result.Settlement!.Observation);
    }

    [Fact]
    public async Task AccountingSettlementClaimIsIndependentFromHostHookAcknowledgement()
    {
        var f = new ConfigurationFixture();
        var provider = new DelegateProvider(f.Configuration.Scope, (r, capture, _) =>
        {
            capture.ObserveDispatch(DispatchExposure.Dispatched);
            capture.CaptureUsage(new(1, 1), new(UsageSettlement.Settled));
            return ValueTask.FromResult(new ProviderResponse(r.Scope, r.Attempt, ProviderFinish.Final, "synthetic", []));
        });
        var configuration = new SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration(provider, [], f.Hooks);
        var consumer = new ConfigurationConsumer(configuration, f.Request);
        f.Hooks.Settlement = (s, _) => ValueTask.FromResult<SettlementAcknowledgement?>(new(s.Exposure, RuntimeHookStatus.Unknown));
        var result = await consumer.RunAsync(consumer.CreateRequest(f.Attempt));
        Assert.Equal(UsageSettlement.Settled, result.Observation.Accounting!.Settlement);
        Assert.Equal(RuntimeStop.SettlementUnknown, result.SettlementStop);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task InvalidExtensionObservationStillClosesCurrentAttemptWithoutClaimingForeignUsage(int field)
    {
        var f = new ConfigurationFixture(); var exposure = new RuntimeExposure(f.Configuration.Scope, f.Attempt, ExposureStrength.Volatile);
        var foreign = ConfigurationFixture.Change(exposure, field).Attempt;
        var retained = await f.Provider.ExchangeAsync(f.Configuration.CreateRequest(foreign, []));
        var invalid = new WrongObservationProvider(f.Provider, retained);
        var configuration = new SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration(invalid, [], f.Hooks);
        var consumer = new ConfigurationConsumer(configuration, f.Request);
        var result = await consumer.RunAsync(consumer.CreateRequest(f.Attempt));
        Assert.Equal(1, invalid.Effects);
        Assert.Equal(DispatchExposure.Unknown, result.Observation.Exposure);
        Assert.Equal(UsageCompleteness.Unavailable, result.Observation.Usage.Completeness);
        Assert.Equal(f.Attempt.PhysicalAttemptId, result.Observation.PhysicalAttemptId);
        Assert.Equal(ProviderOutcome.Rejected, result.Settlement!.ProviderOutcome);
        Assert.Equal(ProviderError.InvalidAssociation, result.Settlement.ProviderError);
        Assert.Null(result.Provider);
        Assert.Equal(ProviderOutcome.Rejected, result.Diagnostic.ProviderOutcome);
        Assert.Equal(ProviderError.InvalidAssociation, result.Diagnostic.ProviderError);
        Assert.Same(result.Observation, f.Hooks.LastSettlement!.Observation);
        Assert.Equal(3, retained.Observation.Usage.InputTokens);
    }

    private sealed class WrongObservationProvider(IModelProvider source, ProviderExchangeResult foreign) : IModelProvider
    {
        public ProviderScope Scope => source.Scope;
        public ProviderCapabilities Capabilities => source.Capabilities;
        public int Effects { get; private set; }
        public ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken token = default)
        { Effects++; return ValueTask.FromResult(foreign); }
    }
}

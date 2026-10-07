using System.Text.Json;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.RuntimeConfiguration;

public sealed class ExposureProtocolTests
{
    [Theory]
    [InlineData(ExposureStrength.Volatile)]
    [InlineData(ExposureStrength.Durable)]
    public async Task ActualHostProtocolAwaitsPermissionBeforeDispatchAndCorrelatesSettlement(ExposureStrength strength)
    {
        var f = new ConfigurationFixture(strength: strength);
        var entered = new TaskCompletionSource<RuntimeExposure>(TaskCreationOptions.RunContinuationsAsynchronously);
        var permission = new TaskCompletionSource<ExposureAcknowledgement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Hooks.Exposure = (exposure, _) => { entered.SetResult(exposure); return new(permission.Task); };
        var run = f.Consumer.RunAsync(f.Exchange).AsTask();
        var exposed = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, f.Provider.Effects);
        Assert.False(run.IsCompleted);
        permission.SetResult(new(exposed, RuntimeHookStatus.Acknowledged, ExposureDecision.Permit, strength));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "expose", "ack", "dispatch", "settle" }, f.Hooks.Events);
        Assert.Equal(1, f.Provider.Effects);
        Assert.Equal(RuntimeStop.None, result.AdmissionStop);
        Assert.Equal(RuntimeStop.None, result.SettlementStop);
        Assert.True(f.Attempt.Matches(result.Settlement!.Exposure.Attempt));
        Assert.Same(result.Observation, f.Hooks.LastSettlement!.Observation);
        Assert.Equal(DispatchExposure.Dispatched, result.Observation.Exposure);
        Assert.Equal(3, result.Observation.Usage.InputTokens);
    }

    [Theory]
    [InlineData("denied", RuntimeStop.ExposureDenied)]
    [InlineData("missing", RuntimeStop.ExposureMissing)]
    [InlineData("failed", RuntimeStop.ExposureFailed)]
    [InlineData("unknown", RuntimeStop.ExposureUnknown)]
    [InlineData("exception", RuntimeStop.ExposureFailed)]
    [InlineData("unrelated-cancel", RuntimeStop.ExposureFailed)]
    [InlineData("volatile", RuntimeStop.DurableAcknowledgementRequired)]
    public async Task NonAuthorizingAcknowledgementNeverReachesProvider(string kind, RuntimeStop expected)
    {
        var f = new ConfigurationFixture(strength: ExposureStrength.Durable);
        f.Hooks.Exposure = (e, _) => kind switch
        {
            "denied" => ValueTask.FromResult<ExposureAcknowledgement?>(new(e, RuntimeHookStatus.Acknowledged, ExposureDecision.Deny)),
            "missing" => ValueTask.FromResult<ExposureAcknowledgement?>(null),
            "failed" => ValueTask.FromResult<ExposureAcknowledgement?>(new(e, RuntimeHookStatus.Failed)),
            "unknown" => ValueTask.FromResult<ExposureAcknowledgement?>(new(e, RuntimeHookStatus.Unknown)),
            "exception" => throw new InvalidOperationException(ConfigurationHooks.CredentialCanary),
            "unrelated-cancel" => throw new OperationCanceledException(ConfigurationHooks.CredentialCanary, CancellationToken.None),
            _ => ValueTask.FromResult<ExposureAcknowledgement?>(new(e, RuntimeHookStatus.Acknowledged, ExposureDecision.Permit, ExposureStrength.Volatile)),
        };
        var result = await f.Consumer.RunAsync(f.Exchange);
        Assert.Equal(expected, result.AdmissionStop);
        Assert.Equal(0, f.Provider.Effects);
        Assert.Equal(DispatchExposure.NotDispatched, result.Observation.Exposure);
        Assert.Equal(UsageCompleteness.Unavailable, result.Observation.Usage.Completeness);
        Assert.Null(result.Provider);
        Assert.Null(result.Settlement!.ProviderOutcome);
        Assert.Null(result.Settlement.ProviderError);
        Assert.DoesNotContain(ConfigurationHooks.CredentialCanary, JsonSerializer.Serialize(result.Diagnostic));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public async Task EveryExposureAssociationFieldFailsClosed(int field)
    {
        var f = new ConfigurationFixture();
        f.Hooks.Exposure = (e, _) => ValueTask.FromResult<ExposureAcknowledgement?>(
            new(ConfigurationFixture.Change(e, field), RuntimeHookStatus.Acknowledged, ExposureDecision.Permit, ExposureStrength.Durable));
        var result = await f.Consumer.RunAsync(f.Exchange);
        Assert.Equal(RuntimeStop.ExposureMismatch, result.AdmissionStop);
        Assert.Equal(0, f.Provider.Effects);
        Assert.True(f.Attempt.Matches(result.Settlement!.Exposure.Attempt));
    }

    [Fact]
    public async Task EquivalentFreshReceiptObjectsMatchByFullValue()
    {
        var f = new ConfigurationFixture();
        f.Hooks.Exposure = (e, _) => ValueTask.FromResult<ExposureAcknowledgement?>(new(new(new(e.Scope.Provider, e.Scope.Model),
            new(e.Attempt.ExecutionId, e.Attempt.LogicalCallId, e.Attempt.PhysicalAttemptId, e.Attempt.AttemptNumber), e.RequiredAcknowledgement),
            RuntimeHookStatus.Acknowledged, ExposureDecision.Permit, ExposureStrength.Durable));
        Assert.Equal(RuntimeStop.None, (await f.Consumer.RunAsync(f.Exchange)).AdmissionStop);
        Assert.Equal(1, f.Provider.Effects);
    }

    [Fact]
    public async Task PreCancelledCallHasNoHostExchangeOrEffect()
    {
        var f = new ConfigurationFixture(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var result = await f.Consumer.RunAsync(f.Exchange, cancel.Token);
        Assert.Equal(RuntimeStop.Cancelled, result.AdmissionStop);
        Assert.Empty(f.Hooks.Events); Assert.Equal(0, f.Provider.Effects); Assert.Null(result.Settlement);
    }

    [Fact]
    public async Task CancellationOfNonCooperatingPendingAcknowledgementCannotAdmitLatePermission()
    {
        var f = new ConfigurationFixture(); using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource<RuntimeExposure>(TaskCreationOptions.RunContinuationsAsynchronously);
        var permission = new TaskCompletionSource<ExposureAcknowledgement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Hooks.Exposure = (e, _) => { entered.SetResult(e); return new(permission.Task); };
        var run = f.Consumer.RunAsync(f.Exchange, cancel.Token).AsTask();
        var exposed = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RuntimeStop.Cancelled, result.AdmissionStop); Assert.Equal(0, f.Provider.Effects);
        Assert.Equal(DispatchExposure.NotDispatched, result.Observation.Exposure);
        permission.SetResult(new(exposed, RuntimeHookStatus.Acknowledged, ExposureDecision.Permit, ExposureStrength.Durable));
        await permission.Task;
        Assert.Equal(0, f.Provider.Effects); Assert.Equal(RuntimeStop.Cancelled, result.AdmissionStop);
        Assert.Equal(RuntimeStop.Cancelled, (await f.Consumer.RunAsync(f.Consumer.CreateRequest(f.Next()))).AdmissionStop);
    }

    [Fact]
    public async Task PositiveAcknowledgementThenCancellationAtAdmissionCutClosesAsNotDispatched()
    {
        var f = new ConfigurationFixture(strength: ExposureStrength.Durable); using var cancel = new CancellationTokenSource();
        f.Hooks.Exposure = (e, _) => { cancel.Cancel(); return ValueTask.FromResult<ExposureAcknowledgement?>(new(e, RuntimeHookStatus.Acknowledged,
            ExposureDecision.Permit, ExposureStrength.Durable)); };
        var result = await f.Consumer.RunAsync(f.Exchange, cancel.Token);
        Assert.Equal(RuntimeStop.Cancelled, result.AdmissionStop); Assert.Equal(0, f.Provider.Effects);
        Assert.Contains("ack", f.Hooks.Events); Assert.Contains("settle", f.Hooks.Events);
        Assert.Equal(DispatchExposure.NotDispatched, f.Hooks.LastSettlement!.Observation.Exposure);
        Assert.Null(f.Hooks.LastSettlement.ProviderOutcome);
    }

    [Fact]
    public async Task CancellationAfterDispatchCannotEraseUsageOrClaimRemoteStop()
    {
        var f = new ConfigurationFixture(); using var cancel = new CancellationTokenSource(); f.Provider.AfterEffect = () => cancel.Cancel();
        var result = await f.Consumer.RunAsync(f.Exchange, cancel.Token);
        Assert.Equal(1, f.Provider.Effects); Assert.Equal(RuntimeStop.Cancelled, result.AdmissionStop);
        Assert.Equal(ProviderOutcome.Cancelled, result.Provider!.Outcome);
        Assert.Equal(DispatchExposure.Dispatched, result.Observation.Exposure); Assert.Equal(3, result.Observation.Usage.InputTokens);
        Assert.Equal(2, result.Observation.Usage.OutputTokens); Assert.Equal(RuntimeStop.None, result.SettlementStop);
        Assert.Same(result.Observation, f.Hooks.LastSettlement!.Observation);
    }

    [Fact]
    public async Task RetryUsesNewPhysicalAttemptFreshPermissionAndIndependentSettlement()
    {
        var f = new ConfigurationFixture(); f.Provider.Response = _ => throw new InvalidOperationException(ConfigurationProvider.CredentialCanary);
        var first = await f.Consumer.RunAsync(f.Exchange);
        var retry = f.Next(retry: true);
        var second = await f.Consumer.RunAsync(f.Consumer.CreateRequest(retry));
        Assert.Equal(ProviderOutcome.Failed, first.Provider!.Outcome); Assert.Equal(ProviderOutcome.Failed, second.Provider!.Outcome);
        Assert.Equal(2, f.Provider.Effects); Assert.Equal(2, f.Hooks.Events.Count(x => x == "expose")); Assert.Equal(2, f.Hooks.Events.Count(x => x == "settle"));
        Assert.Equal(first.Observation.LogicalCallId, second.Observation.LogicalCallId);
        Assert.NotEqual(first.Observation.PhysicalAttemptId, second.Observation.PhysicalAttemptId); Assert.Equal(2, second.Observation.AttemptNumber);
        _ = new AgentRunUsage(f.Attempt.ExecutionId, UsageInventoryCoverage.Complete, [first.Observation, second.Observation]);
    }

    [Fact]
    public async Task StaleRetryAcknowledgementDoesNotAuthorizeSecondPhysicalAttempt()
    {
        var f = new ConfigurationFixture(); ExposureAcknowledgement? old = null;
        f.Hooks.Exposure = (e, _) => ValueTask.FromResult<ExposureAcknowledgement?>(old ??= new(e, RuntimeHookStatus.Acknowledged, ExposureDecision.Permit, ExposureStrength.Durable));
        await f.Consumer.RunAsync(f.Exchange);
        var result = await f.Consumer.RunAsync(f.Consumer.CreateRequest(f.Next(retry: true)));
        Assert.Equal(RuntimeStop.ExposureMismatch, result.AdmissionStop); Assert.Equal(1, f.Provider.Effects);
    }
}

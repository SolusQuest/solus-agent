using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Execution;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Execution;

public sealed class RetryTests
{
    internal static AgentRequest Request(AgentUsageLimits? limits = null, int units = 8, TimeSpan? duration = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), "i", [], new(units, duration ?? TimeSpan.FromSeconds(10)), AgentCapability.None,
            limits ?? new(retryPolicy: new(3)));

    public static IEnumerable<object[]> EnabledCases()
    {
        foreach (var candidate in new[] { false, true })
        foreach (var kind in Enum.GetValues<ProviderRetryKind>())
        foreach (var maximum in new[] { 0, 1, 2, 3 }) yield return [candidate, kind, maximum];
    }
    [Theory, MemberData(nameof(EnabledCases))]
    public async Task ExplicitPermissionKeepsOneLogicalCallAndAcceptsOnce(bool candidate, ProviderRetryKind kind, int maximum)
    {
        var seen = new List<ProviderRequest>();
        var provider = new DelegateProvider(new("p", "m"), (r, o, token) =>
        {
            seen.Add(r); o.ObserveDispatch(DispatchExposure.Dispatched); o.CaptureUsage(new(3, 2));
            if (seen.Count == 1) throw new ProviderFailureException(new(kind));
            return ValueTask.FromResult(RuntimeFixture.Final(r));
        });
        var hooks = new RuntimeHooks();
        var request = Request(new(maximumLogicalCalls: 1, maximumPhysicalDispatches: 2,
            accountingPolicy: new(new(5, 4), 8, 6), retryPolicy: maximum == 0 ? null : new(maximum)), units: 1);
        var outcome = await AccountingTests.Execute(AccountingTests.Agent(provider, hooks), request, candidate);
        var success = maximum >= 2;
        Assert.Equal(success ? AgentTerminationReason.Completed : AgentTerminationReason.Failed, outcome.Reason);
        Assert.Equal(success ? 1 : 0, outcome.CompletedWorkUnits);
        Assert.Equal(success ? 2 : 1, seen.Count); Assert.Equal(seen.Count, hooks.Settlements.Count);
        Assert.Equal(seen.Count * 3, outcome.Usage!.Accounting!.Input.MeasuredTokens);
        Assert.Single(outcome.Usage.Attempts.Select(a => a.LogicalCallId).Distinct());
        Assert.Equal(Enumerable.Range(1, seen.Count), outcome.Usage.Attempts.Select(a => a.AttemptNumber));
        Assert.Equal(seen.Count, seen.Select(r => r.Attempt.PhysicalAttemptId).Distinct().Count());
        if (success)
        {
            Assert.NotSame(seen[0], seen[1]); Assert.NotSame(seen[0].Observation, seen[1].Observation);
            Assert.Equal(seen[0].Inputs, seen[1].Inputs); Assert.Same(seen[0].Bounds, seen[1].Bounds);
            Assert.Equal(3, hooks.Exposures[1].Accounting!.Input.MeasuredTokens);
            Assert.Equal(5, hooks.Exposures[1].Accounting!.Input.ReservedTokens);
            Assert.True(hooks.Exposures[1].Accounting!.Attempts[0].IsFinalized);
            Assert.Equal(ProviderError.ObservationClosed, Assert.Throws<ProviderContractException>(() => seen[0].Observation.CaptureUsage(new())).Error);
        }
    }

    [Fact]
    public void PolicyRejectsUnboundedOrInconsistentConfiguration()
    {
        foreach (var attempts in new[] { 0, -1, 65, int.MaxValue }) Assert.Throws<ArgumentOutOfRangeException>(() => new AgentRetryPolicy(attempts));
        foreach (var delay in new[] { TimeSpan.FromTicks(-1), AgentRetryPolicy.DelayCeiling + TimeSpan.FromTicks(1), TimeSpan.MaxValue })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new AgentRetryPolicy(2, backoff: delay));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AgentRetryPolicy(2, maximumDelay: delay));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentRetryPolicy(2, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)));
        Assert.Equal(TimeSpan.Zero, new AgentRetryPolicy(1, maximumDelay: TimeSpan.Zero).Backoff);
        Assert.Equal(64, new AgentRetryPolicy(64, AgentRetryPolicy.DelayCeiling).MaximumAttemptsPerLogicalCall);
    }

    [Theory]
    [InlineData(false, 1)] [InlineData(true, 1)] [InlineData(false, 3)] [InlineData(true, 3)] [InlineData(false, 64)] [InlineData(true, 64)]
    public async Task ExhaustionRetainsEveryFailureWithoutCompletedWork(bool candidate, int maximum)
    {
        var provider = new ScriptedProvider(Enumerable.Repeat(ScriptedProvider.Failure(new(ProviderRetryKind.Transient)), maximum).ToArray());
        var result = await AccountingTests.Execute(AccountingTests.Agent(provider, null), Request(new(retryPolicy: new(maximum))), candidate);
        Assert.Equal(AgentTerminationReason.Failed, result.Reason); Assert.Equal(maximum, provider.Effects);
        Assert.Equal(0, result.CompletedWorkUnits); Assert.Equal(maximum, result.Usage!.Attempts.Count);
        Assert.Equal(maximum * 3, result.Usage.InputTokens.ObservedTokens);
    }

    public static IEnumerable<object[]> LimitCases()
    {
        foreach (var candidate in new[] { false, true })
        foreach (var limit in new[] { "inventory", "physical", "input", "output", "accounting-input", "accounting-output", "unknown", "count-over-unknown", "record", "bytes" })
            yield return [candidate, limit];
    }
    [Theory, MemberData(nameof(LimitCases))]
    public async Task SharedLimitsDenyBeforeAnotherReservationOrEffect(bool candidate, string limit)
    {
        var unknown = limit is "unknown" or "count-over-unknown";
        var provider = new ScriptedProvider([(_, o, _) =>
        {
            o.CaptureUsage(unknown ? new() : new(3, 2)); throw new ProviderFailureException(new(ProviderRetryKind.Transient));
        }, ScriptedProvider.Final]);
        var hooks = new RuntimeHooks();
        var limits = new AgentUsageLimits(maximumPhysicalDispatches: limit is "physical" or "count-over-unknown" ? 1 : 4,
            inputTokenThreshold: limit == "input" || unknown ? 3 : null, outputTokenThreshold: limit == "output" ? 2 : null,
            accountingPolicy: limit.StartsWith("accounting-", StringComparison.Ordinal)
                ? new(new(5, 4), limit == "accounting-input" ? 7 : 100, limit == "accounting-output" ? 5 : 100) : null,
            retryPolicy: new(3, TimeSpan.FromMinutes(1)));
        var options = new RuntimeOptions(maximumAttempts: limit == "inventory" ? 1 : 64, maximumRecords: limit == "record" ? 1 : 64,
            maximumRetainedBytes: limit == "bytes" ? 1 : 196608);
        var result = await AccountingTests.Execute(AccountingTests.Agent(provider, hooks, options: options), Request(limits), candidate);
        Assert.Equal(limit == "unknown" ? AgentTerminationReason.Partial : AgentTerminationReason.ResourceLimit, result.Reason);
        var count = limit is "record" or "bytes" ? 0 : 1;
        Assert.Equal(count, provider.Effects); Assert.Equal(count, result.Usage!.Attempts.Count); Assert.Equal(count, hooks.Exposures.Count);
    }

    public static IEnumerable<object[]> UnknownCases()
    {
        foreach (var candidate in new[] { false, true })
        foreach (var exposure in Enum.GetValues<DispatchExposure>())
        foreach (var policy in Enum.GetValues<UnknownUsagePolicy>()) yield return [candidate, exposure, policy];
    }
    [Theory, MemberData(nameof(UnknownCases))]
    public async Task MissingMeasurementsUseOriginalLedgerAndExposure(bool candidate, DispatchExposure exposure, UnknownUsagePolicy policy)
    {
        var calls = 0;
        var provider = new DelegateProvider(new("p", "m"), (r, o, _) =>
        {
            o.ObserveDispatch(++calls == 1 ? exposure : DispatchExposure.Dispatched);
            if (calls == 1) throw new ProviderFailureException(new(ProviderRetryKind.Transient));
            o.CaptureUsage(new(1, 1)); return ValueTask.FromResult(RuntimeFixture.Final(r));
        });
        var result = await AccountingTests.Execute(AccountingTests.Agent(provider, new AccountingHostProbe { ReverseAccountingInventory = true }),
            Request(new(maximumPhysicalDispatches: exposure == DispatchExposure.NotDispatched ? 1 : 2,
                accountingPolicy: new(new(5, 4), 20, 20, policy), retryPolicy: new(2))), candidate);
        var stops = exposure != DispatchExposure.NotDispatched && policy == UnknownUsagePolicy.Stop;
        Assert.Equal(stops ? AgentTerminationReason.Partial : AgentTerminationReason.Completed, result.Reason);
        Assert.Equal(stops ? 1 : 2, calls); Assert.Null(result.Usage!.Attempts[0].Usage.InputTokens);
        Assert.Equal(exposure, result.Usage.Attempts[0].Exposure);
        Assert.Equal(exposure == DispatchExposure.NotDispatched ? AccountingDisposition.Released
            : policy == UnknownUsagePolicy.ConservativeCharge ? AccountingDisposition.ConservativeCharge : AccountingDisposition.Unresolved,
            result.Usage.Accounting!.Attempts[0].Input.Disposition);
        Assert.Equal(0, result.Usage.Accounting.Input.ReservedTokens);
    }

    public static IEnumerable<object[]> ClosureCases()
    {
        foreach (var candidate in new[] { false, true })
        foreach (var mode in new[] { "stop", "missing", "failed", "unknown", "throw", "scope", "physical", "ordinal", "accounting" }) yield return [candidate, mode];
    }
    [Theory, MemberData(nameof(ClosureCases))]
    public async Task FailedAttemptPlusInvalidClosureNeverRetriesOrErasesUsage(bool candidate, string mode)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Failure(new(ProviderRetryKind.Transient)), ScriptedProvider.Final]);
        var hooks = new RuntimeHooks { After = (s, _) =>
        {
            if (mode == "throw") throw new InvalidOperationException();
            var exposure = mode is "scope" or "physical" or "ordinal"
                ? PermissionAndAdmissionTests.Change(s.Exposure, mode == "scope" ? 4 : mode == "physical" ? 2 : 3) : s.Exposure;
            return ValueTask.FromResult<SettlementAcknowledgement?>(mode switch
            {
                "missing" => null,
                "failed" => new(exposure, RuntimeHookStatus.Failed),
                "unknown" => new(exposure, RuntimeHookStatus.Unknown),
                _ => new(exposure, RuntimeHookStatus.Acknowledged, mode == "stop" ? RuntimeContinuation.Stop : RuntimeContinuation.Continue,
                    mode == "accounting" ? null : s.Accounting),
            });
        } };
        var result = await AccountingTests.Execute(AccountingTests.Agent(provider, hooks),
            Request(new(accountingPolicy: new(new(5, 4), 20, 20), retryPolicy: new(3))), candidate);
        Assert.Equal(AgentTerminationReason.Failed, result.Reason); Assert.Equal(0, result.CompletedWorkUnits);
        Assert.Equal(1, provider.Effects); Assert.Single(hooks.Settlements); Assert.Single(result.Usage!.Attempts);
        Assert.Equal(3, result.Usage.Accounting!.Input.MeasuredTokens);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OptionalAccountingStillRequiresHooksBeforeFirstAttempt(bool candidate)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Failure(new(ProviderRetryKind.Transient)), ScriptedProvider.Final]);
        var result = await AccountingTests.Execute(AccountingTests.Agent(provider, null),
            Request(new(accountingPolicy: new(new(1, 1)), retryPolicy: new(2))), candidate);
        Assert.Equal(AgentTerminationReason.Failed, result.Reason); Assert.Empty(result.Usage!.Attempts); Assert.Equal(0, provider.Effects);
    }

    public static IEnumerable<object[]> IneligibleCases()
    {
        foreach (var candidate in new[] { false, true })
        foreach (var mode in new[] { "bare", "throw", "cancel", "invalid", "scope", "execution", "logical", "physical", "ordinal", "conflict" }) yield return [candidate, mode];
    }
    [Theory, MemberData(nameof(IneligibleCases))]
    public async Task OnlyValidatedPositiveResultsCanAuthorizeRetry(bool candidate, string mode)
    {
        var guarded = new DelegateProvider(new(mode == "scope" ? "other" : "p", "m"), (r, o, _) =>
        {
            o.ObserveDispatch(DispatchExposure.Dispatched); o.CaptureUsage(new(3, 2));
            if (mode == "cancel") throw new OperationCanceledException();
            if (mode == "invalid") return ValueTask.FromResult(new ProviderResponse(r.Scope, r.Attempt, ProviderFinish.Final, "", []));
            throw new ProviderFailureException(mode == "bare" ? null : new(ProviderRetryKind.Transient));
        });
        var forwarding = new InterfaceScriptedProvider(new("p", "m"), (r, token) =>
        {
            if (mode == "throw") throw new ProviderFailureException(new(ProviderRetryKind.Transient));
            if (mode == "conflict") r.Observation.CaptureUsage(new(99, 99));
            var a = r.Attempt;
            var changed = new ProviderAttempt(mode == "execution" ? Guid.NewGuid() : a.ExecutionId,
                mode == "logical" ? Guid.NewGuid() : a.LogicalCallId, mode == "physical" ? Guid.NewGuid() : a.PhysicalAttemptId,
                mode == "ordinal" ? a.AttemptNumber + 1 : a.AttemptNumber);
            return guarded.ExchangeAsync(new(mode == "scope" ? new("other", "m") : r.Scope, changed, r.Inputs), token);
        });
        var result = await AccountingTests.Execute(AccountingTests.Agent(forwarding, new RuntimeHooks()), Request(), candidate);
        Assert.Equal(AgentTerminationReason.Failed, result.Reason); Assert.Equal(1, forwarding.Calls);
        Assert.Single(result.Usage!.Attempts); Assert.Equal(0, result.CompletedWorkUnits);
    }

    [Fact]
    public async Task RetryAdmissionRejectsUnfinalizedReusedForeignAndAcceptedRequests()
    {
        var request = Request(); using var cut = new RunCut(TimeProvider.System, request.Bounds.MaximumDuration, CancellationToken.None);
        var state = new RunState(request, new(new ScriptedProvider([ScriptedProvider.Final]), [], new RuntimeHooks()), new(), cut);
        state.Initialize(); var first = state.AdmitTurn()!;
        Assert.Throws<ProviderContractException>(() => state.AdmitRetry(first));
        state.Retain(first.Attempt, first.Observation.Seal());
        Assert.Throws<ProviderContractException>(() => state.AdmitRetry(new(first.Scope, first.Attempt, first.Inputs)));
        var second = state.AdmitRetry(first)!;
        Assert.Equal(2, second.Attempt.AttemptNumber);
        Assert.Throws<ProviderContractException>(() => state.AdmitRetry(first));
        state.Retain(second.Attempt, second.Observation.Seal());
        Assert.Throws<ProviderContractException>(() => state.Retain(new(second.Attempt.ExecutionId, second.Attempt.LogicalCallId,
            second.Attempt.PhysicalAttemptId, 1), second.Observation.Snapshot()));
        var acceptedState = new RunState(request, new(new ScriptedProvider([ScriptedProvider.Final]), [], new RuntimeHooks()), new(), cut);
        acceptedState.Initialize(); var acceptedRequest = acceptedState.AdmitTurn()!;
        await ProviderAttemptOperation.ExecuteAsync(acceptedState, acceptedRequest);
        Assert.Throws<ProviderContractException>(() => acceptedState.AdmitRetry(acceptedRequest));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AggregateOverflowRetainsFailedAttemptsAndDeniesThirdReservation(bool candidate)
    {
        var provider = new ScriptedProvider([(_, o, _) =>
        { o.CaptureUsage(new(long.MaxValue, 1)); throw new ProviderFailureException(new(ProviderRetryKind.Transient)); },
            (_, o, _) => { o.CaptureUsage(new(1, 1)); throw new ProviderFailureException(new(ProviderRetryKind.Transient)); }, ScriptedProvider.Final]);
        var result = await AccountingTests.Execute(AccountingTests.Agent(provider, new RuntimeHooks()),
            Request(new(accountingPolicy: new(new(0, 0)), retryPolicy: new(3))), candidate);
        Assert.Equal(AgentTerminationReason.ResourceLimit, result.Reason); Assert.Equal(2, provider.Effects);
        Assert.Equal(2, result.Usage!.Attempts.Count); Assert.Null(result.Usage.Accounting!.Input.AccountedTokens);
        Assert.Equal(long.MaxValue, result.Usage.Attempts[0].Usage.InputTokens);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RetryStillRequiresFreshExposurePermission(bool candidate)
    {
        var provider = new ScriptedProvider([ScriptedProvider.Failure(new(ProviderRetryKind.Transient)), ScriptedProvider.Final]);
        var hooks = new RuntimeHooks { Before = (e, _) => ValueTask.FromResult<ExposureAcknowledgement?>(
            e.Attempt.AttemptNumber == 1 ? RuntimeHooks.Permit(e) : new(e, RuntimeHookStatus.Acknowledged, ExposureDecision.Deny)) };
        var result = await AccountingTests.Execute(AccountingTests.Agent(provider, hooks),
            Request(new(accountingPolicy: new(new(5, 4), 20, 20), retryPolicy: new(3))), candidate);
        Assert.Equal(AgentTerminationReason.Partial, result.Reason); Assert.Equal(1, provider.Effects);
        Assert.Equal(2, hooks.Exposures.Count); Assert.Equal(2, hooks.Settlements.Count);
        Assert.Equal(DispatchExposure.NotDispatched, result.Usage!.Attempts[1].Exposure);
        Assert.Equal(AccountingDisposition.Released, result.Usage.Accounting!.Attempts[1].Input.Disposition);
        Assert.Equal(3, result.Usage.Accounting.Input.MeasuredTokens);
    }
}

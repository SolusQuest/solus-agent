using System.Text.Json;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Execution;

public sealed class AccountingExchangeTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DurablePermissionSeesReservedNumbersAndGatesAllProviderEffects(bool candidate)
    {
        var entered = RuntimeFixture.Barrier<RuntimeExposure>(); var release = RuntimeFixture.Barrier<ExposureAcknowledgement?>();
        var provider = new ScriptedProvider([AccountingTests.Step(new(3, 2))]);
        var hooks = new RuntimeHooks { Before = (e, _) => { entered.SetResult(e); return new(release.Task); } };
        var pending = AccountingTests.Execute(AccountingTests.Agent(provider, hooks, strength: ExposureStrength.Durable),
            AccountingTests.Request(new(new(8, 5), 20, 20)), candidate);
        var exposure = await RuntimeFixture.Await(entered.Task);
        Assert.Equal(0, provider.Effects); Assert.Empty(hooks.Settlements);
        Assert.Equal(AccountingBalanceCoverage.Provisional, exposure.Accounting!.Input.Coverage);
        Assert.Equal(8, exposure.Accounting.Input.ReservedTokens); Assert.Equal(12, exposure.Accounting.Input.RemainingAllowance);
        release.SetResult(RuntimeHooks.Permit(new(exposure.Scope, exposure.Attempt, exposure.RequiredAcknowledgement,
            AccountingHostProbe.Copy(exposure.Accounting))));
        var result = await RuntimeFixture.Await(pending);
        Assert.Equal(AgentTerminationReason.Completed, result.Reason); Assert.Equal(1, provider.Effects);
        Assert.Equal(3, hooks.Settlements.Single().Accounting!.Input.MeasuredTokens);
        Assert.Equal(8, exposure.Accounting.Input.ReservedTokens); // Earlier immutable permission evidence remains provisional.
    }

    public static IEnumerable<object[]> Rejections()
    {
        foreach (var candidate in new[] { false, true })
        foreach (var mode in new[] { "deny", "missing", "unknown", "failed", "weak", "amount", "scope", "throw" }) yield return [candidate, mode];
    }
    [Theory, MemberData(nameof(Rejections))]
    public async Task InvalidPermissionReleasesOnlyAfterProvenNoDispatch(bool candidate, string mode)
    {
        var provider = new ScriptedProvider([AccountingTests.Step(new(3, 2))]);
        var hooks = new RuntimeHooks { Before = (e, _) =>
        {
            if (mode == "throw") throw new InvalidOperationException("HOOK_PRIVATE_CANARY");
            ExposureAcknowledgement? receipt = mode switch
            {
                "deny" => new(e, RuntimeHookStatus.Acknowledged, ExposureDecision.Deny),
                "missing" => null,
                "unknown" => new(e, RuntimeHookStatus.Unknown),
                "failed" => new(e, RuntimeHookStatus.Failed),
                "weak" => new(e, RuntimeHookStatus.Acknowledged, ExposureDecision.Permit, ExposureStrength.Volatile),
                "scope" => RuntimeHooks.Permit(new(new("wrong", "m"), e.Attempt, e.RequiredAcknowledgement, e.Accounting)),
                _ => RuntimeHooks.Permit(new(e.Scope, e.Attempt, e.RequiredAcknowledgement, ChangedReservation(e.Accounting!))),
            };
            return ValueTask.FromResult(receipt);
        } };
        var result = await AccountingTests.Execute(AccountingTests.Agent(provider, hooks, strength: ExposureStrength.Durable),
            AccountingTests.Request(new(new(8, 5), 20, 20)), candidate);
        Assert.Equal(0, provider.Effects); Assert.Equal(0, result.CompletedWorkUnits); Assert.Single(result.Usage!.Attempts);
        Assert.Equal(DispatchExposure.NotDispatched, result.Usage.Attempts[0].Exposure);
        Assert.Equal(AccountingDisposition.Released, result.Usage.Accounting!.Attempts[0].Input.Disposition);
        Assert.Equal(0, result.Usage.Accounting.Input.AccountedTokens); Assert.Equal(20, result.Usage.Accounting.Input.RemainingAllowance);
        Assert.False(hooks.Settlements.Single().ProviderInvoked); Assert.DoesNotContain("HOOK_PRIVATE_CANARY", JsonSerializer.Serialize(result));
    }

    private static RunAccountingSnapshot ChangedReservation(RunAccountingSnapshot source)
    {
        var p = source.Policy; var reservation = new UsageTokenAmounts(p.Reservation.InputTokens + 1, p.Reservation.OutputTokens);
        return new(source.ExecutionId, new(reservation, p.InputAllowance, p.OutputAllowance, p.UnknownUsage),
            source.Attempts.Select(e => new AttemptAccounting(e.ExecutionId, e.LogicalCallId, e.PhysicalAttemptId, e.AttemptNumber, reservation,
                new(e.Input.Disposition, reservation.InputTokens), e.Output)).ToArray());
    }
    private static RunAccountingSnapshot ChangedMeasurement(RunAccountingSnapshot source) => new(source.ExecutionId, source.Policy,
        source.Attempts.Select(e => new AttemptAccounting(e.ExecutionId, e.LogicalCallId, e.PhysicalAttemptId, e.AttemptNumber, e.Reservation,
            new(AccountingDisposition.Measured, e.Input.Amount + 1), e.Output)).ToArray());

    [Theory]
    [InlineData(false, "missing")] [InlineData(true, "missing")]
    [InlineData(false, "amount")] [InlineData(true, "amount")]
    [InlineData(false, "legacy")] [InlineData(true, "legacy")]
    [InlineData(false, "throw")] [InlineData(true, "throw")]
    [InlineData(false, "stop")] [InlineData(true, "stop")]
    public async Task ClosureCannotUndoSettlementOrReleaseNextDispatch(bool candidate, string mode)
    {
        var provider = new ScriptedProvider([AccountingTests.Step(new(3, 2), "a"), AccountingTests.Step(new(1, 1))]);
        var tool = new CustomTools.CounterCapability();
        var hooks = new RuntimeHooks { After = (s, _) =>
        {
            Assert.Equal(3, s.Accounting!.Input.MeasuredTokens); Assert.Equal(0, s.Accounting.Input.ReservedTokens);
            if (mode == "throw") throw new InvalidOperationException();
            return ValueTask.FromResult<SettlementAcknowledgement?>(mode switch
            {
                "missing" => null,
                "amount" => new(s.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue, ChangedMeasurement(s.Accounting)),
                "legacy" => new(s.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue),
                _ => new(s.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Stop, s.Accounting),
            });
        } };
        var result = await AccountingTests.Execute(AccountingTests.Agent(provider, hooks, tool),
            AccountingTests.Request(new(new(8, 5), 20, 20)), candidate);
        Assert.Equal(mode == "stop" ? AgentTerminationReason.Partial : AgentTerminationReason.Failed, result.Reason);
        Assert.Equal(1, provider.Effects); Assert.Equal(0, tool.Effects);
        Assert.Equal(3, result.Usage!.Accounting!.Input.MeasuredTokens); Assert.Equal(17, result.Usage.Accounting.Input.RemainingAllowance);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HeldSettlementIsAlreadyFinalAndOnlyValidReceiptPermitsNextTurn(bool candidate)
    {
        var entered = RuntimeFixture.Barrier<RuntimeSettlement>(); var release = RuntimeFixture.Barrier<SettlementAcknowledgement?>();
        var provider = new ScriptedProvider([AccountingTests.Step(new(3, 2), "a"), AccountingTests.Step(new(1, 1))]);
        var tool = new CustomTools.CounterCapability();
        var hooks = new RuntimeHooks { After = (s, _) =>
        {
            if (s.Accounting!.Attempts.Count != 1) return ValueTask.FromResult<SettlementAcknowledgement?>(RuntimeHooks.Continue(s));
            entered.SetResult(s); return new(release.Task);
        } };
        var pending = AccountingTests.Execute(AccountingTests.Agent(provider, hooks, tool), AccountingTests.Request(new(new(8, 5), 20, 20)), candidate);
        var settlement = await RuntimeFixture.Await(entered.Task);
        Assert.Equal(3, settlement.Accounting!.Input.MeasuredTokens); Assert.Equal(0, settlement.Accounting.Input.ReservedTokens);
        Assert.Equal(1, provider.Effects); Assert.Equal(0, tool.Effects);
        release.SetResult(RuntimeHooks.Continue(settlement));
        var result = await RuntimeFixture.Await(pending);
        Assert.Equal(AgentTerminationReason.Completed, result.Reason); Assert.Equal(2, provider.Effects); Assert.Equal(1, tool.Effects);
        Assert.Equal(4, result.Usage!.Accounting!.Input.MeasuredTokens); Assert.Equal(3, settlement.Accounting.Input.MeasuredTokens);
    }

    public static IEnumerable<object[]> CutCases()
    {
        foreach (var candidate in new[] { false, true })
        foreach (var cancel in new[] { false, true })
        foreach (var mode in Enum.GetValues<UnknownUsagePolicy>()) yield return [candidate, cancel, mode];
    }
    [Theory, MemberData(nameof(CutCases))]
    public async Task LostProviderAndLateClosurePreserveUnresolvedExposureAfterCut(bool candidate, bool cancel, UnknownUsagePolicy mode)
    {
        var clock = new ControlledTimeProvider(); using var cts = new CancellationTokenSource();
        var entered = RuntimeFixture.Barrier<ProviderRequest>(); var releaseProvider = RuntimeFixture.Barrier<ProviderExchangeResult>();
        var closureEntered = RuntimeFixture.Barrier<RuntimeSettlement>(); var releaseClosure = RuntimeFixture.Barrier<SettlementAcknowledgement?>();
        var provider = new InterfaceScriptedProvider(new("p", "m"), (r, _) => { entered.SetResult(r); return new(releaseProvider.Task); });
        var hooks = new RuntimeHooks { After = (s, _) => { closureEntered.SetResult(s); return new(releaseClosure.Task); } };
        var pending = AccountingTests.Execute(AccountingTests.Agent(provider, hooks, options: new(clock)),
            AccountingTests.Request(new(new(8, 5), 20, 20, mode)), candidate, cts.Token);
        var request = await RuntimeFixture.Await(entered.Task);
        if (cancel) cts.Cancel(); else clock.Advance(TimeSpan.FromSeconds(10));
        var settlement = await RuntimeFixture.Await(closureEntered.Task);
        Assert.Equal(mode == UnknownUsagePolicy.ConservativeCharge ? AccountingDisposition.ConservativeCharge : AccountingDisposition.Unresolved,
            settlement.Accounting!.Attempts[0].Input.Disposition);
        clock.Advance(TimeSpan.FromSeconds(2));
        var result = await RuntimeFixture.Await(pending);
        Assert.Equal(cancel ? AgentTerminationReason.Cancelled : AgentTerminationReason.ResourceLimit, result.Reason);
        Assert.Equal(DispatchExposure.Unknown, result.Usage!.Attempts[0].Exposure);
        Assert.Equal(8, result.Usage.Accounting!.Input.AccountedTokens); Assert.Equal(0, result.Usage.Accounting.Input.MeasuredTokens);
        var frozen = JsonSerializer.Serialize(result);
        Assert.Equal(ProviderError.ObservationClosed, Assert.Throws<ProviderContractException>(() => request.Observation.CaptureUsage(new(99, 99))).Error);
        releaseProvider.SetException(new InvalidOperationException("late private failure"));
        releaseClosure.SetResult(RuntimeHooks.Continue(settlement));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await releaseProvider.Task);
        await releaseClosure.Task;
        Assert.Equal(frozen, JsonSerializer.Serialize(result)); Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task SameIdOverlappedInvocationsUseIndependentRequestPoliciesAndLedgers()
    {
        var entered = new[] { RuntimeFixture.Barrier<ProviderRequest>(), RuntimeFixture.Barrier<ProviderRequest>() };
        var release = new[] { RuntimeFixture.Barrier<ProviderResponse>(), RuntimeFixture.Barrier<ProviderResponse>() };
        var provider = new ScriptedProvider([
            (r, o, _) => { o.CaptureUsage(new(3, 2)); entered[0].SetResult(r); return new(release[0].Task); },
            (r, o, _) => { o.CaptureUsage(new(7, null)); entered[1].SetResult(r); return new(release[1].Task); }]);
        var hooks = new RuntimeHooks(); var agent = AccountingTests.Agent(provider, hooks); var id = Guid.NewGuid();
        var first = AccountingTests.Execute(agent, AccountingTests.Request(new(new(8, 5), 20, 20), id: id), false);
        var a = await RuntimeFixture.Await(entered[0].Task);
        var second = AccountingTests.Execute(agent, AccountingTests.Request(new(new(12, 9), 40, 40, UnknownUsagePolicy.ConservativeCharge), id: id), true);
        var b = await RuntimeFixture.Await(entered[1].Task);
        Assert.NotEqual(a.Attempt.PhysicalAttemptId, b.Attempt.PhysicalAttemptId);
        Assert.Equal(8, hooks.Exposures[0].Accounting!.Input.ReservedTokens); Assert.Equal(12, hooks.Exposures[1].Accounting!.Input.ReservedTokens);
        release[1].SetResult(RuntimeFixture.Final(b)); var two = await RuntimeFixture.Await(second);
        release[0].SetResult(RuntimeFixture.Final(a)); var one = await RuntimeFixture.Await(first);
        Assert.Equal(17, one.Usage!.Accounting!.Input.RemainingAllowance); Assert.Equal(33, two.Usage!.Accounting!.Input.RemainingAllowance);
        Assert.Equal(0, one.Usage.Accounting.Output.ConservativeChargeTokens); Assert.Equal(9, two.Usage.Accounting.Output.ConservativeChargeTokens);
        Assert.Single(one.Usage.Attempts); Assert.Single(two.Usage.Attempts);
    }
}

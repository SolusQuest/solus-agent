using System.Collections.Concurrent;
using System.Text.Json;
using CustomTools;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ConsumerProbes.ScribeHost;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.ContractTests.Runtime.Tools;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Consumption;

public sealed class M3ScribeConformanceTests
{
    private const AgentCapability Required = AgentCapability.Cancellation | AgentCapability.WorkUnitLimit
        | AgentCapability.UsageReporting | AgentCapability.DispatchLimits | AgentCapability.ToolInvocationLimit | AgentCapability.UsageAccounting;

    [Fact]
    public void FreshRequestCarriesTrustedImmutableControlsAndRejectsInvalidRequirementsBeforeEffects()
    {
        var limits = Limits(retry: new(2));
        var host = Host(limits, []);
        var fresh = host.CreateFreshRequest();
        Assert.Equal(ContextExecutionIntent.Fresh, fresh.Intent); Assert.Null(fresh.Context);
        Assert.Same(limits, host.Control.UsageLimits); Assert.Same(limits, fresh.Request.UsageLimits);
        Assert.Same(fresh.Request, new ScribeCandidateStartup(host.Control.CandidateBounds).Adopt(fresh).Execution);
        Assert.Equal(Required, fresh.Request.RequiredCapabilities);
        Assert.Equal(ConsumptionFixture.Instructions, fresh.Request.Instructions);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScribeHostControl("i", new(8, TimeSpan.FromSeconds(10)), new(3, 1, 1), (AgentCapability)256));
        var manifest = new ScribeManifest(["alpha"]);
        var noPolicy = new ScribeBusinessHost(manifest, new(manifest, []),
            new("i", new(8, TimeSpan.FromSeconds(10)), new(3, 1, 1), AgentCapability.UsageAccounting), []);
        Assert.Throws<ArgumentException>(() => noPolicy.CreateFreshRequest());
        var incompatible = Host(new(maximumPhysicalDispatches: 2, inputTokenThreshold: 10,
            accountingPolicy: new(new(5, 4), 100, 100, UnknownUsagePolicy.ContinueUnknown)), [],
            required: Required | AgentCapability.UsageThresholds);
        Assert.Throws<ArgumentException>(() => incompatible.CreateFreshRequest());
        Assert.Empty(host.Exchanges); Assert.Empty(noPolicy.Exchanges); Assert.Empty(incompatible.Exchanges);
        Assert.Equal(0, host.ExternalEffects);
    }

    [Theory]
    [InlineData("corrected one")]
    [InlineData("corrected two")]
    public async Task MixedFreshProductionSeparatesToolWorkRetryAndCandidateAccounting(string correction)
    {
        var hooks = new RuntimeHooks();
        var startup = new ProductionStartup([
            (r, o, _) => ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("c", 7)]),
            (r, o, _) => Final(r, o, "alpha", "count " + ConsumptionFixture.CounterTotal(r, "c")),
            (r, o, _) =>
            {
                Assert.True(ScribeCandidateCorrection.TryParseRequestedFact(ConsumptionFixture.CorrectedData(r), out var fact));
                return Final(r, o, "alpha", fact);
            },
            (r, o, _) => ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Transform("t", "second fact")]),
            (r, o, _) => Final(r, o, "beta", ConsumptionFixture.TransformText(r, "t"))], hooks);
        var limits = Limits(tools: 2, logical: 5, physical: 5);
        var host = Host(limits, [new(false, correctionFact: correction), new(true), new(true, continuation: CandidateContinuation.End)], units: 5);
        var progress = new List<AgentProgress>();
        var result = await host.ExecuteFreshCandidatesAsync(startup.Agent, new InlineProgress(progress.Add));

        Assert.Equal(CandidateStopReason.Completed, result.StopReason);
        Assert.Equal(5, result.Outcome.CompletedWorkUnits); Assert.Equal(3, result.Receipts.Count);
        Assert.Equal(1, result.RepairsAdmitted); Assert.Equal(1, result.ContinuationsAdmitted); Assert.Equal(2, result.AcceptedCount);
        Assert.Equal(host.Exchanges[0].SubmissionId, host.Exchanges[1].RepairsSubmissionId);
        Assert.Equal(correction, host.Progress.AcceptedFacts[0].Text); Assert.Equal("SECOND FACT", host.Progress.AcceptedFacts[1].Text);
        Assert.True(host.Progress.IsComplete); Assert.Equal(0, host.ExternalEffects);
        Assert.Equal(1, startup.CounterCapability.Effects); Assert.Equal(1, startup.TransformCapability.Effects);
        Counts(result, 2); Assert.Equal(new[] { 1, 2, 3, 4, 5 }, progress.Select(p => p.CompletedWorkUnits));
        Assert.Equal(1, progress[0].Usage!.ToolInvocations!.Invoked); Assert.Single(progress[0].Usage!.Attempts);
        Assert.Equal(3, progress[0].Usage!.Accounting!.Input.MeasuredTokens);
        Assert.Equal(15, result.Outcome.Usage!.Accounting!.Input.MeasuredTokens);
        Assert.Equal(10, result.Outcome.Usage.Accounting.Output.MeasuredTokens);
        Assert.Same(limits.AccountingPolicy, result.Outcome.Usage.Accounting.Policy);
        Accounting(result, hooks, 5);
        Safe(result, progress, correction, "SECOND FACT", ConsumptionFixture.CredentialCanary);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CompleteBatchFitsOrStartsNoAffordablePrefix(int allowance)
    {
        var hooks = new RuntimeHooks();
        var startup = new ProductionStartup([
            (r, o, _) => ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("c", 2), ToolFixture.Transform("t", "fact")]),
            (r, o, _) => Final(r, o, "alpha", ConsumptionFixture.TransformText(r, "t") + ConsumptionFixture.CounterTotal(r, "c"))], hooks);
        var host = Host(Limits(tools: allowance), [new(true, continuation: CandidateContinuation.End)]);
        var result = await host.ExecuteFreshCandidatesAsync(startup.Agent);
        var admitted = allowance == 2;
        Assert.Equal(admitted ? CandidateStopReason.Completed : CandidateStopReason.RuntimeLimit, result.StopReason);
        Assert.Equal(admitted ? 2 : 1, startup.Provider.Effects);
        Assert.Equal(admitted ? 1 : 0, startup.CounterCapability.Effects);
        Assert.Equal(admitted ? 1 : 0, startup.TransformCapability.Effects);
        Assert.Equal(admitted ? 1 : 0, host.Exchanges.Count); Assert.Equal(admitted ? 1 : 0, result.AcceptedCount);
        Counts(result, admitted ? 2 : 0); Accounting(result, hooks, admitted ? 2 : 1);
        Assert.Equal(admitted ? "FACT2" : null, host.Progress.AcceptedFacts.FirstOrDefault()?.Text);
    }

    [Fact]
    public async Task CumulativeDenialRetainsAcceptedFactAndEntireLaterProviderObservation()
    {
        var hooks = new RuntimeHooks();
        var startup = new ProductionStartup([
            (r, o, _) => ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("first", 2)]),
            (r, o, _) => Final(r, o, "alpha", "accepted " + ConsumptionFixture.CounterTotal(r, "first")),
            (r, o, _) => ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("later", 9), ToolFixture.Transform("last", "denied")])], hooks);
        var host = Host(Limits(tools: 2), [new(true)]);
        var result = await host.ExecuteFreshCandidatesAsync(startup.Agent);
        Assert.Equal(CandidateStopReason.RuntimeLimit, result.StopReason);
        Assert.Equal(3, result.Outcome.CompletedWorkUnits); Assert.Equal(3, startup.Provider.Effects);
        Assert.Equal(1, startup.CounterCapability.Effects); Assert.Equal(0, startup.TransformCapability.Effects);
        Assert.Equal(1, result.ContinuationsAdmitted); Assert.Equal(0, result.RepairsAdmitted);
        RetainedAlpha(host, result); Counts(result, 1); Accounting(result, hooks, 3);
        Assert.Equal(9, result.Outcome.Usage!.InputTokens.ObservedTokens);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("retry-exhausted")]
    [InlineData("dispatch-exhausted")]
    [InlineData("permanent")]
    public async Task ProviderRetryRetainsAccountingAndHistoryWithoutReplayingToolOrHost(string mode)
    {
        var requests = new List<ProviderRequest>(); var hooks = new RuntimeHooks();
        var startup = new ProductionStartup([
            (r, o, _) => { requests.Add(r); return ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("once", 7)], ConsumptionFixture.TurnReplay(1)); },
            (r, o, _) => { requests.Add(r); return Final(r, o, "alpha", "count " + ConsumptionFixture.CounterTotal(r, "once"), ConsumptionFixture.TurnReplay(2)); },
            (r, o, _) => { requests.Add(r); o.CaptureUsage(new(3, 2)); throw new ProviderFailureException(mode == "permanent" ? null : new(ProviderRetryKind.Transient)); },
            (r, o, _) => { requests.Add(r); return Final(r, o, "beta", "retained " + ConsumptionFixture.CounterTotal(r, "once")); }], hooks,
            new RuntimeOptions(requireContinuation: true));
        // Every accepted response must provide continuation under this profile, including the successful retry.
        var host = Host(Limits(tools: 1, logical: 3, physical: mode == "dispatch-exhausted" ? 3 : 4,
            retry: new(mode == "retry-exhausted" ? 1 : 2)), [new(true), new(true, continuation: CandidateContinuation.End)]);
        var result = await host.ExecuteFreshCandidatesAsync(startup.Agent);
        var success = mode == "success";
        Assert.Equal(success ? CandidateStopReason.Completed : mode == "dispatch-exhausted" ? CandidateStopReason.RuntimeLimit : CandidateStopReason.ProductionFailed, result.StopReason);
        Assert.Equal(success ? 4 : 3, requests.Count); Assert.Equal(success ? 3 : 2, result.Outcome.CompletedWorkUnits);
        Assert.Equal(1, startup.CounterCapability.Effects); Assert.Equal(0, startup.TransformCapability.Effects); Counts(result, 1);
        Assert.Equal(success ? 2 : 1, host.Exchanges.Count); Assert.Equal(success ? 2 : 1, result.AcceptedCount);
        Assert.Equal(1, result.ContinuationsAdmitted); Assert.Equal(0, result.RepairsAdmitted);
        Assert.Equal(3, requests.Select(r => r.Attempt.LogicalCallId).Distinct().Count());
        if (success)
        {
            var failed = requests[2]; var retry = requests[3];
            Assert.Equal(failed.Attempt.LogicalCallId, retry.Attempt.LogicalCallId);
            Assert.NotEqual(failed.Attempt.PhysicalAttemptId, retry.Attempt.PhysicalAttemptId);
            Assert.Equal(1, failed.Attempt.AttemptNumber); Assert.Equal(2, retry.Attempt.AttemptNumber);
            Assert.Equal(failed.Inputs, retry.Inputs); Assert.Same(failed.Continuation, retry.Continuation);
            Assert.Equal(ConsumptionFixture.TurnReplay(2), ConsumptionFixture.Replay(retry));
            Assert.Single(retry.Inputs, i => i.ToolResult is not null && i.ToolResult.Call.CallId == "once");
            Assert.Equal("retained 7", host.Progress.AcceptedFacts[1].Text);
        }
        else RetainedAlpha(host, result);
        Accounting(result, hooks, requests.Count);
        Assert.Equal(requests.Count * 3, result.Outcome.Usage!.Accounting!.Input.MeasuredTokens);
    }

    [Theory]
    [InlineData("logical")]
    [InlineData("physical")]
    [InlineData("input")]
    [InlineData("output")]
    [InlineData("reservation")]
    [InlineData("count-before-unknown")]
    [InlineData("failure")]
    public async Task LaterStopPreservesBusinessAcceptanceAndAcknowledgement(string stop)
    {
        var hooks = new RuntimeHooks();
        var usage = stop == "count-before-unknown" ? new UsageObservation(null, 2) : new(3, 2);
        var startup = new ProductionStartup([
            (r, o, _) => { o.CaptureUsage(usage); return ValueTask.FromResult(Response(r, ProviderFinish.Final, ScribeCandidatePayload.Format("alpha", "retained fact"))); },
            ScriptedProvider.Failure()], hooks);
        var policy = new AgentAccountingPolicy(new(5, 4), stop == "reservation" ? 7 : 100, 100);
        var limits = new AgentUsageLimits(maximumLogicalCalls: stop == "logical" ? 1 : 4,
            maximumPhysicalDispatches: stop is "physical" or "count-before-unknown" ? 1 : 4,
            inputTokenThreshold: stop == "input" ? 3 : stop == "count-before-unknown" ? 10 : null,
            outputTokenThreshold: stop == "output" ? 2 : null, maximumToolInvocations: 1, accountingPolicy: policy);
        var host = Host(limits, [new(true)]);
        var result = await host.ExecuteFreshCandidatesAsync(startup.Agent);
        Assert.Equal(stop == "failure" ? CandidateStopReason.ProductionFailed : CandidateStopReason.RuntimeLimit, result.StopReason);
        Assert.Equal(stop == "failure" ? AgentTerminationReason.Failed : AgentTerminationReason.ResourceLimit, result.Outcome.Reason);
        Assert.Equal(stop == "failure" ? 2 : 1, startup.Provider.Effects);
        Assert.Equal(stop == "failure" ? 1 : 0, result.ContinuationsAdmitted);
        Assert.Equal(1, result.Outcome.CompletedWorkUnits); RetainedAlpha(host, result); Counts(result, 0);
        Accounting(result, hooks, stop == "failure" ? 2 : 1);
        if (stop == "count-before-unknown")
        {
            Assert.Null(result.Outcome.Usage!.InputTokens.ObservedTokens);
            Assert.Equal(AccountingDisposition.Unresolved, result.Outcome.Usage.Accounting!.Attempts[0].Input.Disposition);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptEndCompletesAtReachedTokenThreshold(bool output)
    {
        var startup = new ProductionStartup([(r, o, _) => Final(r, o, "alpha", "final fact")]);
        var host = Host(new(maximumPhysicalDispatches: 1, inputTokenThreshold: output ? null : 3, outputTokenThreshold: output ? 2 : null,
            maximumToolInvocations: 1, accountingPolicy: new(new(5, 4), 10, 10)), [new(true, continuation: CandidateContinuation.End)],
            required: Required | AgentCapability.UsageThresholds);
        var result = await host.ExecuteFreshCandidatesAsync(startup.Agent);
        Assert.Equal(CandidateStopReason.Completed, result.StopReason); RetainedAlpha(host, result);
        Assert.Equal(1, startup.Provider.Effects); Assert.Equal(0, result.ContinuationsAdmitted);
    }

    public static IEnumerable<object[]> MissingModes()
    {
        foreach (var policy in Enum.GetValues<UnknownUsagePolicy>())
        foreach (var output in new[] { false, true })
        foreach (var finish in new[] { false, true }) yield return [policy, output, finish];
    }

    [Theory, MemberData(nameof(MissingModes))]
    public async Task MissingUsageStaysUnknownAndContinuationRemainsExplicitlyBounded(UnknownUsagePolicy mode, bool output, bool finish)
    {
        var hooks = new RuntimeHooks(); var usage = output ? new UsageObservation(2, null) : new(null, 2);
        var startup = new ProductionStartup([
            (r, o, _) => { o.CaptureUsage(usage); return ValueTask.FromResult(Response(r, ProviderFinish.Final, ScribeCandidatePayload.Format("alpha", "known business fact"))); },
            (r, o, _) =>
            {
                if (finish) return Final(r, o, "beta", "next fact");
                o.CaptureUsage(usage); return ValueTask.FromResult(Response(r, ProviderFinish.ToolCalls, calls: [ToolFixture.Counter()]));
            },
            (r, o, _) => Final(r, o, "beta", "must not dispatch")], hooks);
        var limits = new AgentUsageLimits(maximumPhysicalDispatches: 2, inputTokenThreshold: output ? null : 10,
            outputTokenThreshold: output ? 10 : null, maximumToolInvocations: 1,
            accountingPolicy: new(new(5, 4), 100, 100, mode));
        var host = Host(limits, [new(true), new(true, continuation: CandidateContinuation.End)]);
        var result = await host.ExecuteFreshCandidatesAsync(startup.Agent);
        var stopped = mode == UnknownUsagePolicy.Stop;
        Assert.Equal(stopped ? CandidateStopReason.UsageAccountingUnavailable : finish ? CandidateStopReason.Completed : CandidateStopReason.RuntimeLimit, result.StopReason);
        Assert.Equal(stopped ? 1 : 2, startup.Provider.Effects);
        Assert.Equal(stopped || finish ? 0 : 1, startup.CounterCapability.Effects);
        Assert.Equal(stopped ? 0 : 1, result.ContinuationsAdmitted);
        Assert.True(result.Receipts[0].IsAccepted); Assert.Equal("alpha", host.Progress.AcceptedFacts[0].Member);
        var raw = result.Outcome.Usage!;
        var axis = output ? raw.Accounting!.Output : raw.Accounting!.Input;
        var first = output ? raw.Accounting.Attempts[0].Output : raw.Accounting.Attempts[0].Input;
        Assert.Equal(mode == UnknownUsagePolicy.ConservativeCharge ? AccountingDisposition.ConservativeCharge : AccountingDisposition.Unresolved, first.Disposition);
        Assert.Equal(output ? 4 : 5, first.Amount);
        Assert.Null(output ? raw.Attempts[0].Usage.OutputTokens : raw.Attempts[0].Usage.InputTokens);
        Assert.NotEqual(TokenObservationCoverage.Complete, output ? raw.OutputTokens.Coverage : raw.InputTokens.Coverage);
        Assert.Equal(mode == UnknownUsagePolicy.ConservativeCharge ? AccountingBalanceCoverage.Provisional : AccountingBalanceCoverage.Unknown, axis.Coverage);
        Assert.Equal(mode == UnknownUsagePolicy.ConservativeCharge ? 0 : first.Amount * (stopped || finish ? 1 : 2), axis.UnresolvedTokens);
        Assert.Equal(mode == UnknownUsagePolicy.ConservativeCharge ? first.Amount * (finish ? 1 : 2) : 0, axis.ConservativeChargeTokens);
        Counts(result, stopped || finish ? 0 : 1); Accounting(result, hooks, stopped ? 1 : 2);
        Assert.Equal(0, host.ExternalEffects);
    }


    [Fact]
    public async Task SequentialFreshRunsOnSameHostResetAllowanceAndLedgerWhileRetainingOnlyBusinessFacts()
    {
        var requests = new List<ProviderRequest>(); var hooks = new RuntimeHooks();
        var startup = new ProductionStartup([
            (r, o, _) => { requests.Add(r); return ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("first", 1)]); },
            (r, o, _) => { requests.Add(r); return Final(r, o, "alpha", "accepted " + ConsumptionFixture.CounterTotal(r, "first")); },
            (r, o, _) => { requests.Add(r); return ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("second", 1)]); },
            (r, o, _) => { requests.Add(r); return Final(r, o, "beta", "remaining " + ConsumptionFixture.CounterTotal(r, "second")); }], hooks);
        var host = Host(Limits(tools: 1, logical: 2, physical: 2), [new(true), new(true, continuation: CandidateContinuation.End)], units: 2);
        var observations = new List<AgentProgress>();
        var first = await host.ExecuteFreshCandidatesAsync(startup.Agent, new InlineProgress(observations.Add));
        var firstId = host.CurrentExecutionId;
        var frozen = JsonSerializer.Serialize(first) + JsonSerializer.Serialize(observations);
        var second = await host.ExecuteFreshCandidatesAsync(startup.Agent);
        Assert.Equal(CandidateStopReason.WorkUnitLimit, first.StopReason); Assert.Equal(CandidateStopReason.Completed, second.StopReason);
        Assert.NotEqual(firstId, host.CurrentExecutionId); Assert.Equal(firstId, requests[0].Attempt.ExecutionId);
        Assert.Equal(host.CurrentExecutionId, requests[2].Attempt.ExecutionId);
        Assert.Contains(requests[2].Inputs, i => i.Text == "accepted fact: alpha = accepted 1");
        Assert.Contains(requests[2].Inputs, i => i.Text == "unresolved member: beta");
        Assert.DoesNotContain(requests[2].Inputs, i => i.Text == "unresolved member: alpha" || i.ToolResult is not null);
        Assert.Equal(4, requests.Select(r => r.Attempt.LogicalCallId).Distinct().Count());
        Assert.Equal(4, requests.Select(r => r.Attempt.PhysicalAttemptId).Distinct().Count());
        Assert.NotEqual(first.Receipts[0].SubmissionId, second.Receipts[0].SubmissionId);
        foreach (var run in new[] { first, second })
        {
            Counts(run, 1); Accounting(run, hooks, 2);
            Assert.Equal(6, run.Outcome.Usage!.Accounting!.Input.MeasuredTokens);
            Assert.Equal(4, run.Outcome.Usage.Accounting.Output.MeasuredTokens); Assert.Equal(1, run.AcceptedCount);
        }
        Assert.Equal(frozen, JsonSerializer.Serialize(first) + JsonSerializer.Serialize(observations));
        Assert.Equal(2, startup.CounterCapability.Effects); Assert.True(host.Progress.IsComplete); Assert.Equal(0, host.ExternalEffects);
    }

    [Fact]
    public async Task OverlappingFreshHostsShareFactoryButNeverShareAllowanceHistoryOrLedger()
    {
        var entered = RuntimeFixture.Barrier(); var release = RuntimeFixture.Barrier();
        var requests = new ConcurrentDictionary<Guid, ProviderRequest>(); var hooks = new RuntimeHooks();
        var startup = new ProductionStartup([Held, Held,
            (r, o, _) => Final(r, o, "alpha", "own " + ConsumptionFixture.CounterTotal(r, "same")),
            (r, o, _) => Final(r, o, "alpha", "own " + ConsumptionFixture.CounterTotal(r, "same"))], hooks);
        var left = Host(Limits(tools: 1, logical: 2, physical: 2), [new(true, continuation: CandidateContinuation.End)]);
        var right = Host(Limits(tools: 1, logical: 2, physical: 2), [new(true, continuation: CandidateContinuation.End)]);
        var a = left.ExecuteFreshCandidatesAsync(startup.Agent).AsTask();
        var b = right.ExecuteFreshCandidatesAsync(startup.Agent).AsTask();
        await RuntimeFixture.Await(entered.Task);
        Assert.False(a.IsCompleted); Assert.False(b.IsCompleted); Assert.Equal(2, requests.Count);
        Assert.Equal(0, startup.CounterCapability.Effects); Assert.Empty(left.Exchanges); Assert.Empty(right.Exchanges);
        release.SetResult();
        var results = await RuntimeFixture.Await(Task.WhenAll(a, b));
        Assert.NotEqual(left.CurrentExecutionId, right.CurrentExecutionId);
        var attempts = results.SelectMany(r => r.Outcome.Usage!.Attempts).ToArray();
        Assert.Equal(4, attempts.Select(r => r.LogicalCallId).Distinct().Count());
        Assert.Equal(4, attempts.Select(r => r.PhysicalAttemptId).Distinct().Count());
        Assert.NotEqual(results[0].Receipts[0].SubmissionId, results[1].Receipts[0].SubmissionId);
        for (var i = 0; i < results.Length; i++)
        {
            var host = i == 0 ? left : right; var run = results[i];
            Assert.Equal(CandidateStopReason.Completed, run.StopReason); Counts(run, 1); Accounting(run, hooks, 2);
            Assert.All(run.Outcome.Usage!.Accounting!.Attempts, e => Assert.Equal(host.CurrentExecutionId, e.ExecutionId));
            Assert.Equal(6, run.Outcome.Usage.Accounting.Input.MeasuredTokens); Assert.Equal(4, run.Outcome.Usage.Accounting.Output.MeasuredTokens);
            Assert.Single(host.Progress.AcceptedFacts); Assert.Single(host.Exchanges); Assert.Equal(0, host.ExternalEffects);
        }
        Assert.Equal(new[] { "own 1", "own 2" }, new[] { left.Progress.AcceptedFacts[0].Text, right.Progress.AcceptedFacts[0].Text }.Order());
        Assert.Equal(2, startup.CounterCapability.Effects); Assert.Equal(4, startup.Provider.Effects);

        async ValueTask<ProviderResponse> Held(ProviderRequest r, ProviderObservation o, CancellationToken _)
        {
            Assert.True(requests.TryAdd(r.Attempt.ExecutionId, r));
            if (requests.Count == 2) entered.TrySetResult();
            await release.Task;
            return await ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("same", 1)]);
        }
    }

    [Theory]
    [InlineData("provider", true)] [InlineData("provider", false)]
    [InlineData("tool", true)] [InlineData("tool", false)]
    public async Task CutDuringLaterProviderOrToolRetainsAcknowledgedProgressAndFreezesLateCompletion(string stage, bool cancel)
    {
        var clock = new ControlledTimeProvider(); using var cancellation = new CancellationTokenSource();
        var entered = RuntimeFixture.Barrier(); var release = RuntimeFixture.Barrier<ToolOutput>();
        Task<ProviderResponse>? providerOperation = null; var hooks = new RuntimeHooks();
        var startup = new ProductionStartup([
            (r, o, _) => Final(r, o, "alpha", "retained fact"),
            (r, o, _) =>
            {
                if (stage == "tool") return ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Transform("held"), ToolFixture.Counter("never")]);
                o.CaptureUsage(new()); providerOperation = HeldProvider(r); return new(providerOperation);
            }], hooks, new RuntimeOptions(clock), transformEffect: (call, cap, token) =>
            { cap.Upper("effect", token); entered.SetResult(); return new(release.Task); });
        var observed = new ObservedScribeTool(startup.Transform);
        var agent = (ICandidateAgent)RuntimeAgentFactory.Create(new(startup.Provider,
            [startup.Bindings[0], new(observed, startup.TransformCapability)], hooks), new RuntimeOptions(clock));
        var host = Host(Limits(tools: 2), [new(true), new(true, continuation: CandidateContinuation.End)]);
        var progress = new List<AgentProgress>();
        var pending = host.ExecuteFreshCandidatesAsync(agent, new InlineProgress(progress.Add), cancellation.Token).AsTask();
        await RuntimeFixture.Await(entered.Task);
        if (cancel) cancellation.Cancel(); else clock.Advance(TimeSpan.FromSeconds(11));
        var result = await RuntimeFixture.Await(pending);
        Task operation = stage == "tool" ? observed.Invocation! : providerOperation!;
        Assert.False(operation.IsCompleted); Assert.False(release.Task.IsCompleted);
        Assert.Equal(cancel ? CandidateStopReason.Cancelled : CandidateStopReason.DurationLimit, result.StopReason);
        RetainedAlpha(host, result); Counts(result, stage == "tool" ? 1 : 0, stage == "tool" ? 1 : 0);
        Assert.Equal(0, startup.CounterCapability.Effects); Assert.Equal(stage == "tool" ? 1 : 0, startup.TransformCapability.Effects);
        Assert.Equal(2, startup.Provider.Effects); Assert.Equal(2, result.Outcome.Usage!.Attempts.Count);
        var ledger = result.Outcome.Usage.Accounting!;
        Assert.Equal(0, ledger.Input.ReservedTokens); Assert.Equal(stage == "provider" ? 5 : 0, ledger.Input.UnresolvedTokens);
        Assert.Equal(stage == "provider" ? 3 : 6, ledger.Input.MeasuredTokens);
        if (stage == "provider") Assert.Null(result.Outcome.Usage.Attempts[1].Usage.InputTokens);
        else Accounting(result, hooks, 2);
        var frozen = JsonSerializer.Serialize(result) + JsonSerializer.Serialize(progress) + JsonSerializer.Serialize(host.Progress);
        release.SetResult(ToolOutput.Success(ToolFixture.Transform("held"), "{\"text\":\"LATE_TOOL_CANARY\"}"));
        await RuntimeFixture.Await(operation);
        Assert.Equal(frozen, JsonSerializer.Serialize(result) + JsonSerializer.Serialize(progress) + JsonSerializer.Serialize(host.Progress));
        Assert.Equal(2, startup.Provider.Effects); Assert.Equal(0, startup.CounterCapability.Effects);
        Assert.Equal(stage == "tool" ? 1 : 0, startup.TransformCapability.Effects); Assert.Single(host.Exchanges);
        Safe(result, progress, "LATE_TOOL_CANARY", "LATE_PROVIDER_CANARY", "retained fact");

        async Task<ProviderResponse> HeldProvider(ProviderRequest r)
        {
            entered.SetResult(); await release.Task;
            return Response(r, ProviderFinish.Final, ScribeCandidatePayload.Format("beta", "LATE_PROVIDER_CANARY"));
        }
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task CutDuringHostAcknowledgementKeepsCommitDistinctAndDoesNotReplayLateDelivery(bool cancel)
    {
        var clock = new ControlledTimeProvider(); using var cancellation = new CancellationTokenSource(); var hooks = new RuntimeHooks();
        var startup = new ProductionStartup([
            (r, o, _) => ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("once", 1)]),
            (r, o, _) => Final(r, o, "alpha", "committed " + ConsumptionFixture.CounterTotal(r, "once"))], hooks, new RuntimeOptions(clock));
        var host = Host(Limits(tools: 1), [new(true, ScribeDelivery.Held)]);
        var channel = new WitnessingChannel(host); var progress = new List<AgentProgress>();
        var request = new ScribeCandidateStartup(host.Control.CandidateBounds).Adopt(host.CreateFreshRequest());
        var pending = startup.Agent.ExecuteCandidatesAsync(request, channel, new InlineProgress(progress.Add), cancellation.Token).AsTask();
        var delivery = await RuntimeFixture.Await(channel.DeliveryPublished); await RuntimeFixture.Await(host.DeliveryHeld);
        if (cancel) cancellation.Cancel(); else clock.Advance(TimeSpan.FromSeconds(11));
        var result = await RuntimeFixture.Await(pending);
        Assert.False(delivery.IsCompleted); Assert.Equal(cancel ? CandidateStopReason.Cancelled : CandidateStopReason.DurationLimit, result.StopReason);
        Assert.Equal(CandidateAcknowledgement.Unknown, Assert.Single(result.Receipts).Acknowledgement); Assert.Equal(0, result.AcceptedCount);
        Assert.Equal("committed 1", Assert.Single(host.Progress.AcceptedFacts).Text); Assert.Single(host.Exchanges);
        Counts(result, 1); Accounting(result, hooks, 2);
        var frozen = JsonSerializer.Serialize(result) + JsonSerializer.Serialize(progress) + JsonSerializer.Serialize(host.Progress);
        host.ReleaseHeldDelivery(); await RuntimeFixture.Await(delivery);
        Assert.Equal(frozen, JsonSerializer.Serialize(result) + JsonSerializer.Serialize(progress) + JsonSerializer.Serialize(host.Progress));
        Assert.Equal(1, startup.CounterCapability.Effects); Assert.Equal(2, startup.Provider.Effects); Assert.Single(host.Exchanges);
        Assert.Equal(0, host.ExternalEffects); Safe(result, progress, "committed 1");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AcceptedToolBatchStillExecutesAtTokenThresholdButNoLaterModelCallStarts(bool output)
    {
        var startup = new ProductionStartup([(r, o, _) => ConsumptionFixture.ToolCalls(r, o,
            [ToolFixture.Counter("c", 1), ToolFixture.Transform("t", "fact")])]);
        var host = Host(new(maximumPhysicalDispatches: 3, inputTokenThreshold: output ? null : 3, outputTokenThreshold: output ? 2 : null,
            maximumToolInvocations: 2, accountingPolicy: new(new(5, 4), 100, 100)), []);
        var result = await host.ExecuteFreshCandidatesAsync(startup.Agent);
        Assert.Equal(CandidateStopReason.RuntimeLimit, result.StopReason); Counts(result, 2);
        Assert.Equal(1, startup.Provider.Effects); Assert.Equal(1, startup.CounterCapability.Effects); Assert.Equal(1, startup.TransformCapability.Effects);
        Assert.Empty(host.Exchanges); Assert.Empty(host.Progress.AcceptedFacts); Assert.Equal(1, result.Outcome.CompletedWorkUnits);
    }

    private sealed class ObservedScribeTool(IFunctionTool inner) : IFunctionTool
    {
        internal Task<ToolResult>? Invocation { get; private set; }
        public ToolDescriptor Descriptor => inner.Descriptor;
        public ToolPreparation Prepare(ToolCall call) => inner.Prepare(call);
        public ToolError ValidateInvocation(PreparedToolInvocation prepared, ToolCall call, IToolCapability? capability) =>
            inner.ValidateInvocation(prepared, call, capability);
        public ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation prepared, ToolCall call, IToolCapability? capability, CancellationToken token = default)
        {
            Invocation = inner.InvokeAsync(prepared, call, capability, token).AsTask();
            return new(Invocation);
        }
    }

    private static AgentUsageLimits Limits(int tools = 2, int logical = 8, int physical = 8, AgentRetryPolicy? retry = null) =>
        new(maximumLogicalCalls: logical, maximumPhysicalDispatches: physical, maximumToolInvocations: tools,
            accountingPolicy: new(new(5, 4), 100, 100), retryPolicy: retry);

    private static ScribeBusinessHost Host(AgentUsageLimits limits, IReadOnlyList<ScribeExchangePlan> plan, int units = 8,
        AgentCapability required = Required, ScribeManifest? manifest = null, ScribeProgress? initial = null)
    {
        manifest ??= new(["alpha", "beta"]);
        return new(manifest, initial ?? new(manifest, []),
            new(ConsumptionFixture.Instructions, new(units, TimeSpan.FromSeconds(10)), new(4, 1, 2), required, limits), plan);
    }

    private static ProviderResponse Response(ProviderRequest r, ProviderFinish finish, string? text = null, IReadOnlyList<ToolCall>? calls = null,
        string? replay = null) => new(r.Scope, r.Attempt, finish, text, calls ?? [],
            r.RequiredCapabilities.HasFlag(ProviderCapabilities.Continuation)
                ? ConsumptionFixture.Continuation(r, replay ?? ConsumptionFixture.TurnReplay(r.Attempt.AttemptNumber)) : null);

    private static ValueTask<ProviderResponse> Final(ProviderRequest r, ProviderObservation o, string member, string fact, string? replay = null)
    {
        o.CaptureUsage(new(3, 2));
        return ValueTask.FromResult(Response(r, ProviderFinish.Final, ScribeCandidatePayload.Format(member, fact), replay: replay));
    }

    private static void Counts(CandidateExecutionResult result, int invoked, int released = 0)
    {
        var tools = Assert.IsType<ToolInvocationUsage>(result.Outcome.Usage!.ToolInvocations);
        Assert.Equal(invoked, tools.Invoked); Assert.Equal(0, tools.ReservedUnstarted); Assert.Equal(released, tools.ReleasedUnstarted);
    }

    private static void RetainedAlpha(ScribeBusinessHost host, CandidateExecutionResult result)
    {
        Assert.Single(host.Exchanges); Assert.Single(host.Progress.AcceptedFacts); Assert.Equal("alpha", host.Progress.AcceptedFacts[0].Member);
        Assert.Single(result.Receipts); Assert.True(result.Receipts[0].IsAccepted); Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(host.Exchanges[0].SubmissionId, result.Receipts[0].SubmissionId); Assert.Equal(0, host.ExternalEffects);
    }

    private static void Accounting(CandidateExecutionResult result, RuntimeHooks hooks, int attempts)
    {
        var usage = result.Outcome.Usage!; var ledger = Assert.IsType<RunAccountingSnapshot>(usage.Accounting);
        var executionId = usage.Attempts[0].ExecutionId;
        var exposures = hooks.Exposures.Where(e => e.Attempt.ExecutionId == executionId).ToArray();
        var settlements = hooks.Settlements.Where(s => s.Exposure.Attempt.ExecutionId == executionId).ToArray();
        Assert.Equal(attempts, usage.Attempts.Count); Assert.Equal(attempts, ledger.Attempts.Count);
        Assert.Equal(attempts, exposures.Length); Assert.Equal(attempts, settlements.Length);
        Assert.Equal(0, ledger.Input.ReservedTokens); Assert.Equal(0, ledger.Output.ReservedTokens);
        for (var i = 0; i < attempts; i++)
        {
            var exposure = exposures[i]; var settled = settlements[i]; var observed = usage.Attempts[i];
            Assert.True(exposure.Attempt.Matches(settled.Exposure.Attempt));
            Assert.Equal(observed.PhysicalAttemptId, exposure.Attempt.PhysicalAttemptId);
            Assert.Equal(AccountingDisposition.Reserved, exposure.Accounting!.Attempts.Last().Input.Disposition);
            Assert.Equal(5, exposure.Accounting.Attempts.Last().Input.Amount);
            Assert.True(ledger.Attempts[i].MatchesObservation(observed, ledger.Policy.UnknownUsage));
            Assert.True(ledger.Attempts[i].Matches(settled.Accounting!.Attempts[i]));
        }
        Assert.True(ledger.Matches(settlements[^1].Accounting!));
    }

    private static void Safe(CandidateExecutionResult result, IReadOnlyList<AgentProgress> progress, params string[] canaries)
    {
        var ordinary = JsonSerializer.Serialize(result) + JsonSerializer.Serialize(progress);
        foreach (var canary in canaries) Assert.DoesNotContain(canary, ordinary);
    }
}

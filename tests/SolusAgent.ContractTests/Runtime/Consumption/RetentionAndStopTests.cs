using AprHost;
using CustomTools;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.ConsumerProbes.ScribeHost;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.ContractTests.Runtime.Tools;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Consumption;

public sealed class RetentionAndStopTests
{
    private sealed record StopRun(CandidateExecutionResult Result, int Committed, int AcceptedByHost, int Exchanges,
        int CounterEffects, int TransformEffects, int ProviderEffects, List<AgentProgress> Progress);

    [Theory]
    [InlineData("apr", "provider-failure", CandidateStopReason.ProductionFailed, 2, 1, 3)]
    [InlineData("apr", "work-limit", CandidateStopReason.WorkUnitLimit, 2, 1, 2)]
    [InlineData("apr", "cancel", CandidateStopReason.Cancelled, 2, 1, 3)]
    [InlineData("apr", "unknown", CandidateStopReason.UnknownAcknowledgement, 3, 2, 3)]
    [InlineData("apr", "host-end", CandidateStopReason.HostEnded, 3, 2, 3)]
    [InlineData("scribe", "provider-failure", CandidateStopReason.ProductionFailed, 2, 1, 3)]
    [InlineData("scribe", "work-limit", CandidateStopReason.WorkUnitLimit, 2, 1, 2)]
    [InlineData("scribe", "cancel", CandidateStopReason.Cancelled, 2, 1, 3)]
    [InlineData("scribe", "unknown", CandidateStopReason.UnknownAcknowledgement, 3, 2, 3)]
    [InlineData("scribe", "host-end", CandidateStopReason.HostEnded, 3, 2, 3)]
    public async Task EarlierHostAcceptanceAndUsageSurvivePostAcceptanceStops(string hostKind, string mode,
        CandidateStopReason stop, int units, int receipts, int attempts)
    {
        using var cancellation = new CancellationTokenSource();
        var steps = new List<Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>>>
        {
            (r, o, _) => ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("c1", 5)], ConsumptionFixture.TurnReplay(1)),
            (r, o, _) => ConsumptionFixture.Final(r, o, hostKind == "apr"
                ? "apr-item:alpha:" + ConsumptionFixture.CounterTotal(r, "c1")
                : ScribeCandidatePayload.Format("alpha", "FACT ALPHA"), ConsumptionFixture.TurnReplay(2)),
        };
        if (mode != "work-limit")
        {
            steps.Add(mode switch
            {
                "provider-failure" => (r, o, _) => { o.CaptureUsage(new(9, null)); throw new InvalidOperationException("PROVIDER_EXCEPTION_CANARY"); },
                "cancel" => (r, o, _) =>
                {
                    o.CaptureUsage(new(3, 2));
                    var response = new ProviderResponse(r.Scope, r.Attempt, ProviderFinish.Final,
                        hostKind == "apr" ? "apr-item:beta:1" : ScribeCandidatePayload.Format("beta", "FACT BETA"), []);
                    cancellation.Cancel();
                    return ValueTask.FromResult(response);
                },
                _ => (r, o, _) => ConsumptionFixture.Final(r, o, hostKind == "apr"
                    ? "apr-item:beta:1" : ScribeCandidatePayload.Format("beta", "FACT BETA"), ConsumptionFixture.TurnReplay(3)),
            });
        }

        var startup = new ProductionStartup(steps, options: new RuntimeOptions(requireContinuation: true));
        var progress = new List<AgentProgress>();
        var tail = mode switch
        {
            "unknown" => new ExchangePolicy(true, CandidateContinuation.Continue, ExchangeDelivery.Unknown),
            "host-end" => new ExchangePolicy(false, CandidateContinuation.End),
            _ => new ExchangePolicy(true, CandidateContinuation.Continue),
        };
        StopRun run;
        if (hostKind == "apr")
        {
            var host = new AprBusinessHost(startup.Agent, startup.Agent);
            var channel = new ExchangeChannel([new ExchangePolicy(true, CandidateContinuation.Continue), tail]);
            var result = await host.ExecuteCandidatesAsync(
                ConsumptionFixture.Candidates(ConsumptionFixture.Request(mode == "work-limit" ? 2 : 8),
                    submissions: 4, repairs: 1, continuations: 1), channel, new InlineProgress(progress.Add), cancellation.Token);
            run = new StopRun(result, host.Acceptance.AcceptedCount, host.Acceptance.AcceptedCount, channel.Submissions.Count,
                startup.CounterCapability.Effects, startup.TransformCapability.Effects, startup.Provider.Effects, progress);
        }
        else
        {
            var manifest = new ScribeManifest(["alpha", "beta"]);
            var control = new ScribeHostControl(ConsumptionFixture.Instructions,
                new AgentExecutionBounds(mode == "work-limit" ? 2 : 8, TimeSpan.FromMinutes(1)), new CandidateExecutionBounds(4, 1, 1));
            var host = new ScribeBusinessHost(manifest, new ScribeProgress(manifest, []), control,
                ExchangePlans.Scribe([new ExchangePolicy(true, CandidateContinuation.Continue), tail]));
            var result = await host.ExecuteFreshCandidatesAsync(startup.Agent, new InlineProgress(progress.Add), cancellation.Token);
            run = new StopRun(result, host.Progress.AcceptedCount, result.AcceptedCount, host.Exchanges.Count,
                startup.CounterCapability.Effects, startup.TransformCapability.Effects, startup.Provider.Effects, progress);
        }

        // Earlier Host acceptance and real usage survive every later stop.
        Assert.Equal(stop, run.Result.StopReason);
        Assert.Equal(units, run.Result.Outcome.CompletedWorkUnits);
        Assert.Equal(receipts, run.Result.Receipts.Count);
        Assert.True(run.Result.Receipts[0].IsAccepted);
        Assert.Equal(1, run.Result.AcceptedCount);
        Assert.Equal(attempts, run.Result.Outcome.Usage!.Attempts.Count);
        Assert.Equal(3, run.Result.Outcome.Usage.Attempts[0].Usage.InputTokens);
        Assert.Equal(3, run.Result.Outcome.Usage.Attempts[1].Usage.InputTokens);
        Assert.Equal(1, run.CounterEffects);
        Assert.Equal(receipts, run.Exchanges);
        Assert.Equal(mode == "work-limit" ? 0 : 1, run.Result.RepairsAdmitted + run.Result.ContinuationsAdmitted);

        if (hostKind == "apr")
        {
            Assert.Equal(1, run.AcceptedByHost);
        }
        else if (mode == "unknown")
        {
            // The Scribe Host commits validated facts before delivery, so committed progress can exceed the
            // acknowledged accepted count; the unknown delivery is never replayed or rolled back.
            Assert.Equal(2, run.Committed);
            Assert.Equal(2, run.Exchanges);
        }
        else
        {
            Assert.Equal(1, run.Committed);
        }

        if (mode == "provider-failure")
        {
            Assert.Equal(9, run.Result.Outcome.Usage.Attempts[2].Usage.InputTokens);
            Assert.Null(run.Result.Outcome.Usage.Attempts[2].Usage.OutputTokens);
        }
    }

    [Theory]
    [InlineData("invalid-arguments", 2)]
    [InlineData("absent-tool", 2)]
    [InlineData("wrong-concrete-capability", 3)]
    public async Task LaterBatchWithInvalidLastMemberYieldsZeroNewEffectsWhileEarlierEffectsAndAcceptanceRemain(string mode, int units)
    {
        var bad = mode switch
        {
            "invalid-arguments" => new ToolCall("bad", "transform", "{}"),
            "absent-tool" => new ToolCall("bad", "absent", "{\"text\":\"x\"}"),
            _ => ToolFixture.Transform("bad", "x"),
        };
        var steps = new Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>>[]
        {
            (r, o, _) => ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("c1", 7)], ConsumptionFixture.TurnReplay(1)),
            (r, o, _) => ConsumptionFixture.Final(r, o, "apr-item:initial:" + ConsumptionFixture.CounterTotal(r, "c1"), ConsumptionFixture.TurnReplay(2)),
            (r, o, _) => ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("c2", 3), bad], ConsumptionFixture.TurnReplay(3)),
        };
        var startup = new ProductionStartup(steps, options: new RuntimeOptions(requireContinuation: true),
            transformBinding: mode == "wrong-concrete-capability" ? new WrongTextCapability() : null);
        var host = new AprBusinessHost(startup.Agent, startup.Agent);
        var channel = new ExchangeChannel([new ExchangePolicy(true, CandidateContinuation.Continue)]);
        var result = await host.ExecuteCandidatesAsync(
            ConsumptionFixture.Candidates(ConsumptionFixture.Request(), submissions: 2, repairs: 1, continuations: 1), channel);

        Assert.Equal(CandidateStopReason.ProductionFailed, result.StopReason);
        Assert.Equal(units, result.Outcome.CompletedWorkUnits);
        Assert.Equal(1, startup.CounterCapability.Effects);
        Assert.Equal(0, startup.TransformCapability.Effects);
        Assert.Equal(3, startup.Provider.Effects);
        Assert.Single(channel.Submissions); Assert.True(result.Receipts.Single().IsAccepted);
        Assert.Equal(1, host.Acceptance.AcceptedCount);
        Assert.Equal(3, result.Outcome.Usage!.Attempts.Count);
    }

    [Theory]
    [InlineData("stop", CandidateStopReason.ProductionStopped, 3, 3)]
    [InlineData("unknown", CandidateStopReason.ProductionFailed, 3, 3)]
    [InlineData("missing", CandidateStopReason.ProductionFailed, 3, 3)]
    [InlineData("failed", CandidateStopReason.ProductionFailed, 3, 3)]
    [InlineData("exposure-denied", CandidateStopReason.ProductionStopped, 2, 3)]
    public async Task NonAuthorizingClosureBlocksNewToolEffectsAndCandidateSubmission(string mode,
        CandidateStopReason stop, int units, int attempts)
    {
        var hooks = new RuntimeHooks();
        hooks.Before = (e, _) => mode == "exposure-denied" && hooks.Exposures.Count == 3
            ? ValueTask.FromResult<ExposureAcknowledgement?>(new(e, RuntimeHookStatus.Acknowledged, ExposureDecision.Deny, e.RequiredAcknowledgement))
            : ValueTask.FromResult<ExposureAcknowledgement?>(RuntimeHooks.Permit(e));
        hooks.After = (s, _) => hooks.Settlements.Count <= 2
            ? ValueTask.FromResult<SettlementAcknowledgement?>(RuntimeHooks.Continue(s))
            : mode switch
            {
                "stop" => ValueTask.FromResult<SettlementAcknowledgement?>(new(s.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Stop)),
                "unknown" => ValueTask.FromResult<SettlementAcknowledgement?>(new(s.Exposure, RuntimeHookStatus.Unknown)),
                "missing" => ValueTask.FromResult<SettlementAcknowledgement?>(null),
                "failed" => ValueTask.FromResult<SettlementAcknowledgement?>(new(s.Exposure, RuntimeHookStatus.Failed)),
                _ => ValueTask.FromResult<SettlementAcknowledgement?>(RuntimeHooks.Continue(s)),
            };
        var steps = new Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>>[]
        {
            (r, o, _) => ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("c1", 1)], ConsumptionFixture.TurnReplay(1)),
            (r, o, _) => ConsumptionFixture.Final(r, o, "apr-item:initial:" + ConsumptionFixture.CounterTotal(r, "c1"), ConsumptionFixture.TurnReplay(2)),
            (r, o, _) => ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Counter("c2", 2)], ConsumptionFixture.TurnReplay(3)),
        };
        var startup = new ProductionStartup(steps, hooks, new RuntimeOptions(requireContinuation: true));
        var host = new AprBusinessHost(startup.Agent, startup.Agent);
        var channel = new ExchangeChannel([new ExchangePolicy(true, CandidateContinuation.Continue)]);
        var result = await host.ExecuteCandidatesAsync(
            ConsumptionFixture.Candidates(ConsumptionFixture.Request(), submissions: 2, repairs: 1, continuations: 1), channel);

        Assert.Equal(stop, result.StopReason);
        Assert.Equal(units, result.Outcome.CompletedWorkUnits);
        // The blocked turn admits no new tool effect and no candidate submission; the earlier acceptance stays.
        Assert.Equal(1, startup.CounterCapability.Effects);
        Assert.Equal(mode == "exposure-denied" ? 2 : 3, startup.Provider.Effects);
        Assert.Equal(attempts, result.Outcome.Usage!.Attempts.Count);
        Assert.Single(channel.Submissions); Assert.True(result.Receipts.Single().IsAccepted);
        Assert.Equal(1, host.Acceptance.AcceptedCount);
    }

    [Fact]
    public async Task FailedToolAfterEarlierAcceptedBatchRetainsPriorEffectsAndNeverReplays()
    {
        var startup = new ProductionStartup(
        [
            (r, o, _) => ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Transform("t1", "first")], ConsumptionFixture.TurnReplay(1)),
            (r, o, _) => ConsumptionFixture.Final(r, o, "apr-item:initial:1", ConsumptionFixture.TurnReplay(2)),
            (r, o, _) => ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Transform("t2", "second")], ConsumptionFixture.TurnReplay(3)),
        ], options: new RuntimeOptions(requireContinuation: true), transformEffect: (call, capability, token) =>
        {
            capability.Upper("effect", token);
            return ValueTask.FromResult(call.CallId == "t2" ? ToolOutput.Failure(call) : ToolOutput.Success(call, "{\"text\":\"EFFECT\"}"));
        });
        var host = new AprBusinessHost(startup.Agent, startup.Agent);
        var channel = new ExchangeChannel([new ExchangePolicy(true, CandidateContinuation.Continue)]);
        var result = await host.ExecuteCandidatesAsync(
            ConsumptionFixture.Candidates(ConsumptionFixture.Request(), submissions: 2, repairs: 1, continuations: 1), channel);

        Assert.Equal(CandidateStopReason.ProductionFailed, result.StopReason);
        Assert.Equal(2, startup.TransformCapability.Effects);
        Assert.Equal(3, startup.Provider.Effects);
        Assert.Single(channel.Submissions); Assert.True(result.Receipts.Single().IsAccepted);
        Assert.Equal(3, result.Outcome.Usage!.Attempts.Count);
    }

    [Theory]
    [InlineData(true, CandidateStopReason.Cancelled)]
    [InlineData(false, CandidateStopReason.DurationLimit)]
    public async Task HeldHostAcknowledgementReturnsAtCutAndKeepsCommittedFactsWithoutReplay(bool cancel, CandidateStopReason stop)
    {
        var clock = new ControlledTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var startup = new ProductionStartup(
        [
            (r, o, _) => ConsumptionFixture.Final(r, o, ScribeCandidatePayload.Format("alpha", "HELD " + ConsumptionFixture.ModelFactCanary),
                ConsumptionFixture.TurnReplay(1)),
        ], options: new RuntimeOptions(clock, requireContinuation: true));
        var manifest = new ScribeManifest(["alpha", "beta"]);
        var control = new ScribeHostControl(ConsumptionFixture.Instructions,
            new AgentExecutionBounds(8, TimeSpan.FromSeconds(10)), new CandidateExecutionBounds(4, 1, 1));
        var host = new ScribeBusinessHost(manifest, new ScribeProgress(manifest, []), control,
            ExchangePlans.Scribe([new ExchangePolicy(true, CandidateContinuation.Continue, ExchangeDelivery.Held)]));
        // The same fresh-injection path ExecuteFreshCandidatesAsync uses, with a witnessing decorator on the Host channel.
        var channel = new WitnessingChannel(host);
        var request = new ScribeCandidateStartup(control.CandidateBounds).Adopt(host.CreateFreshRequest());
        var pending = startup.Agent.ExecuteCandidatesAsync(request, channel, cancellationToken: cancellation.Token).AsTask();
        await RuntimeFixture.Await(host.DeliveryHeld);
        if (cancel) cancellation.Cancel(); else clock.Advance(TimeSpan.FromSeconds(11));
        var result = await RuntimeFixture.Await(pending);

        Assert.Equal(stop, result.StopReason);
        Assert.Equal(CandidateAcknowledgement.Unknown, result.Receipts.Single().Acknowledgement);
        Assert.Equal(0, result.AcceptedCount);
        // The run returned at the cut while the held delivery is still unreleased and incomplete.
        Assert.True(host.DeliveryHeld.IsCompleted);
        Assert.False(channel.DeliveryCompleted.IsCompleted);
        // The Host committed its validated fact before delivery, so committed progress exceeds the acknowledged count.
        Assert.Equal(1, host.Progress.AcceptedCount);
        Assert.Single(host.Exchanges);
        var before = System.Text.Json.JsonSerializer.Serialize(result);
        host.ReleaseHeldDelivery();
        await RuntimeFixture.Await(channel.DeliveryCompleted);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(result));
        Assert.Single(host.Exchanges);
        Assert.Equal(1, host.Progress.AcceptedCount);
        Assert.Equal(1, startup.Provider.Effects);
    }

    [Theory]
    [InlineData("tool")]
    [InlineData("provider")]
    public async Task HeldToolOrProviderReturnsAtCutBeforeReleaseAndLateCompletionChangesNothing(string stage)
    {
        var clock = new ControlledTimeProvider();
        var entered = RuntimeFixture.Barrier();
        var release = RuntimeFixture.Barrier();
        var lateCompleted = RuntimeFixture.Barrier();
        var startup = stage == "tool"
            ? new ProductionStartup(
                [(r, o, _) => ConsumptionFixture.ToolCalls(r, o, [ToolFixture.Transform("t1", "held")], ConsumptionFixture.TurnReplay(1))],
                options: new RuntimeOptions(clock, requireContinuation: true), transformEffect: async (call, capability, token) =>
                {
                    entered.SetResult();
                    await release.Task;
                    var output = ToolOutput.Success(call, "{\"text\":\"LATE\"}");
                    lateCompleted.SetResult();
                    return output;
                })
            : new ProductionStartup(
                [async (r, o, _) =>
                {
                    o.CaptureUsage(new(3, 2));
                    entered.SetResult();
                    await release.Task;
                    var response = new ProviderResponse(r.Scope, r.Attempt, ProviderFinish.Final, "late", []);
                    lateCompleted.SetResult();
                    return response;
                }],
                options: new RuntimeOptions(clock, requireContinuation: true));
        var host = new AprBusinessHost(startup.Agent, startup.Agent);
        var channel = new ExchangeChannel([new ExchangePolicy(true, CandidateContinuation.End)]);
        var pending = host.ExecuteCandidatesAsync(ConsumptionFixture.Candidates(ConsumptionFixture.Request(8)), channel).AsTask();
        await RuntimeFixture.Await(entered.Task);
        clock.Advance(TimeSpan.FromSeconds(61));
        var result = await RuntimeFixture.Await(pending);

        Assert.Equal(CandidateStopReason.DurationLimit, result.StopReason);
        // Cut-return-before-release proof: the run returned while the held operation is still incomplete.
        Assert.False(release.Task.IsCompleted);
        Assert.False(lateCompleted.Task.IsCompleted);
        Assert.Empty(channel.Submissions);
        var before = System.Text.Json.JsonSerializer.Serialize(result);
        release.SetResult();
        await RuntimeFixture.Await(lateCompleted.Task);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(result));
        Assert.Equal(1, startup.Provider.Effects);
        Assert.Empty(channel.Submissions);
    }

    private sealed class WrongTextCapability : IToolCapability
    {
        public string CapabilityId => "text_transform";
    }
}

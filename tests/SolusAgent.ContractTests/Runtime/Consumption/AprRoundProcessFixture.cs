using System.Text.Json;
using AprHost;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Candidates;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Consumption;

internal static class AprRoundProcessFixture
{
    internal static async Task<M4AprFreshProcessTests.Observation> Execute(string directory, string stage)
    {
        var time = stage.Contains("time", StringComparison.Ordinal);
        var source = stage.EndsWith("source", StringComparison.Ordinal);
        var resumed = stage.EndsWith("resume", StringComparison.Ordinal);
        var clock = new AprClock();
        var timers = new List<long>(); var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.Changed = delay => { if (delay > TimeSpan.Zero) timers.Add(delay.Ticks); if (delay == TimeSpan.FromMinutes(5)) waiting.TrySetResult(); };
        var provider = new PersistentProvider(!resumed) { UnknownUsage = !time };
        provider.Inspect = _ =>
        {
            File.AppendAllText(Path.Combine(directory, "round-provider-entries.txt"), $"{stage}:{Environment.ProcessId}\n");
            if (time && resumed) clock.Advance(TimeSpan.FromMinutes(25));
            if (time && source) clock.Advance(TimeSpan.FromMinutes(20));
            if (time && !source && !resumed) clock.Advance(TimeSpan.FromMinutes(provider.Effects == 0 ? 20 : 11));
        };
        provider.Respond = r => new(r.Scope, r.Attempt, ProviderFinish.Final, new AprItem("round", 1).ToPayload(), [],
            new(r.Scope, r.Attempt, "APR_ROUND_REPLAY"u8.ToArray()));
        var limits = new AgentUsageLimits(maximumLogicalCalls: 4, maximumPhysicalDispatches: 4,
            accountingPolicy: time ? null : new(new(source || resumed ? 100000 : 60000, 2), 100000, 100, UnknownUsagePolicy.ConservativeCharge),
            retryPolicy: new(2, backoff: time && source ? TimeSpan.FromMinutes(5) : TimeSpan.Zero));
        var request = new AgentRequest(Guid.NewGuid(), "CURRENT_HOST_INSTRUCTIONS", [], new(4, TimeSpan.FromMinutes(30)),
            SolusAgent.Api.Capabilities.AgentCapability.None, limits);
        var authorityDirectory = Path.Combine(directory, "round-authorization");
        var authority = resumed ? new AprFreshProcessFixture.CountingAuthority(authorityDirectory) : null;
        var agent = CandidateContextFixture.Agent(provider, authority,
            options: new(clock, requireContinuation: true));
        var host = new AprBusinessHost((IAgent)agent, contextCandidateAgent: agent, acceptance: new(Path.Combine(directory, "round-business")));
        ContextExecutionRequest context;
        Guid? selected = null;
        if (!resumed) context = new(request, ContextExecutionIntent.Fresh);
        else
        {
            var info = FileContextAuthority.Selection(authorityDirectory).Info;
            var grant = new ContextRoundGrant(Guid.NewGuid(), info, request.ExecutionId, Guid.NewGuid());
            selected = info.CheckpointId;
            File.WriteAllText(Path.Combine(directory, "round-grant.json"), JsonSerializer.Serialize(grant));
            context = new(request, ContextExecutionIntent.ContinueRun, FileContextAuthority.Read(authorityDirectory), grant);
        }
        var submissions = 0;
        var channel = new ScriptedCandidateHost((s, _) =>
        {
            submissions++;
            return ValueTask.FromResult<CandidateFeedback?>(new(s.ExecutionId, s.SubmissionId,
                CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept, CandidateContinuation.End));
        });
        using var cancellation = new CancellationTokenSource();
        var sink = source ? new RestrictedContextHost() : null;
        var execution = host.ExecuteCandidatesWithContextAsync(context, new(3, 2, 2), channel, sink, cancellationToken: cancellation.Token).AsTask();
        if (time && source)
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
        }
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10));
        if (source)
        {
            Assert.Equal(ContextCaptureStatus.Delivered, result.Context.CaptureStatus);
            FileContextAuthority.Save(authorityDirectory, sink!.CopyRestrictedContext(), result.Context.Checkpoint!);
            selected = result.Context.Checkpoint!.CheckpointId;
        }
        var effects = result.Candidates is null ? 0 : host.Acceptance.ApplyEffects();
        var usage = result.Context.Outcome?.Usage;
        return new(stage, Environment.ProcessId, result.Context.Admission, provider.Imports, provider.Effects, submissions,
            host.Acceptance.AcceptedCount, host.Acceptance.EffectCount, effects, result.Candidates?.HistoricalReceipts.Count ?? 0,
            result.Context.History.Sum(h => h.Usage.Attempts.Count), selected, authority?.Claims ?? 0,
            usage?.Accounting?.Input.ConservativeChargeTokens ?? 0,
            result.Context.History.Sum(h => h.Usage.Accounting?.Input.ConservativeChargeTokens ?? 0), usage?.InputTokens.ObservedTokens,
            clock.GetTimestamp(), timers.ToArray(), result.Candidates?.StopReason,
            result.Context.History.Any(h => h.Usage.InputTokens.ObservedTokens is null),
            result.Context.Checkpoint?.RoundId ?? context.RoundGrant?.RoundId);
    }

    // Records timer changes as well as creation: RunCut creates its timer disabled and then arms it.
    private sealed class AprClock : TimeProvider
    {
        private readonly ControlledTimeProvider inner = new();
        internal Action<TimeSpan>? Changed { get; set; }
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override long GetTimestamp() => inner.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        internal void Advance(TimeSpan duration) => inner.Advance(duration);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Changed?.Invoke(dueTime);
            return new Timer(inner.CreateTimer(callback, state, dueTime, period), delay => Changed?.Invoke(delay));
        }
        private sealed class Timer(ITimer inner, Action<TimeSpan> changed) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) { changed(dueTime); return inner.Change(dueTime, period); }
            public void Dispose() => inner.Dispose();
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}

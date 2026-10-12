using System.Text.Json.Nodes;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Candidates;

public sealed class CandidateClosureBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulClosureWithOriginalCutCannotAuthorizeRestoredSubmission(bool deadline)
    {
        using var cancellation = new CancellationTokenSource(); var clock = new SettlementBoundaryClock();
        var hooks = new RuntimeHooks { After = (s, _) =>
        {
            if (deadline) clock.AdvanceAfterRead = TimeSpan.FromSeconds(31);
            else cancellation.Cancel();
            return ValueTask.FromResult<SettlementAcknowledgement?>(RuntimeHooks.Continue(s));
        } };
        var sink = new RestrictedContextHost(); var host = CandidateContextFixture.Host(); var provider = new PersistentProvider();
        var source = await CandidateConsumer.RunWithContextAsync(CandidateContextFixture.Agent(provider, hooks: hooks,
            options: new(timeProvider: clock, requireContinuation: true)), new(CandidateContextFixture.Request(), ContextExecutionIntent.Fresh),
            new(1, 0, 0), host, sink, cancellationToken: cancellation.Token);
        Assert.Equal(ContextCaptureStatus.Delivered, source.Context.CaptureStatus); Assert.Empty(host.Submissions);
        Assert.Equal(deadline ? CandidateStopReason.DurationLimit : CandidateStopReason.Cancelled, source.Candidates!.StopReason);
        var envelope = sink.CopyRestrictedContext(); var saved = JsonNode.Parse(envelope.CopyRestrictedPayload())!;
        var fact = saved["Rounds"]![0]!["Facts"]![0]!;
        Assert.True(fact["AcceptedFinal"]!.GetValue<bool>()); Assert.True(fact["ClosureAcknowledged"]!.GetValue<bool>());
        Assert.Equal((int)RuntimeStop.None, fact["SettlementStop"]!.GetValue<int>());
        Assert.Equal((int)(deadline ? RuntimeStop.DurationLimit : RuntimeStop.Cancelled), fact["Stop"]!.GetValue<int>());
        var authority = new SelectedAuthority(envelope, source.Context.Checkpoint!); var nextProvider = new PersistentProvider();
        var nextHost = CandidateContextFixture.Host(); var nextSink = new RestrictedContextHost();
        var resumed = await CandidateConsumer.RunWithContextAsync(CandidateContextFixture.Agent(nextProvider, authority),
            CandidateContextFixture.Resume(source, envelope, CandidateContextFixture.Request()), new(1, 0, 0), nextHost, nextSink);
        Assert.Equal(ContextAdmission.Rejected, resumed.Context.Admission);
        Assert.Equal(ContextRejectionCode.UnsupportedContext, resumed.Context.RejectionCode);
        Assert.Equal(0, authority.Claims); Assert.Equal(0, nextProvider.Imports); Assert.Equal(0, nextProvider.Effects);
        Assert.Empty(nextHost.Submissions); Assert.Equal(0, nextSink.CaptureCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HooklessCombinedInvocationRejectsBeforeClaimImportOrEffects(bool supplied, bool capture)
    {
        ContextExecutionRequest request = new(CandidateContextFixture.Request(), ContextExecutionIntent.Fresh);
        SelectedAuthority? authority = null;
        if (supplied)
        {
            var sourceSink = new RestrictedContextHost();
            var sourceAgent = Agent(new(), new RuntimeHooks());
            var source = await CandidateConsumer.RunWithContextAsync(sourceAgent, request, new(1, 0, 0), CandidateContextFixture.Host(), sourceSink);
            Assert.Equal(ContextCaptureStatus.Delivered, source.Context.CaptureStatus);
            var envelope = sourceSink.CopyRestrictedContext(); authority = new(envelope, source.Context.Checkpoint!);
            request = CandidateContextFixture.Resume(source, envelope, CandidateContextFixture.Request());
        }
        var provider = new PersistentProvider(); var host = CandidateContextFixture.Host(); var sink = new RestrictedContextHost();
        var agent = Agent(provider, null, authority);
        await Assert.ThrowsAsync<NotSupportedException>(() => CandidateConsumer.RunWithContextAsync(agent, request,
            new(1, 0, 0), host, capture ? sink : null).AsTask());
        Assert.Equal(0, provider.Effects); Assert.Equal(0, provider.Imports); Assert.Empty(host.Submissions); Assert.Equal(0, sink.CaptureCount);
        if (authority is not null) Assert.Equal(0, authority.Claims);
        // Startup and the existing non-context path still accept this legal optional-hooks profile.
        var ordinaryHost = CandidateContextFixture.Host();
        var result = await agent.ExecuteCandidatesAsync(new(CandidateContextFixture.Request(), new(1, 0, 0)), ordinaryHost);
        Assert.Equal(CandidateStopReason.Completed, result.StopReason); Assert.Single(ordinaryHost.Submissions);
        Assert.Equal(1, provider.Effects);
    }

    private static IContextCandidateAgent Agent(PersistentProvider provider, IRuntimeExposureHooks? hooks,
        IRuntimeContextAuthority? authority = null) => (IContextCandidateAgent)RuntimeAgentFactory.Create(
            new(provider, [], hooks, requiredGuarantees: RuntimeGuarantee.ProviderBounds, contextAuthority: authority), new(requireContinuation: true));

    // Let time pass after closure's final timestamp read but before the original run's next cut check.
    // Real provider/settlement/candidate operations run normally; no production state or saved fact is forged.
    private sealed class SettlementBoundaryClock : TimeProvider
    {
        private long ticks;
        public TimeSpan AdvanceAfterRead { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            var observed = ticks; ticks += AdvanceAfterRead.Ticks; AdvanceAfterRead = TimeSpan.Zero; return observed;
        }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(ticks);
    }
}

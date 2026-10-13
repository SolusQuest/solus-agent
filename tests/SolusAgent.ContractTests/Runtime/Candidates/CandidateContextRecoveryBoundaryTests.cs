using CustomTools;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.ContractTests.Runtime.Tools;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Candidates;

public sealed class CandidateContextRecoveryBoundaryTests
{
    [Theory]
    [InlineData(ToolRecoveryGuarantee.QueryOutcome, false)]
    [InlineData(ToolRecoveryGuarantee.ConcurrentSafeRepeat, false)]
    [InlineData(ToolRecoveryGuarantee.QueryOutcome, true)]
    [InlineData(ToolRecoveryGuarantee.ConcurrentSafeRepeat, true)]
    public async Task RecoveryBindingReusesCompletedHistoryAndResumesSupportedCandidatePartialBatch(
        ToolRecoveryGuarantee guarantee, bool complete)
    {
        var counter = new CounterCapability();
        var recovery = new RecoveryCounterCapability("CANDIDATE_TARGET", guarantee) { ThrowAfterEffect = !complete };
        var tools = PartialRecoveryTests.Bindings(counter, recovery);
        var provider = new PersistentProvider(tools: true)
        {
            Respond = r => r.Inputs.Any(i => i.ToolResult is not null) ? ToolContextFixture.Final(r) : PartialRecoveryTests.Calls(r),
        };
        var request = CandidateContextFixture.Request(limits: new AgentUsageLimits(maximumToolInvocations: 3));
        var sink = new RestrictedContextHost(); var sourceHost = CandidateContextFixture.Host();
        var source = await CandidateConsumer.RunWithContextAsync(CandidateContextFixture.Agent(provider, tools: tools),
            new(request, ContextExecutionIntent.Fresh), new(1, 0, 0), sourceHost, sink);
        Assert.Equal(ContextCaptureStatus.Delivered, source.Context.CaptureStatus);
        Assert.Equal(1, recovery.Invocations); Assert.Equal(1, recovery.Effects);
        Assert.Equal(complete ? 2 : 1, counter.Effects);
        Assert.Equal(complete ? 1 : 0, sourceHost.Submissions.Count);

        recovery.ThrowAfterEffect = false;
        var envelope = sink.CopyRestrictedContext(); var authority = new SelectedAuthority(envelope, source.Context.Checkpoint!);
        var resumedProvider = new PersistentProvider(tools: true); var resumedHost = CandidateContextFixture.Host();
        var resumed = await CandidateConsumer.RunWithContextAsync(CandidateContextFixture.Agent(resumedProvider, authority, tools: tools),
            CandidateContextFixture.Resume(source, envelope, CandidateContextFixture.Request(limits: new AgentUsageLimits(maximumToolInvocations: 3))),
            new(1, 0, 0), resumedHost);
        Assert.Equal(ContextAdmission.Supplied, resumed.Context.Admission);
        Assert.Equal(AgentTerminationReason.Completed, resumed.Context.Outcome!.Reason);
        Assert.Equal(1, resumed.Candidates!.AcceptedCount);
        if (complete)
        {
            Assert.Equal(AgentTerminationReason.Completed, resumed.Context.Outcome!.Reason);
            Assert.Equal(1, resumed.Candidates!.AcceptedCount);
            Assert.Single(resumed.Candidates.HistoricalReceipts);
        }
        else Assert.Single(resumedHost.Submissions);
        Assert.Equal(1, authority.Claims); Assert.Equal(1, resumedProvider.Imports);
        Assert.Equal(complete ? 0 : 1, resumedProvider.Effects);
        Assert.Equal(!complete && guarantee == ToolRecoveryGuarantee.QueryOutcome ? 1 : 0, recovery.Queries);
        Assert.Equal(!complete && guarantee == ToolRecoveryGuarantee.ConcurrentSafeRepeat ? 2 : 1, recovery.Invocations);
        Assert.Equal(1, recovery.Effects); Assert.Equal(2, counter.Effects);
        Assert.Equal(complete ? 0 : 2, resumed.Context.Outcome.Usage!.ToolInvocations!.Invoked);
    }
}

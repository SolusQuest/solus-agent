using System.Text.Json;
using CustomTools;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ContractTests.Runtime.Candidates;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Tools;

public sealed class ArgumentCorrectionTests
{
    [Theory]
    [InlineData(false, 0)] [InlineData(true, 2)]
    public async Task DeadlineBeforeOrAfterCorrectionCommitNeverAdmitsDependentWork(bool after, int admissions)
    {
        var clock = new ControlledTimeProvider(); var fixture = new ArgumentCorrectionFixture();
        if (!after) fixture.Validate = r => { if (r.Inputs.Any(i => i.Model?.Correction is not null)) clock.Advance(TimeSpan.FromSeconds(10)); };
        var outcome = await fixture.Agent(new(timeProvider: clock, requireContinuation: true)).ExecuteAsync(ArgumentCorrectionFixture.Request(),
            new InlineProgress(_ => { if (after) clock.Advance(TimeSpan.FromSeconds(10)); }));
        Assert.Equal(AgentTerminationReason.ResourceLimit, outcome.Reason); Assert.Equal(1, outcome.CompletedWorkUnits);
        Assert.Equal(admissions, outcome.Usage!.ToolCallAdmissions!.Admitted); Assert.Equal(0, outcome.Usage.ToolInvocations!.Invoked);
        Assert.Equal(0, fixture.Capability.Entries); Assert.Equal(1, fixture.Provider.Effects);
    }

    [Theory]
    [InlineData("schema")] [InlineData("path")]
    public async Task ArgumentDenialCannotHideInvalidRecoveryTargetBinding(string mode)
    {
        var fixture = new ArgumentCorrectionFixture(mode);
        var capability = new RecoveryCounterCapability("actual", ToolRecoveryGuarantee.QueryOutcome);
        var tool = new RecoverableCounterTool();
        var provider = new ScriptedProvider([(r, o, _) =>
        {
            o.CaptureUsage(new(3, 2));
            return ValueTask.FromResult(new ProviderResponse(r.Scope, r.Attempt, ProviderFinish.ToolCalls, null,
                [new("bad", "read_snapshot", fixture.BadArguments), new("recovery", tool.Descriptor.Name, "{\"amount\":1}")],
                new(r.Scope, r.Attempt, [1, 42])));
        }], capabilities: ArgumentCorrectionFixture.Capabilities);
        var agent = RuntimeAgentFactory.Create(new(provider, [fixture.Binding(), new(tool, capability,
            new("keyed_recovery", "different", ToolRecoveryGuarantee.QueryOutcome))], fixture.Hooks,
            toolArgumentCorrection: fixture.Policy), new(requireContinuation: true));
        var outcome = await agent.ExecuteAsync(ArgumentCorrectionFixture.Request());
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(1, outcome.CompletedWorkUnits);
        Assert.Equal(0, outcome.Usage!.ToolCallAdmissions!.Admitted); Assert.Equal(0, outcome.Usage.ToolInvocations!.Invoked);
        Assert.Equal(0, capability.Invocations); Assert.Equal(0, capability.Queries); Assert.Equal(0, capability.Effects);
        Assert.Equal(0, fixture.Capability.Entries); Assert.Equal(0, fixture.Classifications);
    }

    [Fact]
    public async Task ProgressFailureAfterCorrectionPreservesIrreversibleAdmissionWithoutNewEffects()
    {
        var fixture = new ArgumentCorrectionFixture();
        var outcome = await fixture.Agent().ExecuteAsync(ArgumentCorrectionFixture.Request(), new InlineProgress(_ => throw new InvalidOperationException("observer")));
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(1, outcome.CompletedWorkUnits);
        Assert.Equal(2, outcome.Usage!.ToolCallAdmissions!.Admitted); Assert.Equal(0, outcome.Usage.ToolInvocations!.Invoked);
        Assert.Equal(0, fixture.Capability.Entries); Assert.Equal(1, fixture.Provider.Effects);
    }

    [Fact]
    public async Task EqualExecutionIdsRetainIndependentTotalAdmissionLedgers()
    {
        var left = new ArgumentCorrectionFixture(); var right = new ArgumentCorrectionFixture(); var id = Guid.NewGuid();
        var outcomes = await Task.WhenAll(left.Agent().ExecuteAsync(ArgumentCorrectionFixture.Request(id: id)).AsTask(),
            right.Agent().ExecuteAsync(ArgumentCorrectionFixture.Request(id: id)).AsTask());
        Assert.All(outcomes, o => { Assert.Equal(AgentTerminationReason.Completed, o.Reason); Assert.Equal(4, o.Usage!.ToolCallAdmissions!.Admitted); });
        Assert.Equal(2, left.Capability.Entries); Assert.Equal(2, right.Capability.Entries);
        Assert.NotEqual(left.Requests[0].Attempt.PhysicalAttemptId, right.Requests[0].Attempt.PhysicalAttemptId);
    }

    [Theory]
    [InlineData("schema", false)] [InlineData("schema", true)]
    [InlineData("json", false)] [InlineData("depth", false)]
    [InlineData("path", false)] [InlineData("path", true)]
    [InlineData("membership", false)] [InlineData("membership", true)]
    [InlineData("cursor", false)] [InlineData("cursor", true)]
    public async Task BothDriversWithholdCompleteBatchThenAdmitOnlyCorrectedWork(string mode, bool candidates)
    {
        var fixture = new ArgumentCorrectionFixture(mode); var progress = new List<AgentProgress>();
        var outcome = await Run(fixture, candidates, progress: new InlineProgress(progress.Add));
        Assert.Equal(AgentTerminationReason.Completed, outcome.Reason); Assert.Equal(3, outcome.CompletedWorkUnits);
        Assert.Equal(4, outcome.Usage!.ToolCallAdmissions!.Admitted); Assert.Equal(2, outcome.Usage.ToolInvocations!.Invoked);
        Assert.Equal(2, fixture.Capability.Entries); Assert.Equal(3, fixture.Provider.Effects);
        Assert.Equal(3, outcome.Usage.Attempts.Count); Assert.Equal(9, outcome.Usage.InputTokens.ObservedTokens);
        Assert.Equal(3, fixture.Hooks.Exposures.Count); Assert.Equal(3, fixture.Hooks.Settlements.Count);
        Assert.Equal(mode is "schema" or "json" or "depth" ? 2 : 4, fixture.Classifications);
        Assert.Equal(mode is "schema" or "json" or "depth" ? 2 : 3, fixture.Tool.DomainChecks);
        Assert.Equal(2, progress[0].Usage!.ToolCallAdmissions!.Admitted); Assert.Equal(0, progress[0].Usage!.ToolInvocations!.Invoked);
        Assert.Equal(1, progress[0].CompletedWorkUnits); Assert.Equal(2, progress[1].Usage!.ToolInvocations!.Invoked);
        Assert.DoesNotContain(ArgumentCorrectionFixture.Canary, JsonSerializer.Serialize(outcome) + JsonSerializer.Serialize(progress));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TotalAdmissionLimitRefusesWholeCorrectedBatchWithoutRefundingCorrection(bool candidates)
    {
        var fixture = new ArgumentCorrectionFixture(); var outcome = await Run(fixture, candidates, ArgumentCorrectionFixture.Request(admissions: 3));
        Assert.Equal(AgentTerminationReason.ResourceLimit, outcome.Reason); Assert.Equal(2, outcome.CompletedWorkUnits);
        Assert.Equal(2, outcome.Usage!.ToolCallAdmissions!.Admitted); Assert.Equal(0, outcome.Usage.ToolInvocations!.Invoked);
        Assert.Equal(0, fixture.Capability.Entries); Assert.Equal(2, fixture.Provider.Effects);
    }

    [Theory]
    [InlineData("unknown_tool", AgentTerminationReason.Failed, 0)]
    [InlineData("bytes", AgentTerminationReason.ResourceLimit, 0)]
    [InlineData("encoding", AgentTerminationReason.Failed, 0)]
    [InlineData("fatal", AgentTerminationReason.Failed, 1)]
    [InlineData("unsafe", AgentTerminationReason.Failed, 1)]
    [InlineData("unknown_code", AgentTerminationReason.Failed, 1)]
    [InlineData("throw", AgentTerminationReason.Failed, 1)]
    public async Task FatalNeighborsRetainActualUsageWithoutFeedbackAdmissionsOrEntries(string mode, AgentTerminationReason reason, int work)
    {
        var fixture = new ArgumentCorrectionFixture(mode); var outcome = await fixture.Agent().ExecuteAsync(ArgumentCorrectionFixture.Request());
        Assert.Equal(reason, outcome.Reason); Assert.Equal(work, outcome.CompletedWorkUnits);
        Assert.Equal(0, outcome.Usage!.ToolCallAdmissions!.Admitted); Assert.Equal(0, outcome.Usage.ToolInvocations!.Invoked);
        Assert.Equal(0, fixture.Capability.Entries); Assert.Equal(1, fixture.Provider.Effects);
        Assert.Equal(3, Assert.Single(outcome.Usage.Attempts).Usage.InputTokens);
        Assert.DoesNotContain(ArgumentCorrectionFixture.Canary, JsonSerializer.Serialize(outcome));
    }

    [Theory]
    [InlineData("schema")] [InlineData("path")]
    public async Task DeniedMemberCannotHideWrongConcreteCapability(string mode)
    {
        var fixture = new ArgumentCorrectionFixture(mode); var outcome = await fixture.Agent(wrongCapability: true).ExecuteAsync(ArgumentCorrectionFixture.Request());
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(1, outcome.CompletedWorkUnits);
        Assert.Equal(0, outcome.Usage!.ToolCallAdmissions!.Admitted); Assert.Equal(0, outcome.Usage.ToolInvocations!.Invoked);
        Assert.Equal(0, fixture.Classifications); Assert.Equal(0, fixture.Tool.DomainChecks); Assert.Equal(0, fixture.Capability.Entries);
    }

    [Theory]
    [InlineData("schema", 0)] [InlineData("unsafe", 1)]
    public async Task AbsentOptInKeepsFatalSchemaAndDomainBehavior(string mode, int work)
    {
        var fixture = new ArgumentCorrectionFixture(mode, enabled: false); var outcome = await fixture.Agent().ExecuteAsync(ArgumentCorrectionFixture.Request());
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(work, outcome.CompletedWorkUnits);
        Assert.Equal(0, outcome.Usage!.ToolCallAdmissions!.Admitted); Assert.Equal(0, outcome.Usage.ToolInvocations!.Invoked);
        Assert.Equal(0, fixture.Classifications); Assert.Equal(1, fixture.Provider.Effects);
    }

    [Theory]
    [InlineData("stop")] [InlineData("missing")] [InlineData("unknown")] [InlineData("throw")]
    public async Task HostClosureCannotBeBypassedBySanitizedProviderData(string mode)
    {
        var fixture = new ArgumentCorrectionFixture();
        fixture.Hooks.After = (s, _) => mode switch
        {
            "stop" => ValueTask.FromResult<SettlementAcknowledgement?>(new(s.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Stop)),
            "missing" => ValueTask.FromResult<SettlementAcknowledgement?>(null),
            "unknown" => ValueTask.FromResult<SettlementAcknowledgement?>(new(s.Exposure, RuntimeHookStatus.Unknown)),
            _ => throw new InvalidOperationException(ArgumentCorrectionFixture.Canary),
        };
        var outcome = await fixture.Agent().ExecuteAsync(ArgumentCorrectionFixture.Request());
        Assert.Equal(mode == "stop" ? AgentTerminationReason.Partial : AgentTerminationReason.Failed, outcome.Reason);
        Assert.Equal(1, outcome.CompletedWorkUnits); Assert.Equal(0, outcome.Usage!.ToolCallAdmissions!.Admitted);
        Assert.Equal(0, fixture.Capability.Entries); Assert.Equal(1, fixture.Provider.Effects);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(true, 2)]
    public async Task CancellationBeforeOrAfterAtomicCommitNeverStartsDependentWork(bool after, int admissions)
    {
        using var cancellation = new CancellationTokenSource(); var fixture = new ArgumentCorrectionFixture();
        if (!after) fixture.Validate = r => { if (r.Inputs.Any(i => i.Model?.Correction is not null)) cancellation.Cancel(); };
        var progress = new InlineProgress(_ => { if (after) cancellation.Cancel(); });
        var outcome = await fixture.Agent().ExecuteAsync(ArgumentCorrectionFixture.Request(), progress, cancellation.Token);
        Assert.Equal(AgentTerminationReason.Cancelled, outcome.Reason); Assert.Equal(1, outcome.CompletedWorkUnits);
        Assert.Equal(admissions, outcome.Usage!.ToolCallAdmissions!.Admitted); Assert.Equal(0, outcome.Usage.ToolInvocations!.Invoked);
        Assert.Equal(0, fixture.Capability.Entries); Assert.Equal(1, fixture.Provider.Effects);
    }

    [Theory]
    [InlineData(4, AgentTerminationReason.ResourceLimit, 0, 1)]
    [InlineData(5, AgentTerminationReason.Completed, 2, 2)]
    public async Task CorrectionReservesCompleteFeedbackAndTheNextModelRecord(int records, AgentTerminationReason reason, int admissions, int attempts)
    {
        var fixture = new ArgumentCorrectionFixture(finalImmediately: true);
        var outcome = await fixture.Agent(new(maximumRecords: records, requireContinuation: true)).ExecuteAsync(ArgumentCorrectionFixture.Request(work: 2));
        Assert.Equal(reason, outcome.Reason); Assert.Equal(admissions, outcome.Usage!.ToolCallAdmissions!.Admitted);
        Assert.Equal(attempts, fixture.Provider.Effects); Assert.Equal(0, fixture.Capability.Entries);
    }

    [Fact]
    public async Task ProspectiveProviderFailureLeavesNoPartialFeedbackOrAdmission()
    {
        var fixture = new ArgumentCorrectionFixture(); fixture.Validate = r =>
        { if (r.Inputs.Any(i => i.Model?.Correction is not null)) throw new ProviderContractException(ProviderError.LimitExceeded); };
        var outcome = await fixture.Agent().ExecuteAsync(ArgumentCorrectionFixture.Request());
        Assert.Equal(AgentTerminationReason.ResourceLimit, outcome.Reason); Assert.Equal(1, outcome.CompletedWorkUnits);
        Assert.Equal(0, outcome.Usage!.ToolCallAdmissions!.Admitted); Assert.Equal(0, fixture.Capability.Entries); Assert.Equal(1, fixture.Provider.Effects);
    }

    [Fact]
    public async Task InvalidActualResultIsTerminalAndNeverBecomesAnotherArgumentCorrection()
    {
        var fixture = new ArgumentCorrectionFixture("result"); var outcome = await fixture.Agent().ExecuteAsync(ArgumentCorrectionFixture.Request());
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(2, outcome.CompletedWorkUnits);
        Assert.Equal(4, outcome.Usage!.ToolCallAdmissions!.Admitted); Assert.Equal(1, outcome.Usage.ToolInvocations!.Invoked);
        Assert.Equal(1, fixture.Capability.Entries); Assert.Equal(2, fixture.Provider.Effects);
    }

    [Theory]
    [InlineData(1, AgentTerminationReason.ResourceLimit, 0)] [InlineData(2, AgentTerminationReason.Completed, 2)]
    public async Task OptionalTotalAdmissionBoundAlsoEnforcesNormalDefaultBatches(int limit, AgentTerminationReason reason, int entries)
    {
        var fixture = new ArgumentCorrectionFixture("valid", enabled: false); var outcome = await fixture.Agent().ExecuteAsync(ArgumentCorrectionFixture.Request(work: 2, admissions: limit));
        Assert.Equal(reason, outcome.Reason); Assert.Equal(entries, fixture.Capability.Entries);
        Assert.Equal(entries, outcome.Usage!.ToolCallAdmissions!.Admitted); Assert.Equal(entries, outcome.Usage.ToolInvocations!.Invoked);
    }

    private static async Task<AgentOutcome> Run(ArgumentCorrectionFixture fixture, bool candidates, AgentRequest? request = null, IProgress<AgentProgress>? progress = null)
    {
        var agent = fixture.Agent(); request ??= ArgumentCorrectionFixture.Request();
        if (!candidates) return await agent.ExecuteAsync(request, progress);
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s));
        var result = await ((ICandidateAgent)agent).ExecuteCandidatesAsync(new(request, new(1, 0, 0)), host, progress);
        Assert.Equal(0, result.RepairsAdmitted); Assert.Equal(0, result.ContinuationsAdmitted);
        Assert.Equal(result.Outcome.Reason == AgentTerminationReason.Completed ? 1 : 0, host.Submissions.Count);
        return result.Outcome;
    }
}

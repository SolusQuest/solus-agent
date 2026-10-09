using System.Text.Json;
using CustomTools;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Candidates;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Execution;
using SolusAgent.Runtime.Startup;
using SolusAgent.Runtime.Tools;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Tools;

public sealed class ToolAllowanceTests
{
    [Theory]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)]
    [InlineData(false, null)] [InlineData(true, null)]
    public async Task WholeBatchMustFitAndExactOrAbsentAllowanceCompletes(bool candidate, int? limit)
    {
        var counter = new CounterCapability(); var transform = new TransformCapability();
        var provider = new ScriptedProvider([Batch, ScriptedProvider.Final]);
        var hooks = new RuntimeHooks(); var configuration = new SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration(provider,
            ToolFixture.Bindings(new(maximumResultBytes: 64), counter, new TransformTool(), transform), hooks);
        var agent = RuntimeAgentFactory.Create(configuration); var progress = new List<AgentProgress>();
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s));
        var outcome = await Run(agent, Request(limit), candidate, new InlineProgress(progress.Add), host: host);
        var fits = limit != 1;
        Assert.True(agent.SupportedCapabilities.HasFlag(AgentCapability.ToolInvocationLimit));
        Assert.True(RuntimeAgentFactory.Describe(configuration).Support.SupportedCapabilities.HasFlag(AgentCapability.ToolInvocationLimit));
        Assert.Equal(fits ? AgentTerminationReason.Completed : AgentTerminationReason.ResourceLimit, outcome.Reason);
        Assert.Equal(fits ? 1 : 0, counter.Effects); Assert.Equal(fits ? 1 : 0, transform.Effects);
        Assert.Equal(fits ? 2 : 1, outcome.CompletedWorkUnits); Assert.Equal(fits ? 2 : 1, provider.Effects);
        Assert.Equal(candidate && fits ? 1 : 0, host.Submissions.Count);
        Counts(outcome.Usage, fits ? 2 : 0, 0, 0);
        Assert.Equal(provider.Effects, outcome.Usage!.Attempts.Count);
        Assert.All(outcome.Usage.Attempts, a => { Assert.Equal(3, a.Usage.InputTokens); Assert.Equal(2, a.Usage.OutputTokens); });
        if (fits)
        {
            Assert.Equal(2, progress.Count);
            Assert.All(progress, p => Counts(p.Usage, 2, 0, 0));
            Assert.Single(progress[0].Usage!.Attempts);
        }
        else Assert.Empty(progress);
    }

    [Theory]
    [InlineData(false, 3)] [InlineData(true, 3)]
    [InlineData(false, 4)] [InlineData(true, 4)]
    [InlineData(false, null)] [InlineData(true, null)]
    public async Task LaterBatchCannotSpendOnlyTheRemainingPrefix(bool candidate, int? limit)
    {
        var counter = new CounterCapability(); var transform = new TransformCapability();
        var provider = new ScriptedProvider([Batch, (r, o, _) =>
        {
            Assert.Equal(2, r.Inputs.Count(i => i.ToolResult is not null));
            Assert.Equal("{\"total\":1}", r.Inputs.Single(i => i.ToolResult?.Call.ToolName == "counter").ToolResult!.Json);
            return ToolFixture.Calls(r, o, [ToolFixture.Counter("next_c"), ToolFixture.Transform("next_t")]);
        }, ScriptedProvider.Final]);
        var progress = new List<AgentProgress>();
        var outcome = await Run(ToolFixture.Agent(provider, ToolFixture.Bindings(new(maximumResultBytes: 64), counter,
            new TransformTool(), transform)), Request(limit), candidate, new InlineProgress(progress.Add));
        var fits = limit != 3;
        Assert.Equal(fits ? AgentTerminationReason.Completed : AgentTerminationReason.ResourceLimit, outcome.Reason);
        Assert.Equal(fits ? 2 : 1, counter.Effects); Assert.Equal(fits ? 2 : 1, transform.Effects);
        Counts(outcome.Usage, fits ? 4 : 2, 0, 0);
        Counts(progress[0].Usage, 2, 0, 0);
        Assert.Equal(fits ? 3 : 2, outcome.Usage!.Attempts.Count);
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(true, false, false)]
    [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(true, false, true)]
    [InlineData(false, true, true)] [InlineData(true, true, true)]
    public async Task CutReleasesOnlyUnstartedAndLateCompletionCannotRewritePublicSnapshots(bool candidate, bool deadline, bool earlier)
    {
        var clock = new ControlledTimeProvider(); using var cancellation = new CancellationTokenSource();
        var entered = RuntimeFixture.Barrier<ToolCall>(); var release = RuntimeFixture.Barrier<ToolOutput>();
        var counter = new CounterCapability(); var transform = new TransformCapability();
        var held = new ObservedTool(new TransformTool(effect: (call, cap, token) =>
        { cap.Upper("effect", token); entered.SetResult(call); return new(release.Task); }));
        var steps = new List<Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>>>();
        if (earlier) steps.Add((r, o, _) => ToolFixture.Calls(r, o, [ToolFixture.Counter("earlier")]));
        steps.Add((r, o, _) => ToolFixture.Calls(r, o, [ToolFixture.Transform("held"), ToolFixture.Counter("never")]));
        var provider = new ScriptedProvider(steps); var progress = new List<AgentProgress>();
        var agent = ToolFixture.Agent(provider, ToolFixture.Bindings(new(maximumResultBytes: 64), counter, held, transform), new(clock));
        var run = Run(agent, Request(earlier ? 3 : 2), candidate, new InlineProgress(progress.Add), cancellation.Token);
        var call = await RuntimeFixture.Await(entered.Task);
        if (deadline) clock.Advance(TimeSpan.FromSeconds(10)); else cancellation.Cancel();
        var outcome = await RuntimeFixture.Await(run);
        Assert.False(release.Task.IsCompleted); Assert.False(held.Completed.Task.IsCompleted);
        Assert.Equal(deadline ? AgentTerminationReason.ResourceLimit : AgentTerminationReason.Cancelled, outcome.Reason);
        Counts(outcome.Usage, earlier ? 2 : 1, 0, 1);
        Assert.Equal(earlier ? 1 : 0, counter.Effects); Assert.Equal(1, transform.Effects);
        Assert.Equal(earlier ? 2 : 1, provider.Effects);
        Assert.Equal(provider.Effects, outcome.Usage!.Attempts.Count);
        Assert.All(outcome.Usage.Attempts, a => Assert.Equal(3, a.Usage.InputTokens));
        if (earlier) Counts(Assert.Single(progress).Usage, 1, 0, 0); else Assert.Empty(progress);
        var snapshot = JsonSerializer.Serialize(outcome) + JsonSerializer.Serialize(progress);
        if (deadline) release.SetException(new InvalidOperationException("LATE_TOOL_CANARY"));
        else release.SetResult(ToolOutput.Success(call, "{\"text\":\"LATE_TOOL_CANARY\"}"));
        await RuntimeFixture.Await(held.Invocation!);
        Assert.Equal(snapshot, JsonSerializer.Serialize(outcome) + JsonSerializer.Serialize(progress));
        Assert.Equal(earlier ? 1 : 0, counter.Effects); Assert.Equal(1, transform.Effects);
        Assert.DoesNotContain("LATE_TOOL_CANARY", snapshot);
    }

    [Fact]
    public async Task InFlightReservationIsIndependentAndCleanupIsIdempotentWithoutReplay()
    {
        using var cancellation = new CancellationTokenSource(); var options = new RuntimeOptions();
        using var cut = new RunCut(options.TimeProvider, TimeSpan.FromSeconds(10), cancellation.Token);
        var entered = RuntimeFixture.Barrier<ToolCall>(); var release = RuntimeFixture.Barrier<ToolOutput>();
        var cap = new TransformCapability(); var counter = new CounterCapability();
        var held = new ObservedTool(new TransformTool(effect: (call, capability, token) =>
        { capability.Upper("effect", token); entered.SetResult(call); return new(release.Task); }));
        var provider = new ScriptedProvider([(r, o, _) => ToolFixture.Calls(r, o, [ToolFixture.Transform(), ToolFixture.Counter()])]);
        var state = new RunState(Request(2), new(provider, ToolFixture.Bindings(new(maximumResultBytes: 64), counter, held, cap), new RuntimeHooks()), options, cut);
        state.Initialize(); var request = state.AdmitTurn()!; var accepted = await ProviderAttemptOperation.ExecuteAsync(state, request);
        var run = ToolBatchOperation.ExecuteAsync(state, request, accepted.Response!).AsTask();
        var call = await RuntimeFixture.Await(entered.Task); var inFlight = state.Usage();
        Counts(inFlight, 1, 1, 0);
        cancellation.Cancel(); await RuntimeFixture.Await(run);
        Counts(state.Usage(), 1, 0, 1); Counts(inFlight, 1, 1, 0);
        state.ReleaseToolBatch(0, 2); Counts(state.Usage(), 1, 0, 1);
        await ToolBatchOperation.ExecuteAsync(state, request, accepted.Response!);
        Counts(state.Usage(), 1, 0, 1); Assert.Equal(0, counter.Effects);
        release.SetResult(ToolOutput.Success(call, "{\"text\":\"late\"}")); await RuntimeFixture.Await(held.Invocation!);
        Counts(state.Usage(), 1, 0, 1); Counts(inFlight, 1, 1, 0);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task EqualIdsOverlapWithIndependentReservationsAndCuts(bool candidate)
    {
        var entered1 = RuntimeFixture.Barrier<ToolCall>(); var entered2 = RuntimeFixture.Barrier<ToolCall>();
        var release1 = RuntimeFixture.Barrier<ToolOutput>(); var release2 = RuntimeFixture.Barrier<ToolOutput>();
        var lateFinished = RuntimeFixture.Barrier(); var invocations = 0;
        var transform = new TransformCapability(); var counter = new CounterCapability();
        var tool = new TransformTool(effect: async (call, capability, token) =>
        {
            capability.Upper("effect", token);
            if (Interlocked.Increment(ref invocations) == 1)
            {
                entered1.SetResult(call);
                try { return await release1.Task; }
                finally { lateFinished.TrySetResult(); }
            }
            entered2.SetResult(call); return await release2.Task;
        });
        var provider = new ScriptedProvider([
            (r, o, _) => ToolFixture.Calls(r, o, [ToolFixture.Transform("same"), ToolFixture.Counter("same_counter")]),
            (r, o, _) => ToolFixture.Calls(r, o, [ToolFixture.Transform("same"), ToolFixture.Counter("same_counter")]), ScriptedProvider.Final]);
        var agent = ToolFixture.Agent(provider, ToolFixture.Bindings(new(maximumResultBytes: 64), counter, tool, transform));
        var id = Guid.NewGuid(); using var cancellation = new CancellationTokenSource();
        var first = Run(agent, Request(2, id), candidate, cancellationToken: cancellation.Token);
        var call1 = await RuntimeFixture.Await(entered1.Task);
        var second = Run(agent, Request(2, id), candidate); var call2 = await RuntimeFixture.Await(entered2.Task);
        cancellation.Cancel(); var stopped = await RuntimeFixture.Await(first);
        Assert.False(second.IsCompleted); Counts(stopped.Usage, 1, 0, 1);
        release2.SetResult(ToolOutput.Success(call2, "{\"text\":\"SECOND\"}"));
        var completed = await RuntimeFixture.Await(second); Counts(completed.Usage, 2, 0, 0);
        Assert.Equal(AgentTerminationReason.Cancelled, stopped.Reason); Assert.Equal(AgentTerminationReason.Completed, completed.Reason);
        Assert.Equal(1, counter.Effects); Assert.Equal(2, transform.Effects); Assert.Equal(3, provider.Effects);
        Assert.NotEqual(stopped.Usage!.Attempts[0].PhysicalAttemptId, completed.Usage!.Attempts[0].PhysicalAttemptId);
        release1.SetResult(ToolOutput.Success(call1, "{\"text\":\"LATE\"}")); await RuntimeFixture.Await(lateFinished.Task);
        Counts(stopped.Usage, 1, 0, 1); Counts(completed.Usage, 2, 0, 0); Assert.Equal(1, counter.Effects);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task InvalidLaterMemberRejectsBeforeAnyReservationOrEffect(bool candidate)
    {
        var counter = new CounterCapability(); var transform = new TransformCapability();
        var provider = new ScriptedProvider([Batch]);
        var outcome = await Run(ToolFixture.Agent(provider, ToolFixture.Bindings(new(maximumResultBytes: 64), counter,
            new TransformTool(rejectDomain: true), transform)), Request(2), candidate);
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason);
        Counts(outcome.Usage, 0, 0, 0); Assert.Equal(0, counter.Effects); Assert.Equal(0, transform.Effects);
        Assert.Equal(1, provider.Effects);
    }

    internal static AgentRequest Request(int? limit, Guid? id = null, int units = 8) =>
        new(id ?? Guid.NewGuid(), "synthetic", [], new(units, TimeSpan.FromSeconds(10)),
            AgentCapability.ToolInvocationLimit | AgentCapability.UsageReporting, new(maximumToolInvocations: limit));
    internal static async Task<AgentOutcome> Run(IAgent agent, AgentRequest request, bool candidate,
        IProgress<AgentProgress>? progress = null, CancellationToken cancellationToken = default, ScriptedCandidateHost? host = null)
    {
        if (!candidate) return await agent.ExecuteAsync(request, progress, cancellationToken);
        var result = await ((ICandidateAgent)agent).ExecuteCandidatesAsync(new(request, new(4, 2, 2)),
            host ?? new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s)), progress, cancellationToken);
        if (result.Outcome.Reason == AgentTerminationReason.ResourceLimit)
            Assert.Contains(result.StopReason, new[] { CandidateStopReason.RuntimeLimit, CandidateStopReason.DurationLimit, CandidateStopReason.WorkUnitLimit });
        return result.Outcome;
    }
    internal static void Counts(AgentRunUsage? usage, int invoked, int reserved, int released)
    {
        Assert.NotNull(usage); var tools = Assert.IsType<ToolInvocationUsage>(usage.ToolInvocations);
        Assert.Equal(invoked, tools.Invoked); Assert.Equal(reserved, tools.ReservedUnstarted); Assert.Equal(released, tools.ReleasedUnstarted);
    }
    private static ValueTask<ProviderResponse> Batch(ProviderRequest r, ProviderObservation o, CancellationToken _) =>
        ToolFixture.Calls(r, o, [ToolFixture.Counter(), ToolFixture.Transform()]);

    internal sealed class ObservedTool(IFunctionTool inner) : IFunctionTool
    {
        public TaskCompletionSource Completed { get; } = RuntimeFixture.Barrier();
        public Task<ToolResult>? Invocation { get; private set; }
        public ToolDescriptor Descriptor => inner.Descriptor;
        public ToolPreparation Prepare(ToolCall call) => inner.Prepare(call);
        public ToolError ValidateInvocation(PreparedToolInvocation prepared, ToolCall call, IToolCapability? capability) =>
            inner.ValidateInvocation(prepared, call, capability);
        public ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation prepared, ToolCall call, IToolCapability? capability, CancellationToken token = default)
        {
            Invocation = Observe();
            return new(Invocation);
            async Task<ToolResult> Observe()
            {
                try { return await inner.InvokeAsync(prepared, call, capability, token); }
                finally { Completed.TrySetResult(); }
            }
        }
    }
}

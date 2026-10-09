using System.Net;
using System.Text.Json;
using CustomTools;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ContractTests.DeepSeek.Adapter;
using SolusAgent.ContractTests.Runtime.Candidates;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.RuntimeIntegration;

public sealed class RetryAccountingCompositionTests
{
    internal static AgentRequest Request(AgentUsageLimits limits, int units = 1) => IntegrationFixture.Request(units: units, usageLimits: limits).Execution;
    internal static async Task<AgentOutcome> Execute(IAgent agent, AgentRequest request, bool candidate, CancellationToken token = default) => candidate
        ? (await CandidateConsumer.RunAsync((ICandidateAgent)agent, new(request, new(8, 8, 8)),
            new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s)), token)).Result.Outcome
        : await agent.ExecuteAsync(request, cancellationToken: token);
    internal const string MeasuredError = "{\"error\":\"restricted-error-canary\",\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2}}";

    public static IEnumerable<object[]> PolicyCases()
    {
        foreach (var candidate in new[] { false, true })
        foreach (var policy in Enum.GetValues<UnknownUsagePolicy>())
        foreach (var shape in new[] { "known", "zero", "missing", "input-only", "output-only", "response-loss", "body-loss" }) yield return [candidate, policy, shape];
    }
    [Theory, MemberData(nameof(PolicyCases))]
    public async Task HttpDerivedMeasurementsSettleEachAxisBeforeRetry(bool candidate, UnknownUsagePolicy policy, string shape)
    {
        var sends = 0;
        using var handler = new FakeHandler((_, _) =>
        {
            if (++sends > 1) return Task.FromResult(AdapterFixture.Http(AdapterFixture.Response()));
            if (shape == "response-loss") throw new HttpRequestException(HttpRequestError.ResponseEnded, "restricted-loss-canary");
            if (shape == "body-loss") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new InterruptedContent() });
            return Task.FromResult(AdapterFixture.Http(shape switch
            {
                "known" => MeasuredError,
                "zero" => "{\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":0}}",
                "input-only" => "{\"usage\":{\"prompt_tokens\":3}}",
                "output-only" => "{\"usage\":{\"completion_tokens\":2}}",
                _ => "{}",
            }, HttpStatusCode.ServiceUnavailable));
        });
        using var provider = AdapterFixture.Provider(handler); var hooks = new RuntimeHooks();
        var agent = RuntimeAgentFactory.Create(new(provider, [], hooks), IntegrationFixture.Options());
        var result = await Execute(agent, Request(new(maximumLogicalCalls: 1, maximumPhysicalDispatches: 2,
            accountingPolicy: new(new(5, 4), 40, 40, policy), retryPolicy: new(2))), candidate);
        var stops = shape is not ("known" or "zero") && policy == UnknownUsagePolicy.Stop;
        Assert.Equal(stops ? AgentTerminationReason.Partial : AgentTerminationReason.Completed, result.Reason);
        Assert.Equal(stops ? 1 : 2, handler.Sends); Assert.Equal(stops ? 0 : 1, result.CompletedWorkUnits);
        Assert.Equal(handler.Sends, result.Usage!.Attempts.Count); Assert.Equal(handler.Sends, hooks.Settlements.Count);
        var observation = result.Usage.Attempts[0]; var accounting = result.Usage.Accounting!;
        Assert.Equal(shape == "response-loss" ? DispatchExposure.Unknown : DispatchExposure.Dispatched, observation.Exposure);
        long? input = shape == "zero" ? 0 : shape is "known" or "input-only" ? 3 : null;
        long? output = shape == "zero" ? 0 : shape is "known" or "output-only" ? 2 : null;
        Assert.Equal(input, observation.Usage.InputTokens); Assert.Equal(output, observation.Usage.OutputTokens);
        foreach (var axis in new[] { (accounting.Attempts[0].Input, input, 5L), (accounting.Attempts[0].Output, output, 4L) })
        {
            Assert.Equal(axis.Item2.HasValue ? AccountingDisposition.Measured : policy == UnknownUsagePolicy.ConservativeCharge
                ? AccountingDisposition.ConservativeCharge : AccountingDisposition.Unresolved, axis.Item1.Disposition);
            Assert.Equal(axis.Item2 ?? axis.Item3, axis.Item1.Amount);
        }
        Assert.Equal((input ?? 0) + (stops ? 0 : 10), accounting.Input.MeasuredTokens);
        Assert.Equal((output ?? 0) + (stops ? 0 : 5), accounting.Output.MeasuredTokens);
        Assert.Equal(0, accounting.Input.ReservedTokens); Assert.Equal(0, accounting.Output.ReservedTokens);
        Assert.Equal(input is null && policy == UnknownUsagePolicy.ConservativeCharge ? 5 : 0, accounting.Input.ConservativeChargeTokens);
        Assert.Equal(output is null && policy == UnknownUsagePolicy.ConservativeCharge ? 4 : 0, accounting.Output.ConservativeChargeTokens);
        Assert.Equal(input is null && policy != UnknownUsagePolicy.ConservativeCharge ? 5 : 0, accounting.Input.UnresolvedTokens);
        Assert.Equal(output is null && policy != UnknownUsagePolicy.ConservativeCharge ? 4 : 0, accounting.Output.UnresolvedTokens);
        Assert.True(hooks.Settlements[^1].Accounting!.Matches(accounting));
        Assert.Equal(5, hooks.Exposures[0].Accounting!.Input.ReservedTokens); Assert.Equal(4, hooks.Exposures[0].Accounting!.Output.ReservedTokens);
        if (!stops)
        {
            Assert.Equal(observation.LogicalCallId, result.Usage.Attempts[1].LogicalCallId);
            Assert.NotEqual(observation.PhysicalAttemptId, result.Usage.Attempts[1].PhysicalAttemptId);
            Assert.Equal(2, result.Usage.Attempts[1].AttemptNumber);
            Assert.True(hooks.Exposures[1].Accounting!.Attempts[0].Matches(accounting.Attempts[0]));
            Assert.Equal(5, hooks.Exposures[1].Accounting!.Input.ReservedTokens);
        }
        Assert.DoesNotContain("restricted-", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(503, 0, 1, false)] [InlineData(503, 1, 1, false)]
    [InlineData(503, 2, 2, true)] [InlineData(429, 2, 2, true)]
    [InlineData(401, 3, 1, false)] [InlineData(501, 3, 1, false)]
    [InlineData(503, 3, 3, false)]
    public async Task ActualClassificationAndFinitePolicyControlPhysicalSends(int status, int maximum, int sends, bool succeeds)
    {
        var count = 0;
        using var handler = new FakeHandler((_, _) => Task.FromResult(++count == 2 && succeeds
            ? AdapterFixture.Http(AdapterFixture.Response()) : AdapterFixture.Http(MeasuredError, (HttpStatusCode)status)));
        using var provider = AdapterFixture.Provider(handler);
        var result = await Execute(RuntimeAgentFactory.Create(new(provider, [], new RuntimeHooks())),
            Request(new(retryPolicy: maximum == 0 ? null : new(maximum))), candidate: true);
        Assert.Equal(succeeds ? AgentTerminationReason.Completed : AgentTerminationReason.Failed, result.Reason);
        Assert.Equal(sends, handler.Sends); Assert.Equal(sends, result.Usage!.Attempts.Count);
        Assert.Equal(succeeds ? 1 : 0, result.CompletedWorkUnits);
        Assert.Equal(succeeds ? 13 : 3 * sends, result.Usage.InputTokens.ObservedTokens);
        Assert.Single(result.Usage.Attempts.Select(a => a.LogicalCallId).Distinct());
        Assert.Equal(Enumerable.Range(1, sends), result.Usage.Attempts.Select(a => a.AttemptNumber));
    }

    [Theory]
    [InlineData("malformed-error", 10)] [InlineData("unreadable-error", 10)]
    [InlineData("invalid-model", 13)] [InlineData("invalid-finish", 13)]
    public async Task LaterInvalidHttpContentKeepsSafeFactsWithoutEffectsOrRetry(string shape, int measured)
    {
        var counter = new CounterCapability(); var sends = 0;
        using var handler = new FakeHandler((_, _) => Task.FromResult(++sends == 1
            ? AdapterFixture.Http(AdapterFixture.Response(null, "tool", "tool_calls", calls: [AdapterFixture.Call("first", "counter", IntegrationFixture.CounterArguments)]))
            : shape == "malformed-error" ? AdapterFixture.Http("{\"usage\":", HttpStatusCode.ServiceUnavailable)
            : shape == "unreadable-error" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new InterruptedContent() }
            : AdapterFixture.Http(AdapterFixture.Response("rejected", "bad-content", shape == "invalid-finish" ? "length" : "stop",
                model: shape == "invalid-model" ? "foreign-model" : "deepseek-flash", usage: new { prompt_tokens = 3, completion_tokens = 2 }))));
        using var provider = AdapterFixture.Provider(handler); var hooks = new RuntimeHooks();
        var agent = RuntimeAgentFactory.Create(new(provider, [new(new CounterTool(maximumResultBytes: 64), counter)], hooks), IntegrationFixture.Options());
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s));
        var result = (await CandidateConsumer.RunAsync((ICandidateAgent)agent, IntegrationFixture.Request(usageLimits:
            new(maximumPhysicalDispatches: 4, accountingPolicy: new(new(5, 4), 50, 50, UnknownUsagePolicy.ContinueUnknown), retryPolicy: new(3))), host)).Result;
        Assert.Equal(AgentTerminationReason.Failed, result.Outcome.Reason); Assert.Equal(2, handler.Sends);
        Assert.Equal(1, counter.Effects); Assert.Equal(2, counter.Total); Assert.Empty(host.Submissions); Assert.Empty(result.Receipts);
        Assert.Equal(1, result.Outcome.CompletedWorkUnits); Assert.Equal(2, result.Outcome.Usage!.Attempts.Count);
        Assert.Equal(measured, result.Outcome.Usage.Accounting!.Input.MeasuredTokens);
        Assert.Equal(shape is "malformed-error" or "unreadable-error" ? null : (long?)3, result.Outcome.Usage.Attempts[1].Usage.InputTokens);
        Assert.All(result.Outcome.Usage.Attempts, a => Assert.Equal(DispatchExposure.Dispatched, a.Exposure));
        Assert.True(hooks.Settlements[^1].Accounting!.Matches(result.Outcome.Usage.Accounting));
    }

    [Theory]
    [InlineData(1, 4, AgentTerminationReason.ResourceLimit)]
    [InlineData(2, 3, AgentTerminationReason.ResourceLimit)]
    [InlineData(2, 4, AgentTerminationReason.Partial)]
    public async Task KnownLimitsTakePriorityOverMissingRequiredMeasurements(int physical, int input, AgentTerminationReason reason)
    {
        using var handler = FakeHandler.Reply("{\"usage\":{\"prompt_tokens\":3}}", HttpStatusCode.ServiceUnavailable);
        using var provider = AdapterFixture.Provider(handler); var hooks = new RuntimeHooks();
        var result = await Execute(RuntimeAgentFactory.Create(new(provider, [], hooks)), Request(new(
            maximumPhysicalDispatches: physical, inputTokenThreshold: input, outputTokenThreshold: 2, retryPolicy: new(2))), true);
        Assert.Equal(reason, result.Reason); Assert.Equal(1, handler.Sends); Assert.Single(hooks.Exposures);
        Assert.Equal(3, result.Usage!.Attempts[0].Usage.InputTokens); Assert.Null(result.Usage.Attempts[0].Usage.OutputTokens);
    }

    [Fact]
    public async Task MissingMeasurementWithoutRequiredComparisonDoesNotUnconditionallyStop()
    {
        var sends = 0;
        using var handler = new FakeHandler((_, _) => Task.FromResult(++sends == 1
            ? AdapterFixture.Http("{}", HttpStatusCode.ServiceUnavailable) : AdapterFixture.Http(AdapterFixture.Response())));
        using var provider = AdapterFixture.Provider(handler);
        var result = await Execute(RuntimeAgentFactory.Create(new(provider, [], new RuntimeHooks())),
            Request(new(accountingPolicy: new(new(5, 4)), retryPolicy: new(2))), true);
        Assert.Equal(AgentTerminationReason.Completed, result.Reason); Assert.Equal(2, handler.Sends);
        Assert.Null(result.Usage!.Attempts[0].Usage.InputTokens);
        Assert.Equal(AccountingDisposition.Unresolved, result.Usage.Accounting!.Attempts[0].Input.Disposition);
    }

    public static IEnumerable<object[]> Boundaries()
    {
        foreach (var candidate in new[] { false, true })
        foreach (var axis in new[] { "physical", "inventory", "input", "output", "input-allowance", "output-allowance" })
        foreach (var allowed in new[] { false, true }) yield return [candidate, axis, allowed];
    }
    [Theory, MemberData(nameof(Boundaries))]
    public async Task ExactAdmissionBoundaryAllowsLastRetryAndDeniesBeforeNextReservation(bool candidate, string axis, bool allowed)
    {
        var sends = 0;
        using var handler = new FakeHandler((_, _) => Task.FromResult(++sends == 1
            ? AdapterFixture.Http(MeasuredError, HttpStatusCode.ServiceUnavailable) : AdapterFixture.Http(AdapterFixture.Response())));
        using var provider = AdapterFixture.Provider(handler); var hooks = new RuntimeHooks();
        var limits = new AgentUsageLimits(maximumLogicalCalls: 1, maximumPhysicalDispatches: axis == "physical" && !allowed ? 1 : 2,
            inputTokenThreshold: axis == "input" ? allowed ? 4 : 3 : null,
            outputTokenThreshold: axis == "output" ? allowed ? 3 : 2 : null,
            accountingPolicy: new(new(5, 4), axis == "input-allowance" ? allowed ? 8 : 7 : 40,
                axis == "output-allowance" ? allowed ? 6 : 5 : 40), retryPolicy: new(2));
        var agent = RuntimeAgentFactory.Create(new(provider, [], hooks), IntegrationFixture.Options(maximumAttempts: axis == "inventory" && !allowed ? 1 : 2));
        var result = await Execute(agent, Request(limits), candidate);
        Assert.Equal(allowed ? AgentTerminationReason.Completed : AgentTerminationReason.ResourceLimit, result.Reason);
        Assert.Equal(allowed ? 2 : 1, handler.Sends); Assert.Equal(handler.Sends, hooks.Exposures.Count);
        Assert.Equal(handler.Sends, result.Usage!.Attempts.Count); Assert.Single(result.Usage.Attempts.Select(a => a.LogicalCallId).Distinct());
        Assert.Equal(allowed ? 1 : 0, result.CompletedWorkUnits); Assert.Equal(0, result.Usage.Accounting!.Input.ReservedTokens);
    }

    [Theory]
    [InlineData("stop")] [InlineData("missing")] [InlineData("accounting")] [InlineData("deny-retry")]
    [InlineData("stale-accounting")] [InlineData("continue")]
    public async Task ActualFailedAttemptMustCloseAndRetryNeedsFreshPermission(string mode)
    {
        var sends = 0;
        using var handler = new FakeHandler((_, _) => Task.FromResult(++sends == 1
            ? AdapterFixture.Http(MeasuredError, HttpStatusCode.ServiceUnavailable) : AdapterFixture.Http(AdapterFixture.Response())));
        using var provider = AdapterFixture.Provider(handler);
        var emitted = new List<SettlementAcknowledgement?>();
        var hooks = new RuntimeHooks
        {
            After = (s, _) =>
            {
                SettlementAcknowledgement? receipt = mode switch
                {
                    "missing" => null,
                    "accounting" => new(s.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue, accounting: null),
                    "stale-accounting" => new(s.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue, s.Exposure.Accounting),
                    "stop" => new(s.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Stop, s.Accounting),
                    _ => RuntimeHooks.Continue(s),
                };
                emitted.Add(receipt);
                return ValueTask.FromResult(receipt);
            },
            Before = (e, _) => ValueTask.FromResult<ExposureAcknowledgement?>(mode == "deny-retry" && e.Attempt.AttemptNumber == 2
                ? new(e, RuntimeHookStatus.Acknowledged, ExposureDecision.Deny) : RuntimeHooks.Permit(e)),
        };
        var result = await Execute(RuntimeAgentFactory.Create(new(provider, [], hooks)),
            Request(new(accountingPolicy: new(new(5, 4), 40, 40), retryPolicy: new(2))), true);
        Assert.Equal(hooks.Settlements.Count, emitted.Count);
        // Assert outside the hook: a constructor or assertion exception swallowed by closure is not mismatch evidence.
        if (mode == "missing") Assert.Null(emitted[0]);
        else
        {
            Assert.NotNull(emitted[0]);
            Assert.Equal(mode is "accounting" or "stale-accounting" ? RuntimeStop.SettlementMismatch
                : mode == "stop" ? RuntimeStop.HostStopped : RuntimeStop.None, emitted[0]!.Assess(hooks.Settlements[0]));
            Assert.Equal(mode == "stop" ? RuntimeContinuation.Stop : RuntimeContinuation.Continue, emitted[0]!.Continuation);
        }
        if (mode == "stale-accounting")
        {
            Assert.NotNull(emitted[0]!.Accounting);
            Assert.Equal(5, emitted[0]!.Accounting!.Input.ReservedTokens);
            Assert.Equal(0, hooks.Settlements[0].Accounting!.Input.ReservedTokens);
        }
        var continues = mode == "continue";
        Assert.Equal(continues ? AgentTerminationReason.Completed : mode == "deny-retry" ? AgentTerminationReason.Partial : AgentTerminationReason.Failed, result.Reason);
        Assert.Equal(continues ? 2 : 1, handler.Sends); Assert.Equal(continues ? 1 : 0, result.CompletedWorkUnits);
        Assert.Equal(mode == "deny-retry" || continues ? 2 : 1, result.Usage!.Attempts.Count);
        Assert.Equal(continues ? 13 : 3, result.Usage.Accounting!.Input.MeasuredTokens); Assert.Equal(0, result.Usage.Accounting.Input.ReservedTokens);
        Assert.All(result.Usage.Accounting.Attempts, a => Assert.True(a.IsFinalized));
        if (mode == "deny-retry")
        {
            Assert.Equal(2, hooks.Exposures.Count); Assert.Equal(2, hooks.Settlements.Count);
            Assert.Equal(DispatchExposure.NotDispatched, result.Usage.Attempts[1].Exposure);
            Assert.Equal(AccountingDisposition.Released, result.Usage.Accounting.Attempts[1].Input.Disposition);
        }
    }

    private sealed class InterruptedContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(new HttpRequestException(HttpRequestError.ResponseEnded, "restricted-body-loss-canary"));
        protected override bool TryComputeLength(out long length) { length = 100; return true; }
    }
}

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
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.RuntimeIntegration;

public sealed class RetryCompositionTests
{
    [Theory]
    [InlineData(IntegrationFixture.CorrectionA)] [InlineData(IntegrationFixture.CorrectionB)]
    public async Task MixedRetriesPreserveWireIdentityAccountingAndEarlierEffects(string correction)
    {
        var counter = new CounterCapability(); var transform = new TransformCapability();
        var bodies = new List<string>();
        ScriptedCandidateHost? host = null;
        using var handler = new FakeHandler(async (message, token) =>
        {
            IntegrationFixture.AssertTransport(message);
            bodies.Add(await message.Content!.ReadAsStringAsync(token));
            var number = bodies.Count;
            if (number is 3 or 6)
            {
                // Both a real tool effect and an acknowledged Host effect precede the failure.
                Assert.Equal(number == 3 ? 1 : 2, counter.Effects); Assert.Equal(1, transform.Effects);
                Assert.Equal(1, host!.Effects);
                return AdapterFixture.Http(JsonSerializer.Serialize(new { error = "failed-payload-canary", usage = IntegrationFixture.Usage(number) }),
                    number == 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.TooManyRequests);
            }
            var body = number switch
            {
                1 => AdapterFixture.Response(null, "", "tool_calls", calls:
                    [AdapterFixture.Call("counter-first", "counter", IntegrationFixture.CounterArguments),
                     AdapterFixture.Call("transform-first", "transform", IntegrationFixture.TransformArguments)], usage: IntegrationFixture.Usage(number)),
                2 => AdapterFixture.Response("accepted-before-retry", "first-final-reasoning", usage: IntegrationFixture.Usage(number)),
                4 => AdapterFixture.Response(null, "retry-tool-reasoning", "tool_calls",
                    calls: [AdapterFixture.Call("counter-second", "counter", IntegrationFixture.CounterArgumentsSecond)], usage: IntegrationFixture.Usage(number)),
                5 => AdapterFixture.Response("rejected-candidate", "rejected-reasoning", usage: IntegrationFixture.Usage(number)),
                // The result depends on the actual replayed correction, not merely the response index.
                7 => AdapterFixture.Response("repaired-" + IntegrationFixture.Parse(bodies[^1]).GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString(),
                    "retry-final-reasoning", usage: IntegrationFixture.Usage(number)),
                8 => AdapterFixture.Response("closing-candidate", "closing-reasoning", usage: IntegrationFixture.Usage(number)),
                _ => throw new InvalidOperationException("Unexpected extra transport send."),
            };
            return AdapterFixture.Http(body);
        });
        using var adapter = AdapterFixture.Provider(handler); var provider = new ObservedProvider(adapter);
        var hooks = new RuntimeHooks();
        var agent = RuntimeAgentFactory.Create(new(provider,
            [new(new CounterTool(maximumResultBytes: 64), counter), new(new TransformTool(maximumResultBytes: 64), transform)], hooks),
            IntegrationFixture.Options(maximumAttempts: 8));
        host = new ScriptedCandidateHost((submission, _) =>
        {
            var count = host!.Submissions.Count;
            if (count == 2) return CandidateFixture.Feedback(submission, CandidateDecision.Reject, CandidateContinuation.Continue, correction);
            host.ApplyEffect();
            return CandidateFixture.Feedback(submission, CandidateDecision.Accept, count == 4 ? CandidateContinuation.End : CandidateContinuation.Continue);
        });
        var request = IntegrationFixture.Request(units: 6, submissions: 4, repairs: 1, continuations: 2,
            usageLimits: new(maximumLogicalCalls: 6, maximumPhysicalDispatches: 8, maximumToolInvocations: 3,
                accountingPolicy: new(new(120, 25), 1000, 250), retryPolicy: new(2)));
        var result = (await CandidateConsumer.RunAsync((ICandidateAgent)agent, request, host)).Result;

        Assert.Equal(CandidateStopReason.Completed, result.StopReason); Assert.Equal(6, result.Outcome.CompletedWorkUnits);
        Assert.Equal(8, handler.Sends); Assert.Equal(4, host.Submissions.Count); Assert.Equal(3, host.Effects);
        Assert.Equal(4, result.Receipts.Count); Assert.Equal(1, result.RepairsAdmitted); Assert.Equal(2, result.ContinuationsAdmitted);
        Assert.Equal("repaired-" + correction, host.Submissions[2].Payload);
        Assert.Equal(host.Submissions[1].SubmissionId, host.Submissions[2].RepairsSubmissionId);
        Assert.Equal(4, host.Submissions.Select(s => s.SubmissionId).Distinct().Count());
        Assert.Equal(2, counter.Effects); Assert.Equal(5, counter.Total); Assert.Equal(1, transform.Effects);
        Assert.Equal(3, result.Outcome.Usage!.ToolInvocations!.Invoked);
        Assert.Equal(0, result.Outcome.Usage.ToolInvocations.ReservedUnstarted);
        var attempts = result.Outcome.Usage.Attempts;
        Assert.Equal(8, attempts.Count); Assert.Equal(6, attempts.Select(a => a.LogicalCallId).Distinct().Count());
        Assert.Equal(8, attempts.Select(a => a.PhysicalAttemptId).Distinct().Count());
        Assert.Equal(new[] { 1, 1, 1, 2, 1, 1, 2, 1 }, attempts.Select(a => a.AttemptNumber));
        Assert.Equal(8, hooks.Exposures.Count); Assert.Equal(8, hooks.Settlements.Count);
        for (var i = 0; i < 8; i++)
        {
            var actual = provider.Requests[i].Attempt; var entry = attempts[i];
            Assert.Equal(actual.ExecutionId, entry.ExecutionId); Assert.Equal(actual.LogicalCallId, entry.LogicalCallId);
            Assert.Equal(actual.PhysicalAttemptId, entry.PhysicalAttemptId); Assert.Equal(actual.AttemptNumber, entry.AttemptNumber);
            Assert.True(actual.Matches(hooks.Exposures[i].Attempt)); Assert.True(actual.Matches(hooks.Settlements[i].Exposure.Attempt));
            Assert.Equal(DispatchExposure.Dispatched, entry.Exposure); Assert.Equal(101 + i, entry.Usage.InputTokens); Assert.Equal(11 + i, entry.Usage.OutputTokens);
            var reserved = hooks.Exposures[i].Accounting!; var settled = hooks.Settlements[i].Accounting!;
            Assert.Equal(i + 1, reserved.Attempts.Count); Assert.Equal(120, reserved.Input.ReservedTokens); Assert.Equal(25, reserved.Output.ReservedTokens);
            Assert.All(settled.Attempts, a => Assert.True(a.IsFinalized)); Assert.Equal(0, settled.Input.ReservedTokens);
            Assert.True(settled.Attempts[i].MatchesObservation(entry, UnknownUsagePolicy.Stop));
        }
        Assert.Equal(836, result.Outcome.Usage.Accounting!.Input.MeasuredTokens);
        Assert.Equal(116, result.Outcome.Usage.Accounting.Output.MeasuredTokens);
        Assert.True(hooks.Settlements[^1].Accounting!.Matches(result.Outcome.Usage.Accounting));
        foreach (var pair in new[] { (2, 3), (5, 6) })
        {
            var before = provider.Requests[pair.Item1]; var retry = provider.Requests[pair.Item2];
            Assert.Equal(before.Attempt.LogicalCallId, retry.Attempt.LogicalCallId);
            Assert.NotSame(before.Observation, retry.Observation); Assert.Same(before.Bounds, retry.Bounds);
            Assert.Equal(before.Inputs, retry.Inputs); Assert.Equal(before.Tools, retry.Tools);
            Assert.Same(before.Continuation, retry.Continuation); Assert.Equal(before.RequiredCapabilities, retry.RequiredCapabilities);
            Assert.True(before.Scope.Matches(retry.Scope)); Assert.Equal(bodies[pair.Item1], bodies[pair.Item2]);
        }
        Assert.True(provider.Requests[3].Attempt.Matches(provider.Requests[4].Continuation!.Origin));
        Assert.True(provider.Requests[6].Attempt.Matches(provider.Requests[7].Continuation!.Origin));
        var roots = bodies.Select(IntegrationFixture.Parse).ToArray(); Assert.All(roots, IntegrationFixture.AssertControlFields);
        var messages = roots[^1].GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal("", messages[2].GetProperty("reasoning_content").GetString());
        Assert.Equal(IntegrationFixture.TransformArguments, messages[2].GetProperty("tool_calls")[1].GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("counter-first", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("{\"total\":2}", messages[3].GetProperty("content").GetString());
        Assert.Equal("transform-first", messages[4].GetProperty("tool_call_id").GetString());
        Assert.Equal("{\"text\":\"TRANSFORM-CANARY\"}", messages[4].GetProperty("content").GetString());
        Assert.Equal("retry-final-reasoning", messages[^1].GetProperty("reasoning_content").GetString());
        Assert.All(bodies, body => Assert.DoesNotContain("failed-payload-canary", body));
        var ordinary = JsonSerializer.Serialize(result);
        foreach (var canary in new[] { AdapterFixture.Credential, "failed-payload-canary", "retry-tool-reasoning", correction }) Assert.DoesNotContain(canary, ordinary);
    }

    [Theory]
    [InlineData(3, true)] [InlineData(2, false)]
    public async Task RetryConsumesNoToolAllowanceAndLaterBatchIsAdmittedAsAWhole(int allowance, bool allowed)
    {
        var counter = new CounterCapability(); var transform = new TransformCapability(); var sends = 0;
        using var handler = new FakeHandler((_, _) => Task.FromResult(++sends switch
        {
            1 => AdapterFixture.Http(AdapterFixture.Response(null, "first", "tool_calls", calls: [AdapterFixture.Call("one", "counter", IntegrationFixture.CounterArguments)])),
            2 => AdapterFixture.Http("{\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2}}", HttpStatusCode.ServiceUnavailable),
            3 => AdapterFixture.Http(AdapterFixture.Response(null, "second", "tool_calls", calls:
                [AdapterFixture.Call("two", "counter", IntegrationFixture.CounterArgumentsSecond), AdapterFixture.Call("three", "transform", IntegrationFixture.TransformArguments)])),
            4 => AdapterFixture.Http(AdapterFixture.Response()),
            _ => throw new InvalidOperationException(),
        }));
        using var provider = AdapterFixture.Provider(handler);
        var agent = RuntimeAgentFactory.Create(new(provider,
            [new(new CounterTool(maximumResultBytes: 64), counter), new(new TransformTool(maximumResultBytes: 64), transform)], new RuntimeHooks()), IntegrationFixture.Options());
        var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s));
        var result = (await CandidateConsumer.RunAsync((ICandidateAgent)agent,
            IntegrationFixture.Request(usageLimits: new(maximumToolInvocations: allowance, retryPolicy: new(2))), host)).Result;
        Assert.Equal(allowed ? AgentTerminationReason.Completed : AgentTerminationReason.ResourceLimit, result.Outcome.Reason);
        Assert.Equal(allowed ? 4 : 3, handler.Sends); Assert.Equal(allowed ? 3 : 2, result.Outcome.CompletedWorkUnits);
        Assert.Equal(allowed ? 2 : 1, counter.Effects); Assert.Equal(allowed ? 5 : 2, counter.Total);
        Assert.Equal(allowed ? 1 : 0, transform.Effects); Assert.Equal(allowed ? 1 : 0, host.Submissions.Count);
        Assert.Equal(allowed ? 3 : 1, result.Outcome.Usage!.ToolInvocations!.Invoked);
        Assert.Equal(0, result.Outcome.Usage.ToolInvocations.ReservedUnstarted);
    }
}

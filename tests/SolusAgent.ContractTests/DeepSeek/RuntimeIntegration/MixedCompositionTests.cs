using System.Text.Json;
using CustomTools;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ContractTests.DeepSeek.Adapter;
using SolusAgent.ContractTests.Runtime.Candidates;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.RuntimeIntegration;

public sealed class MixedCompositionTests
{
    [Fact]
    public async Task ContinuousMixedRunReplaysExactHistoryAndChargesOneFollowOnAcrossToolTurns()
    {
        var counter = new CounterTool(maximumResultBytes: 64); var counterCapability = new CounterCapability();
        var transform = new TransformTool(maximumResultBytes: 64); var transformCapability = new TransformCapability();
        var bodies = new List<string>();
        var wire = new Queue<string>([
            AdapterFixture.Response(null, "", "tool_calls", calls:
                [AdapterFixture.Call("call-1a", "counter", IntegrationFixture.CounterArguments),
                 AdapterFixture.Call("call-1b", "transform", IntegrationFixture.TransformArguments)], usage: IntegrationFixture.Usage(1)),
            AdapterFixture.Response(null, "turn-two-reasoning", "tool_calls",
                calls: [AdapterFixture.Call("call-2", "counter", IntegrationFixture.CounterArgumentsSecond)], usage: IntegrationFixture.Usage(2)),
            AdapterFixture.Response("candidate-one", "rejected-final-reasoning", usage: IntegrationFixture.Usage(3)),
            AdapterFixture.Response(null, "repair-reasoning", "tool_calls",
                calls: [AdapterFixture.Call("call-3", "transform", IntegrationFixture.TransformRepairArguments)], usage: IntegrationFixture.Usage(4)),
            AdapterFixture.Response("candidate-two", "accepted-final-reasoning", usage: IntegrationFixture.Usage(5)),
            AdapterFixture.Response("candidate-three", "closing-reasoning", usage: IntegrationFixture.Usage(6)),
        ]);
        using var handler = new FakeHandler(async (message, token) =>
        {
            IntegrationFixture.AssertTransport(message);
            bodies.Add(await message.Content!.ReadAsStringAsync(token));
            return AdapterFixture.Http(wire.Dequeue());
        });
        using var provider = AdapterFixture.Provider(handler);
        var hooks = new RuntimeHooks(); var options = IntegrationFixture.Options();
        var configuration = new SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration(provider,
            [new RuntimeToolRegistration(counter, counterCapability), new RuntimeToolRegistration(transform, transformCapability)], hooks);
        IAgent agent = RuntimeAgentFactory.Create(configuration, options);
        var candidateAgent = Assert.IsAssignableFrom<ICandidateAgent>(agent);
        Assert.True(RuntimeAgentFactory.Describe(configuration, options).RequireContinuation);
        ScriptedCandidateHost? host = null;
        host = new ScriptedCandidateHost((submission, _) =>
        {
            var index = host!.Submissions.Count;
            if (index == 1)
                return CandidateFixture.Feedback(submission, CandidateDecision.Reject, CandidateContinuation.Continue, IntegrationFixture.CorrectionA);
            host.ApplyEffect();
            return index == 2
                ? CandidateFixture.Feedback(submission, CandidateDecision.Accept, CandidateContinuation.Continue)
                : CandidateFixture.Feedback(submission);
        });
        var request = IntegrationFixture.Request(units: 6, submissions: 3, repairs: 1, continuations: 1);
        var observed = await CandidateConsumer.RunAsync(candidateAgent, request, host);
        var result = observed.Result;

        // One public production run: six accepted model turns, three submissions and one repair across two tool rounds.
        Assert.Equal(CandidateStopReason.Completed, result.StopReason);
        Assert.Equal(AgentTerminationReason.Completed, result.Outcome.Reason);
        Assert.Equal(6, result.Outcome.CompletedWorkUnits);
        Assert.Equal(6, handler.Sends);
        Assert.Equal(1, result.RepairsAdmitted); Assert.Equal(1, result.ContinuationsAdmitted);
        Assert.Equal(2, result.AcceptedCount); Assert.Equal(3, result.Receipts.Count);
        Assert.Equal(3, host.Submissions.Count); Assert.Equal(2, host.Effects);
        Assert.Equal(6, hooks.Exposures.Count); Assert.Equal(6, hooks.Settlements.Count);
        Assert.Equal(2, counterCapability.Effects); Assert.Equal(5, counterCapability.Total);
        Assert.Equal(2, transformCapability.Effects);
        Assert.NotEmpty(observed.Progress); Assert.All(observed.Progress, report => Assert.Equal(request.Execution.ExecutionId, report.ExecutionId));

        // Distinct candidate identities with exactly one repair backlink to the rejected predecessor.
        Assert.Equal("candidate-one", host.Submissions[0].Payload);
        Assert.Equal("candidate-two", host.Submissions[1].Payload);
        Assert.Equal("candidate-three", host.Submissions[2].Payload);
        Assert.Null(host.Submissions[0].RepairsSubmissionId);
        Assert.Equal(host.Submissions[0].SubmissionId, host.Submissions[1].RepairsSubmissionId);
        Assert.Null(host.Submissions[2].RepairsSubmissionId);
        Assert.Equal(3, host.Submissions.Select(submission => submission.SubmissionId).Distinct().Count());
        Assert.All(result.Receipts, receipt => Assert.Equal(CandidateAcknowledgement.Acknowledged, receipt.Acknowledgement));
        Assert.Equal(CandidateDecision.Reject, result.Receipts[0].Decision); Assert.Equal(CandidateContinuation.Continue, result.Receipts[0].Continuation);
        Assert.Equal(CandidateDecision.Accept, result.Receipts[1].Decision); Assert.Equal(CandidateContinuation.Continue, result.Receipts[1].Continuation);
        Assert.Equal(CandidateDecision.Accept, result.Receipts[2].Decision); Assert.Equal(CandidateContinuation.End, result.Receipts[2].Continuation);

        // Per-attempt numeric usage stays retained independently of payloads and effects.
        Assert.Equal(UsageInventoryCoverage.Complete, result.Outcome.Usage!.Coverage);
        var attempts = result.Outcome.Usage.Attempts;
        Assert.Equal(6, attempts.Count);
        for (var i = 0; i < attempts.Count; i++)
        {
            Assert.Equal(DispatchExposure.Dispatched, attempts[i].Exposure);
            Assert.Equal(101 + i, attempts[i].Usage.InputTokens);
            Assert.Equal(11 + i, attempts[i].Usage.OutputTokens);
        }

        // Every later request retains all earlier turns and both registered tools.
        var parsed = bodies.Select(IntegrationFixture.Parse).ToArray();
        Assert.Equal(new[] { 2, 5, 7, 9, 11, 12 }, parsed.Select(root => root.GetProperty("messages").GetArrayLength()));
        foreach (var root in parsed) IntegrationFixture.AssertControlFields(root);
        for (var i = 1; i < parsed.Length; i++)
        {
            var previous = parsed[i - 1].GetProperty("messages").EnumerateArray().ToArray();
            var current = parsed[i].GetProperty("messages").EnumerateArray().ToArray();
            for (var message = 0; message < previous.Length; message++)
                Assert.Equal(previous[message].GetRawText(), current[message].GetRawText());
        }

        var messages = parsed[^1].GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal(IntegrationFixture.Instruction, messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal(IntegrationFixture.InputData, messages[1].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[2].GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, messages[2].GetProperty("content").ValueKind);
        Assert.Equal(JsonValueKind.String, messages[2].GetProperty("reasoning_content").ValueKind);
        Assert.Equal("", messages[2].GetProperty("reasoning_content").GetString());
        var firstCalls = messages[2].GetProperty("tool_calls").EnumerateArray().ToArray();
        Assert.Equal(2, firstCalls.Length);
        Assert.Equal("call-1a", firstCalls[0].GetProperty("id").GetString());
        Assert.Equal("function", firstCalls[0].GetProperty("type").GetString());
        Assert.Equal("counter", firstCalls[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(IntegrationFixture.CounterArguments, firstCalls[0].GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("call-1b", firstCalls[1].GetProperty("id").GetString());
        Assert.Equal("transform", firstCalls[1].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(IntegrationFixture.TransformArguments, firstCalls[1].GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Equal("call-1a", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("{\"total\":2}", messages[3].GetProperty("content").GetString());
        Assert.Equal("tool", messages[4].GetProperty("role").GetString());
        Assert.Equal("call-1b", messages[4].GetProperty("tool_call_id").GetString());
        Assert.Equal("{\"text\":\"TRANSFORM-CANARY\"}", messages[4].GetProperty("content").GetString());
        Assert.Equal("turn-two-reasoning", messages[5].GetProperty("reasoning_content").GetString());
        var secondCalls = messages[5].GetProperty("tool_calls").EnumerateArray().ToArray();
        Assert.Single(secondCalls); Assert.Equal("call-2", secondCalls[0].GetProperty("id").GetString());
        Assert.Equal(IntegrationFixture.CounterArgumentsSecond, secondCalls[0].GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("call-2", messages[6].GetProperty("tool_call_id").GetString());
        Assert.Equal("{\"total\":5}", messages[6].GetProperty("content").GetString());
        Assert.Equal("candidate-one", messages[7].GetProperty("content").GetString());
        Assert.Equal("rejected-final-reasoning", messages[7].GetProperty("reasoning_content").GetString());
        Assert.False(messages[7].TryGetProperty("tool_calls", out _));
        Assert.Equal("user", messages[8].GetProperty("role").GetString());
        Assert.Equal(IntegrationFixture.CorrectionA, messages[8].GetProperty("content").GetString());
        Assert.Equal("repair-reasoning", messages[9].GetProperty("reasoning_content").GetString());
        var repairCalls = messages[9].GetProperty("tool_calls").EnumerateArray().ToArray();
        Assert.Single(repairCalls); Assert.Equal("call-3", repairCalls[0].GetProperty("id").GetString());
        Assert.Equal("transform", repairCalls[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(IntegrationFixture.TransformRepairArguments, repairCalls[0].GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("call-3", messages[10].GetProperty("tool_call_id").GetString());
        Assert.Equal("{\"text\":\"REPAIR-TRANSFORM-CANARY\"}", messages[10].GetProperty("content").GetString());
        Assert.Equal("candidate-two", messages[11].GetProperty("content").GetString());
        Assert.Equal("accepted-final-reasoning", messages[11].GetProperty("reasoning_content").GetString());
        // The rejected correction is applied exactly once despite the intervening tool round.
        Assert.Single(messages, message => message.GetProperty("role").GetString() == "user"
            && message.GetProperty("content").GetString() == IntegrationFixture.CorrectionA);

        var ordinary = JsonSerializer.Serialize(result) + JsonSerializer.Serialize(observed.Progress)
            + JsonSerializer.Serialize(RuntimeAgentFactory.Describe(configuration, options));
        foreach (var canary in new[] { AdapterFixture.Credential, IntegrationFixture.CorrectionA, IntegrationFixture.InputData,
            "rejected-final-reasoning", "candidate-one", "TRANSFORM-CANARY", "transform-canary" })
            Assert.DoesNotContain(canary, ordinary);
    }

    [Fact]
    public async Task HostCorrectionDataCausallyChangesTheFollowOnRequestAndResponse()
    {
        var (bodiesA, resultA, hostA) = await RunCorrectionVariant(IntegrationFixture.CorrectionA);
        var (bodiesB, resultB, hostB) = await RunCorrectionVariant(IntegrationFixture.CorrectionB);

        // The response selection derives from the captured repair data rather than a canned result.
        Assert.Equal("repair-echo:" + IntegrationFixture.CorrectionA, hostA.Submissions[1].Payload);
        Assert.Equal("repair-echo:" + IntegrationFixture.CorrectionB, hostB.Submissions[1].Payload);
        Assert.Equal(CandidateStopReason.Completed, resultA.StopReason);
        Assert.Equal(CandidateStopReason.Completed, resultB.StopReason);
        Assert.Equal(1, resultA.RepairsAdmitted); Assert.Equal(0, resultA.ContinuationsAdmitted);

        var wireA = IntegrationFixture.Parse(bodiesA[1]); var wireB = IntegrationFixture.Parse(bodiesB[1]);
        foreach (var field in new[] { "model", "stream", "thinking", "reasoning_effort", "max_tokens", "tools" })
            Assert.Equal(wireA.GetProperty(field).GetRawText(), wireB.GetProperty(field).GetRawText());
        var messagesA = wireA.GetProperty("messages").EnumerateArray().ToArray();
        var messagesB = wireB.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(4, messagesA.Length); Assert.Equal(messagesA.Length, messagesB.Length);
        for (var message = 0; message < 3; message++)
            Assert.Equal(messagesA[message].GetRawText(), messagesB[message].GetRawText());
        Assert.Equal("user", messagesA[3].GetProperty("role").GetString());
        Assert.Equal(IntegrationFixture.CorrectionA, messagesA[3].GetProperty("content").GetString());
        Assert.Equal(IntegrationFixture.CorrectionB, messagesB[3].GetProperty("content").GetString());
        Assert.Single(messagesA, message => message.GetProperty("role").GetString() == "user"
            && message.GetProperty("content").GetString() == IntegrationFixture.CorrectionA);
        Assert.Single(messagesB, message => message.GetProperty("role").GetString() == "user"
            && message.GetProperty("content").GetString() == IntegrationFixture.CorrectionB);
    }

    private static async Task<(List<string> Bodies, CandidateExecutionResult Result, ScriptedCandidateHost Host)> RunCorrectionVariant(string correction)
    {
        var counter = new CounterTool(maximumResultBytes: 64); var counterCapability = new CounterCapability();
        var transform = new TransformTool(maximumResultBytes: 64); var transformCapability = new TransformCapability();
        var bodies = new List<string>(); var turn = 0;
        using var handler = new FakeHandler(async (message, token) =>
        {
            IntegrationFixture.AssertTransport(message);
            var body = await message.Content!.ReadAsStringAsync(token);
            bodies.Add(body); turn++;
            return AdapterFixture.Http(turn == 1
                ? AdapterFixture.Response("rejected-candidate", "ab-rejected-reasoning", usage: IntegrationFixture.Usage(1))
                : AdapterFixture.Response("repair-echo:" + ObservedCorrection(body), "ab-follow-reasoning", usage: IntegrationFixture.Usage(2)));
        });
        using var provider = AdapterFixture.Provider(handler);
        IAgent agent = RuntimeAgentFactory.Create(new(provider,
            [new RuntimeToolRegistration(counter, counterCapability), new RuntimeToolRegistration(transform, transformCapability)], new RuntimeHooks()),
            IntegrationFixture.Options());
        var candidateAgent = Assert.IsAssignableFrom<ICandidateAgent>(agent);
        ScriptedCandidateHost? host = null;
        host = new ScriptedCandidateHost((submission, _) => host!.Submissions.Count == 1
            ? CandidateFixture.Feedback(submission, CandidateDecision.Reject, CandidateContinuation.Continue, correction)
            : CandidateFixture.Feedback(submission));
        var observed = await CandidateConsumer.RunAsync(candidateAgent,
            IntegrationFixture.Request(units: 4, submissions: 2, repairs: 1, continuations: 0), host);
        return (bodies, observed.Result, host);
    }

    private static string ObservedCorrection(string body)
    {
        var messages = IntegrationFixture.Parse(body).GetProperty("messages").EnumerateArray().ToArray();
        var last = messages[^1];
        Assert.Equal("user", last.GetProperty("role").GetString());
        return last.GetProperty("content").GetString()!;
    }
}

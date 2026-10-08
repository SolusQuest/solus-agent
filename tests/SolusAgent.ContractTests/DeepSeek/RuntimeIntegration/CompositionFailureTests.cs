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

public sealed class CompositionFailureTests
{
    [Theory]
    [InlineData("incomplete-finish")] [InlineData("foreign-association")]
    public async Task RejectedPayloadAfterAcceptedToolTurnKeepsToolResultsAndAllUsage(string mode)
    {
        var counter = new CounterTool(maximumResultBytes: 64); var counterCapability = new CounterCapability();
        var transform = new TransformTool(maximumResultBytes: 64); var transformCapability = new TransformCapability();
        var wire = new Queue<string>([
            AdapterFixture.Response(null, "tool-turn-reasoning", "tool_calls",
                calls: [AdapterFixture.Call("call-1a", "counter", IntegrationFixture.CounterArguments)], usage: IntegrationFixture.Usage(1)),
            mode == "incomplete-finish"
                ? AdapterFixture.Response("truncated", "incomplete-reasoning", "length", usage: IntegrationFixture.Usage(2))
                : AdapterFixture.Response(null, "foreign-reasoning", "tool_calls",
                    calls: [AdapterFixture.Call("call-1a", "counter", IntegrationFixture.CounterArguments)], usage: IntegrationFixture.Usage(2)),
        ]);
        using var handler = new FakeHandler((message, _) =>
        {
            IntegrationFixture.AssertTransport(message);
            return Task.FromResult(AdapterFixture.Http(wire.Dequeue()));
        });
        using var provider = AdapterFixture.Provider(handler);
        IAgent agent = RuntimeAgentFactory.Create(new(provider,
            [new RuntimeToolRegistration(counter, counterCapability), new RuntimeToolRegistration(transform, transformCapability)], new RuntimeHooks()),
            IntegrationFixture.Options());
        var candidateAgent = Assert.IsAssignableFrom<ICandidateAgent>(agent);
        var host = new ScriptedCandidateHost((submission, _) => CandidateFixture.Feedback(submission));
        var result = (await CandidateConsumer.RunAsync(candidateAgent, IntegrationFixture.Request(), host)).Result;

        // The rejected payload prevents candidate delivery, new effects and a next send without erasing prior facts.
        Assert.Equal(CandidateStopReason.ProductionFailed, result.StopReason);
        Assert.Equal(AgentTerminationReason.Failed, result.Outcome.Reason);
        Assert.Equal(AgentFailureCode.ExecutionFailed, result.Outcome.FailureCode);
        Assert.Equal(2, handler.Sends); Assert.Equal(1, result.Outcome.CompletedWorkUnits);
        Assert.Equal(1, counterCapability.Effects); Assert.Equal(2, counterCapability.Total);
        Assert.Equal(0, transformCapability.Effects);
        Assert.Empty(host.Submissions); Assert.Equal(0, host.Effects); Assert.Empty(result.Receipts);
        var attempts = result.Outcome.Usage!.Attempts;
        Assert.Equal(2, attempts.Count);
        Assert.Equal(DispatchExposure.Dispatched, attempts[0].Exposure);
        Assert.Equal(101, attempts[0].Usage.InputTokens); Assert.Equal(11, attempts[0].Usage.OutputTokens);
        Assert.Equal(DispatchExposure.Dispatched, attempts[1].Exposure);
        Assert.Equal(102, attempts[1].Usage.InputTokens); Assert.Equal(12, attempts[1].Usage.OutputTokens);
    }

    [Theory]
    [InlineData("missing", UsageCompleteness.Unavailable)]
    [InlineData("partial", UsageCompleteness.Partial)]
    public async Task UnavailableUsageStaysUnknownRatherThanZero(string mode, UsageCompleteness completeness)
    {
        var counter = new CounterTool(maximumResultBytes: 64); var counterCapability = new CounterCapability();
        var transform = new TransformTool(maximumResultBytes: 64); var transformCapability = new TransformCapability();
        using var handler = FakeHandler.Reply(mode == "missing"
            ? IntegrationFixture.ResponseWithoutUsage("candidate", "unavailable-reasoning")
            : AdapterFixture.Response("candidate", "partial-usage-reasoning", usage: new { prompt_tokens = 7 }));
        using var provider = AdapterFixture.Provider(handler);
        IAgent agent = RuntimeAgentFactory.Create(new(provider,
            [new RuntimeToolRegistration(counter, counterCapability), new RuntimeToolRegistration(transform, transformCapability)], new RuntimeHooks()),
            IntegrationFixture.Options());
        var candidateAgent = Assert.IsAssignableFrom<ICandidateAgent>(agent);
        var host = new ScriptedCandidateHost((submission, _) => CandidateFixture.Feedback(submission));
        var result = (await CandidateConsumer.RunAsync(candidateAgent, IntegrationFixture.Request(units: 2), host)).Result;

        Assert.Equal(CandidateStopReason.Completed, result.StopReason);
        Assert.Equal(1, result.Outcome.CompletedWorkUnits); Assert.Single(host.Submissions);
        var usage = result.Outcome.Usage!.Attempts.Single().Usage;
        Assert.Equal(completeness, usage.Completeness);
        Assert.Null(usage.OutputTokens);
        if (mode == "missing") Assert.Null(usage.InputTokens); else Assert.Equal(7, usage.InputTokens);
    }

    [Fact]
    public async Task LaterToolMemberCapabilityFailureRejectsWholeBatchWithoutAnyEffect()
    {
        var counter = new CounterTool(maximumResultBytes: 64); var counterCapability = new CounterCapability();
        var transform = new TransformTool(maximumResultBytes: 64); var transformCapability = new ObservedTransformCapability();
        using var handler = FakeHandler.Reply(AdapterFixture.Response(null, "batch-reasoning", "tool_calls",
            calls: [AdapterFixture.Call("call-1a", "counter", IntegrationFixture.CounterArguments),
                    AdapterFixture.Call("call-1b", "transform", IntegrationFixture.TransformArguments)], usage: IntegrationFixture.Usage(1)));
        using var provider = AdapterFixture.Provider(handler);
        IAgent agent = RuntimeAgentFactory.Create(new(provider,
            [new RuntimeToolRegistration(counter, counterCapability), new RuntimeToolRegistration(transform, transformCapability)], new RuntimeHooks()),
            IntegrationFixture.Options());
        var candidateAgent = Assert.IsAssignableFrom<ICandidateAgent>(agent);
        var host = new ScriptedCandidateHost((submission, _) => CandidateFixture.Feedback(submission));
        var result = (await CandidateConsumer.RunAsync(candidateAgent, IntegrationFixture.Request(), host)).Result;

        // Valid wire/schema reaches genuine all-member admission; the capability probe proves the second member was consulted.
        Assert.Equal(2, transformCapability.Reads);
        Assert.Equal(CandidateStopReason.ProductionFailed, result.StopReason);
        Assert.Equal(1, handler.Sends); Assert.Equal(1, result.Outcome.CompletedWorkUnits);
        Assert.Equal(0, counterCapability.Effects); Assert.Equal(0, counterCapability.Total);
        Assert.Empty(host.Submissions); Assert.Equal(0, host.Effects); Assert.Empty(result.Receipts);
        var attempt = result.Outcome.Usage!.Attempts.Single();
        Assert.Equal(DispatchExposure.Dispatched, attempt.Exposure);
        Assert.Equal(101, attempt.Usage.InputTokens); Assert.Equal(11, attempt.Usage.OutputTokens);
    }

    [Fact]
    public async Task EarlierAcceptedReceiptAndEffectsSurviveLaterEffectFreeRejectedBatch()
    {
        var counter = new CounterTool(maximumResultBytes: 64); var counterCapability = new CounterCapability();
        var transform = new TransformTool(rejectDomain: true, maximumResultBytes: 64); var transformCapability = new TransformCapability();
        var wire = new Queue<string>([
            AdapterFixture.Response(null, "first-batch-reasoning", "tool_calls",
                calls: [AdapterFixture.Call("call-1a", "counter", IntegrationFixture.CounterArguments)], usage: IntegrationFixture.Usage(1)),
            AdapterFixture.Response("candidate-one", "accepted-final-reasoning", usage: IntegrationFixture.Usage(2)),
            AdapterFixture.Response(null, "rejected-batch-reasoning", "tool_calls",
                calls: [AdapterFixture.Call("call-3a", "counter", "{\"amount\":5}"),
                        AdapterFixture.Call("call-3b", "transform", IntegrationFixture.TransformArguments)], usage: IntegrationFixture.Usage(3)),
        ]);
        using var handler = new FakeHandler((message, _) =>
        {
            IntegrationFixture.AssertTransport(message);
            return Task.FromResult(AdapterFixture.Http(wire.Dequeue()));
        });
        using var provider = AdapterFixture.Provider(handler);
        IAgent agent = RuntimeAgentFactory.Create(new(provider,
            [new RuntimeToolRegistration(counter, counterCapability), new RuntimeToolRegistration(transform, transformCapability)], new RuntimeHooks()),
            IntegrationFixture.Options());
        var candidateAgent = Assert.IsAssignableFrom<ICandidateAgent>(agent);
        ScriptedCandidateHost? host = null;
        host = new ScriptedCandidateHost((submission, _) => { host!.ApplyEffect(); return CandidateFixture.Feedback(submission, CandidateDecision.Accept, CandidateContinuation.Continue); });
        var result = (await CandidateConsumer.RunAsync(candidateAgent, IntegrationFixture.Request(), host)).Result;

        // Earlier executed tools, the acknowledged Host effect and the accepted receipt survive the later rejected batch unchanged.
        Assert.Equal(CandidateStopReason.ProductionFailed, result.StopReason);
        Assert.Equal(3, handler.Sends); Assert.Equal(3, result.Outcome.CompletedWorkUnits);
        Assert.Equal(1, counterCapability.Effects); Assert.Equal(2, counterCapability.Total);
        Assert.Equal(0, transformCapability.Effects);
        Assert.Single(host.Submissions); Assert.Equal("candidate-one", host.Submissions[0].Payload);
        Assert.Equal(1, host.Effects); Assert.Single(result.Receipts);
        Assert.True(result.Receipts[0].IsAccepted); Assert.Equal(CandidateContinuation.Continue, result.Receipts[0].Continuation);
        Assert.Equal(0, result.RepairsAdmitted); Assert.Equal(1, result.ContinuationsAdmitted);
        var attempts = result.Outcome.Usage!.Attempts;
        Assert.Equal(3, attempts.Count);
        for (var i = 0; i < attempts.Count; i++)
            Assert.Equal(101 + i, attempts[i].Usage.InputTokens);
    }
}

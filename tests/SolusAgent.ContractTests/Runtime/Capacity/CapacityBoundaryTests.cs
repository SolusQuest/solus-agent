using System.Text;
using CustomTools;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Capacity;

public sealed class CapacityBoundaryTests
{
    [Fact]
    public async Task LargerConstructionPreservesDefaultDescriptorAdmissionAndExactCallIdentity()
    {
        var json = CapacityFixture.Envelope(CapacityFixture.Artifact(32768));
        var call = CapacityFixture.Call(arguments: json); var capability = new TransformCapability();
        var defaultTool = CapacityFixture.Tool(json, resultLimit: ToolLimits.PayloadBytes, argumentLimit: ToolLimits.PayloadBytes);
        Assert.Equal(ToolError.LimitExceeded, defaultTool.Prepare(call).Error); Assert.Equal(0, capability.Effects);
        var small = CapacityFixture.Call();
        var rejected = await defaultTool.InvokeAsync(defaultTool.Prepare(small).Prepared!, small, capability);
        Assert.Equal(ToolError.InvalidResult, rejected.Error); Assert.True(rejected.InvocationStarted); Assert.Equal(1, capability.Effects);
        var tool = CapacityFixture.Tool(json); var prepared = tool.Prepare(call).Prepared!;
        var alternate = new ToolCall(call.CallId, call.ToolName, json + " ");
        Assert.False(call.Matches(alternate)); Assert.Equal(ToolError.CallMismatch, (await tool.InvokeAsync(prepared, alternate, capability)).Error);
        Assert.Equal(1, capability.Effects);
        var accepted = await tool.InvokeAsync(prepared, call, capability);
        Assert.Equal(ToolOutcome.Succeeded, accepted.Outcome); CapacityFixture.Same(json, accepted.Json!); Assert.Equal(2, capability.Effects);
        Assert.Equal(ToolError.AlreadyInvoked, (await tool.InvokeAsync(prepared, call, capability)).Error);
        Assert.Equal(ToolError.ResultMismatch, Assert.Throws<ToolContractException>(() => ToolResult.RestoreHistorical(tool.Descriptor,
            call, alternate, ToolOutcome.Succeeded, ToolError.None, true, json)).Error);
        Assert.Throws<ToolContractException>(() => ToolResult.RestoreHistorical(defaultTool.Descriptor, call, call,
            ToolOutcome.Succeeded, ToolError.None, true, json));
        Assert.Equal(ToolError.None, tool.Descriptor.InputSchema.Validate(json, tool.Descriptor.MaximumArgumentBytes));
        Assert.Equal(ToolError.LimitExceeded, tool.Descriptor.InputSchema.Validate(json));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultTextBoundRemainsSmallAndExplicitLeafOptInReachesRuntime(bool optIn)
    {
        var text = new string('x', 32768); var provider = new ScriptedProvider([CapacityFixture.Final]);
        var bounds = optIn ? new ProviderExchangeBounds(maximumTextBytes: 32768) : new();
        var outcome = await CapacityFixture.Agent(provider, bounds: bounds, options: new()).ExecuteAsync(
            CapacityFixture.Request(1, [new(AgentInputSource.Repository, text)]));
        Assert.Equal(optIn ? AgentTerminationReason.Completed : AgentTerminationReason.ResourceLimit, outcome.Reason);
        Assert.Equal(optIn ? 1 : 0, provider.Effects); Assert.Equal(optIn ? 1 : 0, outcome.Usage!.Attempts.Count);
        Assert.Equal(196608, new RuntimeOptions().MaximumRetainedBytes);
        Assert.Equal(64, new RuntimeOptions().MaximumRecords); Assert.Equal(64, new RuntimeOptions().MaximumAttempts);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 2)]
    public async Task DeclaredLargeResultsReserveTheEntireBatchBeforeAnyNewEffect(int offset, int effects)
    {
        var json = CapacityFixture.Envelope(CapacityFixture.Artifact(32768));
        var capability = new TransformCapability(); var tool = CapacityFixture.Tool(json, resultLimit: CapacityFixture.MiB);
        ToolCall[] calls = [CapacityFixture.Call("one"), CapacityFixture.Call("two")];
        var provider = new ScriptedProvider([(r, o, _) => { o.CaptureUsage(new(3, 2));
            return ValueTask.FromResult(new ProviderResponse(r.Scope, r.Attempt, ProviderFinish.ToolCalls, null, calls)); }]);
        var configuration = new SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration(provider, [new(tool, capability)], new RuntimeHooks(), CapacityFixture.Bounds);
        var request = CapacityFixture.Request(1);
        var sizing = configuration.CreateRequest(new(request.ExecutionId, Guid.NewGuid(), Guid.NewGuid()), [ProviderInput.Instruction(request.Instructions)]);
        var response = new ProviderResponse(sizing.Scope, sizing.Attempt, ProviderFinish.ToolCalls, null, calls);
        var capacity = sizing.PayloadByteCount + response.PayloadByteCount + calls.Sum(CapacityFixture.CallBytes) + 2 * CapacityFixture.MiB;
        var agent = RuntimeAgentFactory.Create(configuration, new(maximumRetainedBytes: capacity + offset));
        var outcome = await agent.ExecuteAsync(request);
        Assert.Equal(effects, capability.Effects); Assert.Equal(1, provider.Effects); Assert.Equal(1, outcome.CompletedWorkUnits);
        Assert.Equal(AgentTerminationReason.ResourceLimit, outcome.Reason); Assert.Equal(effects, outcome.Usage!.ToolInvocations!.Invoked);
        Assert.Equal(3, outcome.Usage.InputTokens.ObservedTokens);
    }

    [Fact]
    public async Task LaterCapacityFailurePreservesEarlierCompleteToolEffectsAndProviderUsage()
    {
        var json = CapacityFixture.Envelope(CapacityFixture.Artifact(32768)); var capability = new TransformCapability();
        var provider = new ScriptedProvider([
            (r, o, _) => { o.CaptureUsage(new(3, 2)); return ValueTask.FromResult(new ProviderResponse(r.Scope, r.Attempt,
                ProviderFinish.ToolCalls, null, [CapacityFixture.Call("first")])); },
            (r, o, _) => { Assert.Single(r.Inputs, i => i.ToolResult?.Json == json); o.CaptureUsage(new(3, 2));
                return ValueTask.FromResult(new ProviderResponse(r.Scope, r.Attempt, ProviderFinish.ToolCalls, null,
                    [CapacityFixture.Call("later_one"), CapacityFixture.Call("later_two")])); }
        ]);
        var outcome = await CapacityFixture.Agent(provider, [new(CapacityFixture.Tool(json, resultLimit: CapacityFixture.MiB), capability)],
            options: new(maximumRetainedBytes: 3 * CapacityFixture.MiB / 2)).ExecuteAsync(CapacityFixture.Request(3));
        Assert.Equal(AgentTerminationReason.ResourceLimit, outcome.Reason); Assert.Equal(1, capability.Effects); Assert.Equal(2, provider.Effects);
        Assert.Equal(2, outcome.CompletedWorkUnits); Assert.Equal(1, outcome.Usage!.ToolInvocations!.Invoked); Assert.Equal(6, outcome.Usage.InputTokens.ObservedTokens);
    }

    [Fact]
    public void ConstructionAndSelectedConfigurationHaveFiniteStrictUtf8Neighbors()
    {
        var tool = CapacityFixture.Tool("{}");
        var exact = new string(' ', ToolLimits.MaximumPayloadBytes);
        _ = ToolCall.FromUtf8("c", tool.Descriptor.Name, Encoding.UTF8.GetBytes(exact));
        Assert.Equal(ToolError.LimitExceeded, Assert.Throws<ToolContractException>(() => new ToolCall("c", tool.Descriptor.Name, exact + " ")).Error);
        Assert.Equal(ToolError.LimitExceeded, Assert.Throws<ToolContractException>(() => ToolOutput.Success(CapacityFixture.Call(), exact + " ")).Error);
        Assert.Equal(ToolError.InvalidEncoding, Assert.Throws<ToolContractException>(() => ToolOutput.FromUtf8(CapacityFixture.Call(), [0xff])).Error);
        Assert.Equal(ToolError.InvalidEncoding, Assert.Throws<ToolContractException>(() => new ToolCall("c", tool.Descriptor.Name, "\ud800")).Error);
        var bytes = new byte[ProviderLimits.MaximumTextBytes + 1];
        Assert.Equal(ProviderError.LimitExceeded, Assert.Throws<ProviderContractException>(() => ProviderInput.DataFromUtf8(bytes)).Error);
        Assert.Equal(ProviderError.InvalidEncoding, Assert.Throws<ProviderContractException>(() => ProviderInput.DataFromUtf8([0xff])).Error);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeOptions(maximumAttempts: 129));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeOptions(maximumRecords: 2049));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeOptions(maximumRetainedBytes: 256 * CapacityFixture.MiB + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderExchangeBounds(maximumInputs: 2049));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderExchangeBounds(maximumRequestBytes: 128 * CapacityFixture.MiB + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderExchangeBounds(maximumResponseBytes: 8 * CapacityFixture.MiB + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderExchangeBounds(maximumTextBytes: 32 * CapacityFixture.MiB + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderExchangeBounds(maximumContinuationBytes: 2 * CapacityFixture.MiB + 1));
        var scope = new ProviderScope("p", "m"); var attempt = new ProviderAttempt(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        _ = new ProviderResponse(scope, attempt, ProviderFinish.Final, new string('x', 8 * CapacityFixture.MiB - 2), []);
        Assert.Equal(ProviderError.LimitExceeded, Assert.Throws<ProviderContractException>(() => new ProviderResponse(scope, attempt,
            ProviderFinish.Final, new string('x', 8 * CapacityFixture.MiB - 1), [])).Error);
        _ = new ProviderContinuation(scope, attempt, new byte[2 * CapacityFixture.MiB]);
        Assert.Throws<ProviderContractException>(() => new ProviderContinuation(scope, attempt, new byte[2 * CapacityFixture.MiB + 1]));
        _ = new ProviderSavedState(scope, attempt, 1, new byte[8192]);
        Assert.Throws<ArgumentException>(() => new ProviderSavedState(scope, attempt, 1, new byte[8193]));
        _ = new CandidateSubmission(attempt.ExecutionId, Guid.NewGuid(), new string('x', CapacityFixture.MiB), maximumPayloadBytes: CapacityFixture.MiB);
        Assert.Throws<ArgumentException>(() => new CandidateSubmission(attempt.ExecutionId, Guid.NewGuid(), new string('x', CapacityFixture.MiB + 1), maximumPayloadBytes: CapacityFixture.MiB));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CandidateExecutionBounds(1, 0, 0, CapacityFixture.MiB + 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedContinuationExactAndOneOverRefuseWithoutErasingUsage(bool over)
    {
        const int selected = 32768;
        var provider = new ScriptedProvider([(r, o, _) => { o.CaptureUsage(new(3, 2)); return ValueTask.FromResult(new ProviderResponse(r.Scope,
            r.Attempt, ProviderFinish.Final, "complete", [], new(r.Scope, r.Attempt, new byte[selected + (over ? 1 : 0)]))); }]);
        var bounds = new ProviderExchangeBounds(maximumResponseBytes: selected + 1024, maximumContinuationBytes: selected);
        var request = new ProviderRequest(provider.Scope, new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), [],
            requiredCapabilities: ProviderCapabilities.Continuation, bounds: bounds);
        var result = await provider.ExchangeAsync(request);
        Assert.Equal(over ? ProviderOutcome.Rejected : ProviderOutcome.Succeeded, result.Outcome);
        Assert.Equal(over ? ProviderError.LimitExceeded : ProviderError.None, result.Error);
        Assert.Equal(3, result.Observation.Usage.InputTokens); Assert.Equal(DispatchExposure.Dispatched, result.Observation.Exposure);
    }
}

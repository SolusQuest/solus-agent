using System.Text;
using CustomTools;
using SolusAgent.Api.Execution;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Tools;

public sealed class RuntimeToolCapacityTests
{
    [Theory]
    [InlineData(3, 0)] [InlineData(4, 2)] [InlineData(5, 2)]
    public async Task WholeBatchRecordReservationChecksExactNeighborsBeforeEffects(int records, int effects)
    {
        var cap = new CounterCapability(); var tool = new CounterTool(maximumResultBytes: 64);
        var provider = TwoCalls();
        var outcome = await ToolFixture.Agent(provider, [new(tool, cap)], new(maximumRecords: records)).ExecuteAsync(RuntimeFixture.Request(2));
        Assert.Equal(effects, cap.Effects); Assert.Equal(records < 5 ? 1 : 2, outcome.CompletedWorkUnits);
        Assert.Equal(records < 5 ? AgentTerminationReason.ResourceLimit : AgentTerminationReason.Completed, outcome.Reason); Assert.Equal(records < 5 ? 1 : 2, provider.Effects);
        Assert.Equal(records < 5 ? 1 : 2, outcome.Usage!.Attempts.Count);
    }

    [Theory]
    [InlineData(-1, 0)] [InlineData(0, 2)] [InlineData(1, 2)]
    public async Task AllDeclaredResultBytesAreReservedWithExactUtf8Neighbors(int offset, int effects)
    {
        var cap = new CounterCapability(); var tool = new CounterTool(maximumResultBytes: 64);
        var provider = TwoCalls(); var call1 = ToolFixture.Counter("一"); var call2 = ToolFixture.Counter("二");
        var configuration = new SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration(provider, [new(tool, cap)], new RuntimeHooks());
        var attempt = new ProviderAttempt(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var request = configuration.CreateRequest(attempt, [ProviderInput.Instruction("i")]);
        var response = new ProviderResponse(request.Scope, attempt, ProviderFinish.ToolCalls, null, [call1, call2]);
        var reserved = request.PayloadByteCount + response.PayloadByteCount + CallBytes(call1) + CallBytes(call2) + 128;
        provider = new([(r, o, _) => ToolFixture.Calls(r, o, [call1, call2]), ScriptedProvider.Final]);
        var outcome = await ToolFixture.Agent(provider, [new(tool, cap)], new(maximumRetainedBytes: reserved + offset)).ExecuteAsync(RuntimeFixture.Request(1));
        Assert.Equal(effects, cap.Effects); Assert.Equal(1, outcome.CompletedWorkUnits); Assert.Equal(1, provider.Effects);
        Assert.Equal(AgentTerminationReason.ResourceLimit, outcome.Reason); Assert.Equal(3, outcome.Usage!.Attempts.Single().Usage.InputTokens);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task NextRequestInputCapacityStopsAfterRetainingAllAdmittedToolEffects(int inputs)
    {
        var cap = new CounterCapability(); var tool = new CounterTool(maximumResultBytes: 64); var provider = TwoCalls();
        var outcome = await ToolFixture.Agent(provider, [new(tool, cap)], bounds: new(maximumInputs: inputs)).ExecuteAsync(RuntimeFixture.Request(2));
        // Four entries are required: instruction, accepted call turn and its two results.
        Assert.Equal(inputs < 4 ? 1 : 2, provider.Effects); Assert.Equal(2, cap.Effects);
        Assert.Equal(inputs < 4 ? AgentTerminationReason.ResourceLimit : AgentTerminationReason.Completed, outcome.Reason);
        Assert.Equal(inputs < 4 ? 1 : 2, outcome.CompletedWorkUnits);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task AttemptInventoryBoundsDoNotDiscardTheAcceptedToolRound(int attempts)
    {
        var cap = new CounterCapability(); var tool = new CounterTool(maximumResultBytes: 64); var provider = TwoCalls();
        var outcome = await ToolFixture.Agent(provider, [new(tool, cap)], new(maximumAttempts: attempts)).ExecuteAsync(RuntimeFixture.Request(2));
        Assert.Equal(2, cap.Effects); Assert.Equal(attempts, provider.Effects); Assert.Equal(attempts, outcome.Usage!.Attempts.Count);
        Assert.Equal(attempts == 1 ? AgentTerminationReason.ResourceLimit : AgentTerminationReason.Completed, outcome.Reason);
    }

    [Theory]
    [InlineData(15, true)] [InlineData(16, true)] [InlineData(17, false)]
    public async Task RealProviderBatchCountCeilingHasValidNeighbors(int count, bool valid)
    {
        var cap = new CounterCapability(); var tool = new CounterTool(maximumResultBytes: 64);
        var provider = new ScriptedProvider([(r, o, _) => ToolFixture.Calls(r, o, Enumerable.Range(0, count).Select(i => ToolFixture.Counter("c" + i)).ToArray()), ScriptedProvider.Final]);
        var outcome = await ToolFixture.Agent(provider, [new(tool, cap)]).ExecuteAsync(RuntimeFixture.Request(2));
        Assert.Equal(valid ? count : 0, cap.Effects); Assert.Equal(valid ? 2 : 1, provider.Effects);
        Assert.Equal(valid ? AgentTerminationReason.Completed : AgentTerminationReason.ResourceLimit, outcome.Reason);
        Assert.Equal(valid ? 2 : 0, outcome.CompletedWorkUnits); Assert.Equal(3, outcome.Usage!.Attempts[0].Usage.InputTokens);
    }

    [Theory]
    [InlineData(10, false)] [InlineData(11, true)] [InlineData(12, true)]
    public async Task ActualResultBytesAreEnforcedAtTheDeclaredBoundary(int bytes, bool valid)
    {
        var cap = new CounterCapability(); var tool = new CounterTool(maximumResultBytes: bytes); var provider = TwoCalls();
        var outcome = await ToolFixture.Agent(provider, [new(tool, cap)]).ExecuteAsync(RuntimeFixture.Request(2));
        Assert.Equal(valid ? 2 : 1, cap.Effects); Assert.Equal(valid ? 2 : 1, provider.Effects);
        Assert.Equal(valid ? AgentTerminationReason.Completed : AgentTerminationReason.Failed, outcome.Reason);
        Assert.Equal(valid ? 2 : 1, outcome.CompletedWorkUnits);
    }


    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(1)]
    public async Task NextActualRequestByteNeighborsPreserveTheCompletePriorToolRound(int offset)
    {
        var tool = new CounterTool(maximumResultBytes: 64); var measuredCap = new CounterCapability(); var measured = TwoCalls();
        var measuredOutcome = await ToolFixture.Agent(measured, [new(tool, measuredCap)]).ExecuteAsync(RuntimeFixture.Request(2));
        Assert.Equal(AgentTerminationReason.Completed, measuredOutcome.Reason);
        var ceiling = measured.LastRequest!.PayloadByteCount + offset;
        var cap = new CounterCapability(); var provider = TwoCalls();
        var outcome = await ToolFixture.Agent(provider, [new(tool, cap)], bounds: new(maximumRequestBytes: ceiling)).ExecuteAsync(RuntimeFixture.Request(2));
        Assert.Equal(2, cap.Effects); Assert.Equal(offset < 0 ? 1 : 2, provider.Effects);
        Assert.Equal(offset < 0 ? 1 : 2, outcome.CompletedWorkUnits);
        Assert.Equal(offset < 0 ? AgentTerminationReason.ResourceLimit : AgentTerminationReason.Completed, outcome.Reason);
    }

    private static ScriptedProvider TwoCalls() => new([(r, o, _) => ToolFixture.Calls(r, o, [ToolFixture.Counter("first"), ToolFixture.Counter("second")]), ScriptedProvider.Final]);
    private static int CallBytes(ToolCall call) => Encoding.UTF8.GetByteCount(call.CallId) + Encoding.UTF8.GetByteCount(call.ToolName) + Encoding.UTF8.GetByteCount(call.ArgumentsJson);
}

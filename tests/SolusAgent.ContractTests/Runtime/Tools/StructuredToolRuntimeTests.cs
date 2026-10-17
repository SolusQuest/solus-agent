using CustomTools;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Tools;

public sealed class StructuredToolRuntimeTests
{
    [Fact]
    public async Task FiveDefinitionsAndTwoCompleteBatchesReachNextProviderTurnWithoutChangingAssociationOrBudgets()
    {
        var tools = StructuredReadFixtures.Names.Select(n => new StructuredReadTool(n)).ToArray();
        var caps = tools.Select(_ => new StructuredReadCapability()).ToArray();
        var bindings = tools.Select((t, i) => new RuntimeToolRegistration(t, caps[i])).ToArray();
        ProviderRequest? last = null;
        var calls = tools.Select(t => new ToolCall("original-" + t.Descriptor.Name, t.Descriptor.Name, StructuredReadFixtures.Arguments(t.Descriptor.Name))).ToArray();
        var provider = new ScriptedProvider([
            (r, o, _) => ToolFixture.Calls(r, o, calls[..3], new(r.Scope, r.Attempt, [1, 2, 3])),
            (r, o, _) => ToolFixture.Calls(r, o, calls[3..], new(r.Scope, r.Attempt, [4, 5, 6])),
            (r, o, token) => { last = r; return ScriptedProvider.Final(r, o, token); }]);
        var request = new AgentRequest(Guid.NewGuid(), "Synthetic Host policy", [], new(3, TimeSpan.FromSeconds(30)), AgentCapability.None,
            new(maximumLogicalCalls: 3, maximumPhysicalDispatches: 3, maximumToolInvocations: 6));
        var outcome = await ToolFixture.Agent(provider, bindings, new(requireContinuation: true), bounds: Bounds()).ExecuteAsync(request);
        Assert.Equal(AgentTerminationReason.Completed, outcome.Reason); Assert.Equal(3, provider.Effects);
        Assert.Equal(5, outcome.Usage!.ToolInvocations!.Invoked); Assert.All(caps, cap => Assert.Equal(1, cap.Entries));
        Assert.Equal(5, last!.Tools.Count); Assert.InRange(last.PayloadByteCount, 1, 65536);
        var results = last.Inputs.Where(i => i.ToolResult is not null).Select(i => i.ToolResult!).ToArray();
        Assert.Equal(5, results.Length); Assert.Equal(calls.Select(c => c.CallId), results.Select(r => r.Call.CallId));
        Assert.Equal(calls.Select(c => c.ArgumentsJson), results.Select(r => r.Call.ArgumentsJson));
        Assert.Equal(tools.Select(t => StructuredReadFixtures.Result(t.Descriptor.Name)), results.Select(r => r.Json));
        Assert.All(results, r => Assert.False(r.IsHistorical)); Assert.Equal(new byte[] { 4, 5, 6 }, last.Continuation!.CopyReplayBytes());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ProviderSchemaAndRuntimeDomainRefusalEachLeaveBothMembersUninvoked(bool domain)
    {
        var first = new StructuredReadTool("list_files"); var firstCap = new StructuredReadCapability(); var secondCap = new StructuredReadCapability();
        IFunctionTool second = domain ? new NestedInputTool() : new StructuredReadTool("read_file");
        var invalid = domain ? new ToolCall("second", "nested_input", NestedInputTool.Arguments.Replace("allowed", "denied", StringComparison.Ordinal))
            : new ToolCall("second", "read_file", """{"path":"sample.cs","line_count":801}""");
        var provider = new ScriptedProvider([(r, o, _) => ToolFixture.Calls(r, o, [new("first", "list_files", "{}"), invalid])]);
        var outcome = await ToolFixture.Agent(provider, [new(first, firstCap), new(second, secondCap)]).ExecuteAsync(RuntimeFixture.Request(2));
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(0, firstCap.Entries); Assert.Equal(0, secondCap.Entries);
        Assert.Equal(domain ? 1 : 0, outcome.CompletedWorkUnits); Assert.Equal(1, provider.Effects);
        Assert.Equal(3, Assert.Single(outcome.Usage!.Attempts).Usage.InputTokens);
    }

    [Fact]
    public async Task InvalidStructuredResultRetainsActualEntryAndStopsBeforeNextModel()
    {
        var tool = new StructuredReadTool("read_file", result: StructuredReadFixtures.Result("read_file").Replace("\"line\":1,", "\"line\":0,", StringComparison.Ordinal));
        var cap = new StructuredReadCapability();
        var provider = new ScriptedProvider([(r, o, _) => ToolFixture.Calls(r, o,
            [new("file", "read_file", StructuredReadFixtures.Arguments("read_file"))]), ScriptedProvider.Final]);
        var outcome = await ToolFixture.Agent(provider, [new(tool, cap)]).ExecuteAsync(RuntimeFixture.Request(2));
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(1, cap.Entries); Assert.Equal(1, provider.Effects);
        Assert.Equal(1, outcome.Usage!.ToolInvocations!.Invoked);
    }

    internal static ProviderExchangeBounds Bounds() => new(maximumTools: 5, maximumToolCalls: 3, maximumRequestBytes: 65536, maximumResponseBytes: 16384);

    [Fact]
    public async Task MatchingStructuredHistoricalDataCannotStandInForCurrentInvocationEvidence()
    {
        var tool = new HistoricalTool(); var cap = new StructuredReadCapability();
        var provider = new ScriptedProvider([(r, o, _) => ToolFixture.Calls(r, o,
            [new("current", "read_file", StructuredReadFixtures.Arguments("read_file"))]), ScriptedProvider.Final]);
        var outcome = await ToolFixture.Agent(provider, [new(tool, cap)]).ExecuteAsync(RuntimeFixture.Request(2));
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(1, provider.Effects); Assert.Equal(0, cap.Entries);
        Assert.Equal(1, outcome.Usage!.ToolInvocations!.Invoked);
    }

    private sealed class HistoricalTool : IFunctionTool
    {
        private readonly StructuredReadTool guarded = new("read_file");
        public ToolDescriptor Descriptor => guarded.Descriptor;
        public ToolPreparation Prepare(ToolCall call) => guarded.Prepare(call);
        public ToolError ValidateCapability(IToolCapability? capability) => guarded.ValidateCapability(capability);
        public ToolError ValidateInvocation(PreparedToolInvocation prepared, ToolCall expectedCall, IToolCapability? capability) =>
            guarded.ValidateInvocation(prepared, expectedCall, capability);
        public ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation prepared, ToolCall expectedCall, IToolCapability? capability, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ToolResult.RestoreHistorical(Descriptor, expectedCall, expectedCall, ToolOutcome.Succeeded, ToolError.None, true,
                StructuredReadFixtures.Result("read_file")));
    }
}

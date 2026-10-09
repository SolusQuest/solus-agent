using CustomTools;
using SolusAgent.Api.Execution;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Execution;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Runtime.Tools;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Tools;

public sealed class RuntimeToolInterfaceTests
{
    [Theory]
    [InlineData("owner")] [InlineData("call")] [InlineData("metadata")] [InlineData("prepare_fault")]
    public async Task ActualInterfacePreflightCannotPartiallyExecuteABatch(string mode)
    {
        var cap = new CounterCapability(); var tool = new ForwardingTool(mode);
        var provider = new ScriptedProvider([(r, o, _) => ToolFixture.Calls(r, o, [ToolFixture.Counter("first"), ToolFixture.Counter("last")])]);
        var outcome = await ToolFixture.Agent(provider, [new(tool, cap)]).ExecuteAsync(RuntimeFixture.Request(2));
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(0, cap.Effects);
        Assert.Equal(1, outcome.CompletedWorkUnits); Assert.Equal(1, provider.Effects);
    }

    [Fact]
    public async Task SemanticallyEqualFreshMetadataDoesNotRequireReferenceIdentity()
    {
        var cap = new CounterCapability(); var tool = new ForwardingTool("equal");
        var provider = new ScriptedProvider([(r, o, _) => ToolFixture.Calls(r, o, [ToolFixture.Counter("first"), ToolFixture.Counter("last")]), ScriptedProvider.Final]);
        var outcome = await ToolFixture.Agent(provider, [new(tool, cap)]).ExecuteAsync(RuntimeFixture.Request(2));
        Assert.Equal(AgentTerminationReason.Completed, outcome.Reason); Assert.Equal(2, cap.Effects);
    }

    [Theory]
    [InlineData("null")] [InlineData("invoke_fault")] [InlineData("foreign")] [InlineData("loose_bytes")] [InlineData("loose_schema")] [InlineData("cancelled")]
    public async Task PublicInterfaceResultIsRevalidatedAndNeverRetriesAfterEarlierEffect(string mode)
    {
        var cap = new CounterCapability(); var tool = new ForwardingTool(mode);
        var provider = Script();
        var outcome = await ToolFixture.Agent(provider, [new(tool, cap)]).ExecuteAsync(RuntimeFixture.Request(3));
        Assert.Equal(AgentTerminationReason.Failed, outcome.Reason); Assert.Equal(1, outcome.CompletedWorkUnits);
        Assert.Equal(mode is "loose_bytes" or "loose_schema" or "foreign" ? 2 : 1, cap.Effects);
        Assert.Equal(1, provider.Effects); Assert.Single(outcome.Usage!.Attempts);
        // Interface entry is counted even when the guarded core reports pre-effect cancellation.
        ToolAllowanceTests.Counts(outcome.Usage, 2, 0, 1);
    }

    [Theory]
    [InlineData("null")] [InlineData("invoke_fault")] [InlineData("foreign")] [InlineData("loose_schema")]
    public async Task InvalidInterfaceReturnsNeverBecomeInventedAssociatedResults(string mode)
    {
        var cap = new CounterCapability(); var options = new RuntimeOptions();
        using var cut = new RunCut(options.TimeProvider, TimeSpan.FromSeconds(10), CancellationToken.None);
        var accepted = await ToolFixture.Accept(Script(), [new(new ForwardingTool(mode), cap)], options, cut);
        var batch = await ToolBatchOperation.ExecuteAsync(accepted.State, accepted.Request, accepted.Response);
        Assert.NotEqual(ToolError.None, batch.Error); var records = accepted.State.ToolRecords;
        Assert.Equal(ToolMemberState.Succeeded, records[0].State); Assert.NotNull(records[0].Result);
        Assert.Null(records[1].Result); Assert.Null(records[1].InvocationStarted); Assert.False(records[2].InvocationStarted);
        Assert.Single(accepted.State.Records, record => record.ToolResult is not null);
        ToolAllowanceTests.Counts(accepted.State.Usage(), 2, 0, 1);
    }

    private static ScriptedProvider Script() => new([(r, o, _) => ToolFixture.Calls(r, o, [ToolFixture.Counter("first"), ToolFixture.Counter("last"), ToolFixture.Counter("unstarted")]), ScriptedProvider.Final]);
    private sealed class ForwardingTool(string mode) : IFunctionTool
    {
        private readonly CounterTool guarded = new(maximumResultBytes: 64);
        private readonly CounterTool foreign = new(maximumResultBytes: 64);
        private int descriptorReads;
        public ToolDescriptor Descriptor
        {
            get
            {
                var d = guarded.Descriptor; var changed = ++descriptorReads > 1 && mode == "metadata";
                return new(d.Name, changed ? "changed" : d.Description, d.InputSchema, d.ResultSchema, d.CapabilityId, d.Effect, d.MaximumArgumentBytes, mode == "loose_bytes" ? 11 : d.MaximumResultBytes);
            }
        }
        public ToolPreparation Prepare(ToolCall call)
        {
            if (call.CallId == "last")
            {
                if (mode == "prepare_fault") throw new InvalidOperationException("PRIVATE_PREPARE_CANARY");
                if (mode == "owner") return foreign.Prepare(call);
                if (mode == "call") return guarded.Prepare(new(call.CallId, call.ToolName, "{\"amount\":2}"));
            }
            return guarded.Prepare(call);
        }
        public ToolError ValidateInvocation(PreparedToolInvocation prepared, ToolCall expectedCall, IToolCapability? capability) => guarded.ValidateInvocation(prepared, expectedCall, capability);
        public ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation prepared, ToolCall call, IToolCapability? capability, CancellationToken token = default)
        {
            if (call.CallId != "last") return guarded.InvokeAsync(prepared, call, capability, token);
            if (mode == "null") return ValueTask.FromResult<ToolResult>(null!);
            if (mode == "invoke_fault") throw new InvalidOperationException("PRIVATE_INVOKE_CANARY");
            if (mode == "cancelled") return guarded.InvokeAsync(prepared, call, capability, new CancellationToken(true));
            if (mode == "foreign")
            {
                var other = new ToolCall("foreign", call.ToolName, call.ArgumentsJson);
                return foreign.InvokeAsync(foreign.Prepare(other).Prepared!, other, capability, token);
            }
            if (mode == "loose_bytes")
            {
                var wide = new WideCounter();
                return wide.InvokeAsync(wide.Prepare(call).Prepared!, call, capability, token);
            }
            if (mode == "loose_schema")
            {
                var loose = new LooseCounter();
                return loose.InvokeAsync(loose.Prepare(call).Prepared!, call, capability, token);
            }
            return guarded.InvokeAsync(prepared, call, capability, token);
        }
    }
    private sealed class WideCounter() : FunctionTool<CounterCapability>(new("counter", "wide", ToolSchema.Parse(CounterTool.InputSchema), ToolSchema.Parse(CounterTool.ResultSchema), "counter_increment", ToolEffect.Mutating, maximumResultBytes: 64))
    {
        protected override ValueTask<ToolOutput> InvokeCoreAsync(ToolCall call, CounterCapability capability, CancellationToken token)
        { capability.Add(1, token); return ValueTask.FromResult(ToolOutput.Success(call, "{\"total\":20}")); }
    }
    private sealed class LooseCounter() : FunctionTool<CounterCapability>(new("counter", "loose",
        ToolSchema.Parse(CounterTool.InputSchema), ToolSchema.Parse("{\"type\":\"object\",\"properties\":{\"total\":{\"type\":\"string\"}},\"required\":[\"total\"],\"additionalProperties\":false}"), "counter_increment", ToolEffect.Mutating, maximumResultBytes: 64))
    {
        protected override ValueTask<ToolOutput> InvokeCoreAsync(ToolCall call, CounterCapability capability, CancellationToken token)
        { capability.Add(1, token); return ValueTask.FromResult(ToolOutput.Success(call, "{\"total\":\"data\"}")); }
    }
}

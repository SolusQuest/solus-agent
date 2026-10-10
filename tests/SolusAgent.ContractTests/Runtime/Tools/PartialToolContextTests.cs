using System.Text.Json.Nodes;
using CustomTools;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Tools;

public sealed class PartialToolContextTests
{
    [Theory]
    [InlineData("failed")]
    [InlineData("invalid-interface")]
    [InlineData("unknown")]
    public async Task SuccessfulFirstMemberAndHonestPartialRemainderCaptureWithoutContinuation(string mode)
    {
        var counter = new CounterCapability(); var transform = new TransformCapability();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ToolOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
        ToolCall? held = null;
        IFunctionTool transformTool = new TransformTool(effect: (call, capability, token) =>
        {
            capability.Upper("actual-effect", token); held = call; entered.TrySetResult();
            return mode == "unknown" ? new(release.Task) : ValueTask.FromResult(ToolOutput.Failure(call));
        });
        var observed = new ObservableInterface(transformTool, mode == "invalid-interface");
        RuntimeToolRegistration[] tools = [new(new CounterTool(maximumResultBytes: 100), counter), new(observed, transform)];
        var provider = new PersistentProvider(tools: true)
        {
            Respond = r => new(r.Scope, r.Attempt, ProviderFinish.ToolCalls, "partial", [new("first", "counter", "{\"amount\":7}"),
                new("second", "transform", "{\"text\":\"restricted\"}"), new("third", "counter", "{\"amount\":1}")], new(r.Scope, r.Attempt, [1]))
        };
        var request = ToolContextFixture.Request(3, new(maximumToolInvocations: 3)); var sink = new RestrictedContextHost();
        using var cancellation = new CancellationTokenSource();
        var pending = ToolContextFixture.Agent(provider, tools).ExecuteWithContextAsync(new(request, ContextExecutionIntent.Fresh), sink,
            cancellationToken: cancellation.Token).AsTask();
        if (mode == "unknown") { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel(); }
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ContextCaptureStatus.Delivered, result.CaptureStatus); var envelope = sink.CopyRestrictedContext();
        var frozen = envelope.CopyRestrictedPayload(); var node = JsonNode.Parse(frozen)!;
        var members = node["Members"]!.AsArray(); Assert.Equal(3, members.Count); Assert.Null(node["ToolCursor"]);
        Assert.Equal("{\"total\":7}", (string)members[0]!["Result"]!["Json"]!);
        Assert.Equal(1, counter.Effects); Assert.Equal(7, counter.Total); Assert.Equal(2, result.Outcome!.Usage!.ToolInvocations!.Invoked);
        Assert.Equal(1, result.Outcome.Usage.ToolInvocations.ReleasedUnstarted); Assert.Null(members[2]!["Result"]);
        Assert.Equal(mode == "invalid-interface" ? 0 : 1, transform.Effects);
        if (mode == "failed") Assert.NotNull(members[1]!["Result"]);
        else Assert.Null(members[1]!["Result"]);
        if (mode == "unknown")
        {
            release.SetResult(ToolOutput.Success(held!, "{\"text\":\"LATE_RESULT_CANARY\"}"));
            Assert.Contains("LATE_RESULT_CANARY", (await observed.Returned.Task.WaitAsync(TimeSpan.FromSeconds(5))).Json!);
            Assert.Equal(frozen, envelope.CopyRestrictedPayload());
        }
        var nextProvider = new PersistentProvider(tools: true); var next = ToolContextFixture.Request();
        var authority = new SelectedAuthority(envelope, result.Checkpoint!);
        var denied = await ToolContextFixture.Agent(nextProvider, tools, authority).ExecuteWithContextAsync(
            new(next, ContextExecutionIntent.ContinueRun, envelope, OrdinaryFixture.Grant(result, next.ExecutionId)));
        Assert.Equal(ContextAdmission.Rejected, denied.Admission); Assert.Equal(0, authority.Claims);
        Assert.Equal(0, nextProvider.Imports); Assert.Equal(0, nextProvider.Effects); Assert.Equal(1, counter.Effects);
    }
    private sealed class ObservableInterface(IFunctionTool inner, bool invalid) : IFunctionTool
    {
        internal TaskCompletionSource<ToolResult> Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ToolDescriptor Descriptor => inner.Descriptor;
        public ToolPreparation Prepare(ToolCall call) => inner.Prepare(call);
        public ToolError ValidateInvocation(PreparedToolInvocation prepared, ToolCall call, IToolCapability? capability) => inner.ValidateInvocation(prepared, call, capability);
        public async ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation prepared, ToolCall call, IToolCapability? capability, CancellationToken token = default)
        {
            if (invalid) return null!;
            var result = await inner.InvokeAsync(prepared, call, capability, token); Returned.TrySetResult(result); return result;
        }
    }
}

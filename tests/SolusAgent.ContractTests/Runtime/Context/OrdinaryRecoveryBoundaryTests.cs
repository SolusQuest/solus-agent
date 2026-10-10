using CustomTools;
using System.Text.Json;
using System.Text.Json.Nodes;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Context;

public sealed class OrdinaryRecoveryBoundaryTests
{
    [Fact]
    public async Task DistinctNewTasksCannotRecycleAnEarlierLogicalWorkIdentity()
    {
        var (source, bytes) = await OrdinaryFixture.Capture(false); var firstWork = source.Checkpoint!.LogicalWorkId;
        for (var i = 0; i < 2; i++)
        {
            var request = OrdinaryFixture.Request(); var sink = new RestrictedContextHost();
            source = await OrdinaryFixture.Agent(new(), new SelectedAuthority(bytes, source.Checkpoint!)).ExecuteWithContextAsync(
                new(request, ContextExecutionIntent.NewRunFromContext, bytes, OrdinaryFixture.Grant(source, request.ExecutionId)), sink);
            Assert.Equal(AgentTerminationReason.Completed, source.Outcome!.Reason); bytes = sink.CopyRestrictedContext();
        }
        var node = JsonNode.Parse(bytes.CopyRestrictedPayload())!;
        node["Rounds"]!.AsArray()[^1]!["LogicalWorkId"] = firstWork; node["Info"]!["LogicalWorkId"] = firstWork;
        bytes = new(bytes.ImplementationId, 1, 1, JsonSerializer.SerializeToUtf8Bytes(node));
        var modified = source.Checkpoint! with { LogicalWorkId = firstWork }; var authority = new SelectedAuthority(bytes, modified);
        var next = OrdinaryFixture.Request(); var provider = new PersistentProvider();
        var result = await OrdinaryFixture.Agent(provider, authority).ExecuteWithContextAsync(new(next, ContextExecutionIntent.NewRunFromContext,
            bytes, new(Guid.NewGuid(), modified, next.ExecutionId, Guid.NewGuid())));
        Assert.Equal(ContextAdmission.Rejected, result.Admission); Assert.Equal(0, authority.Claims); Assert.Equal(0, provider.Imports);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoAttemptBackoffCutPreservesPendingOperationForThirdRound(bool deadline)
    {
        var (first, bytes) = await OrdinaryFixture.Capture();
        var origin = Assert.Single(first.Outcome!.Usage!.Attempts);
        var clock = new ControlledTimeProvider(); var backoff = RuntimeFixture.Barrier();
        clock.TimerCreated = delay => { if (delay == TimeSpan.FromSeconds(5)) backoff.TrySetResult(); };
        using var cancellation = new CancellationTokenSource();
        var secondRequest = OrdinaryFixture.Request(limits: new(1, 1, 3, 2, 1, new(new(3, 2), 3, 2), new(2, backoff: TimeSpan.FromSeconds(5))));
        var provider = new PersistentProvider(); var sink = new RestrictedContextHost();
        var pending = OrdinaryFixture.Agent(provider, new SelectedAuthority(bytes, first.Checkpoint!),
            options: new(timeProvider: clock, requireContinuation: true)).ExecuteWithContextAsync(
            new(secondRequest, ContextExecutionIntent.ContinueRun, bytes, OrdinaryFixture.Grant(first, secondRequest.ExecutionId)),
            sink, cancellationToken: cancellation.Token).AsTask();
        await RuntimeFixture.Await(backoff.Task);
        if (deadline) clock.Advance(TimeSpan.FromSeconds(30)); else cancellation.Cancel();
        var second = await RuntimeFixture.Await(pending);
        Assert.Equal(deadline ? AgentTerminationReason.ResourceLimit : AgentTerminationReason.Cancelled, second.Outcome!.Reason);
        Assert.Equal(ContextCaptureStatus.Delivered, second.CaptureStatus);
        Assert.Empty(second.Outcome.Usage!.Attempts); Assert.Equal(0, provider.Effects);
        var thirdBytes = sink.CopyRestrictedContext(); var thirdProvider = new PersistentProvider(); var thirdRequest = OrdinaryFixture.Request();
        var third = await OrdinaryFixture.Agent(thirdProvider, new SelectedAuthority(thirdBytes, second.Checkpoint!)).ExecuteWithContextAsync(
            new(thirdRequest, ContextExecutionIntent.ContinueRun, thirdBytes, OrdinaryFixture.Grant(second, thirdRequest.ExecutionId)));
        Assert.Equal(ContextAdmission.Supplied, third.Admission); Assert.Equal(AgentTerminationReason.Completed, third.Outcome!.Reason);
        Assert.Equal(2, third.History.Count); Assert.Empty(third.History[1].Usage.Attempts);
        var attempted = Assert.Single(third.Outcome.Usage!.Attempts);
        Assert.Equal(origin.LogicalCallId, attempted.LogicalCallId); Assert.Equal(2, attempted.AttemptNumber);
        Assert.Equal(origin.PhysicalAttemptId, thirdProvider.LastRequest!.History!.OperationOrigin!.PhysicalAttemptId);
        Assert.Equal(3, third.History[0].Usage.InputTokens.ObservedTokens); Assert.Equal(3, third.Outcome.Usage.InputTokens.ObservedTokens);
        Assert.Equal(1, thirdProvider.Effects);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("tamper")]
    public async Task UntrustedOrUnclaimedStateNeverReachesProviderImport(string kind)
    {
        var (source, bytes) = await OrdinaryFixture.Capture(); var provider = new PersistentProvider();
        var authority = new SelectedAuthority(bytes, source.Checkpoint!); var request = OrdinaryFixture.Request();
        var grant = OrdinaryFixture.Grant(source, request.ExecutionId);
        if (kind == "duplicate") Assert.True(authority.TryClaim(bytes, grant));
        if (kind == "tamper")
        {
            var node = JsonNode.Parse(bytes.CopyRestrictedPayload())!; node["Records"]![0]!["Text"] = "changed";
            bytes = new(bytes.ImplementationId, 1, 1, JsonSerializer.SerializeToUtf8Bytes(node));
            request = OrdinaryFixture.Request(request.ExecutionId, "changed");
        }
        var result = await OrdinaryFixture.Agent(provider, kind == "missing" ? null : authority).ExecuteWithContextAsync(
            new(request, ContextExecutionIntent.ContinueRun, bytes, grant));
        Assert.Equal(ContextAdmission.Rejected, result.Admission); Assert.Null(result.Outcome);
        Assert.Equal(0, provider.Imports); Assert.Equal(0, provider.Effects);
    }

    [Fact]
    public async Task FailedProviderImportConsumesClaimAndCannotBeReplayed()
    {
        var (source, bytes) = await OrdinaryFixture.Capture(); var provider = new PersistentProvider { RejectImport = true };
        var authority = new SelectedAuthority(bytes, source.Checkpoint!); var request = OrdinaryFixture.Request();
        var context = new ContextExecutionRequest(request, ContextExecutionIntent.ContinueRun, bytes, OrdinaryFixture.Grant(source, request.ExecutionId));
        var agent = OrdinaryFixture.Agent(provider, authority);
        Assert.Equal(ContextAdmission.Rejected, (await agent.ExecuteWithContextAsync(context)).Admission);
        Assert.Equal(1, provider.Imports); provider.RejectImport = false;
        Assert.Equal(ContextAdmission.Rejected, (await agent.ExecuteWithContextAsync(context)).Admission);
        Assert.Equal(1, provider.Imports); Assert.Equal(0, provider.Effects);
    }

    [Fact]
    public async Task HookFreeFailureDoesNotInventAcknowledgedRecoveryClosure()
    {
        var provider = new PersistentProvider(true); var sink = new RestrictedContextHost();
        AgentRequest Request() => new(Guid.NewGuid(), "i", [], new(1, TimeSpan.FromSeconds(30)), AgentCapability.None,
            new(maximumPhysicalDispatches: 1, inputTokenThreshold: 3, retryPolicy: new(2)));
        IContextAgent Agent(PersistentProvider p, IRuntimeContextAuthority? authority = null) => (IContextAgent)RuntimeAgentFactory.Create(
            new(p, [], null, requiredGuarantees: RuntimeGuarantee.ProviderBounds, contextAuthority: authority), new(requireContinuation: true));
        var source = await Agent(provider).ExecuteWithContextAsync(new(Request(), ContextExecutionIntent.Fresh), sink);
        Assert.Equal(AgentTerminationReason.ResourceLimit, source.Outcome!.Reason); Assert.Equal(3, source.Outcome.Usage!.InputTokens.ObservedTokens);
        var bytes = sink.CopyRestrictedContext(); var request = Request(); var next = new PersistentProvider();
        var authority = new SelectedAuthority(bytes, source.Checkpoint!);
        var result = await Agent(next, authority).ExecuteWithContextAsync(new(request, ContextExecutionIntent.ContinueRun, bytes, OrdinaryFixture.Grant(source, request.ExecutionId)));
        Assert.Equal(ContextAdmission.Rejected, result.Admission); Assert.Equal(0, authority.Claims); Assert.Equal(0, next.Imports); Assert.Equal(0, next.Effects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewTaskRequestAllowsExactInputCapacityAndRejectsOneMore(bool extra)
    {
        var sink = new RestrictedContextHost(); var options = new RuntimeOptions(requireContinuation: true);
        IContextAgent Agent(PersistentProvider p, IRuntimeContextAuthority? authority = null) => (IContextAgent)RuntimeAgentFactory.Create(
            new(p, [], new RuntimeHooks(), new(maximumInputs: 2), contextAuthority: authority), options);
        var source = await Agent(new()).ExecuteWithContextAsync(new(OrdinaryFixture.Request(), ContextExecutionIntent.Fresh), sink);
        var bytes = sink.CopyRestrictedContext(); var provider = new PersistentProvider(); var authority = new SelectedAuthority(bytes, source.Checkpoint!);
        var request = OrdinaryFixture.Request(data: extra ? [new(AgentInputSource.Repository, "data")] : []);
        var result = await Agent(provider, authority).ExecuteWithContextAsync(new(request, ContextExecutionIntent.NewRunFromContext, bytes, OrdinaryFixture.Grant(source, request.ExecutionId)));
        Assert.Equal(extra ? ContextAdmission.Rejected : ContextAdmission.Supplied, result.Admission);
        Assert.Equal(extra ? 0 : 1, authority.Claims); Assert.Equal(extra ? 0 : 1, provider.Effects);
        if (!extra) Assert.Equal(2, provider.LastRequest!.Inputs.Count);
    }

    [Fact]
    public async Task ResumedOperationPredecessorEndsWhenNextLogicalCallStarts()
    {
        var capability = new CounterCapability(); var tool = new CounterTool(maximumResultBytes: 100); var sink = new RestrictedContextHost();
        IContextAgent Agent(PersistentProvider p, IRuntimeContextAuthority? authority = null) => (IContextAgent)RuntimeAgentFactory.Create(
            new(p, [new(tool, capability)], new RuntimeHooks(), contextAuthority: authority), new(requireContinuation: true));
        var source = await Agent(new(true, true)).ExecuteWithContextAsync(new(OrdinaryFixture.Request(), ContextExecutionIntent.Fresh), sink);
        var bytes = sink.CopyRestrictedContext(); var provider = new PersistentProvider(tools: true); var requests = new List<ProviderRequest>();
        provider.Inspect = requests.Add;
        provider.Respond = r => new(r.Scope, r.Attempt, requests.Count == 1 ? ProviderFinish.ToolCalls : ProviderFinish.Final,
            requests.Count == 1 ? null : "final", requests.Count == 1 ? [new ToolCall("new-call", "counter", "{\"amount\":1}")] : [],
            new(r.Scope, r.Attempt, [1]));
        var request = new AgentRequest(Guid.NewGuid(), "CURRENT_HOST_INSTRUCTIONS", [], new(3, TimeSpan.FromSeconds(30)), AgentCapability.None,
            new(maximumPhysicalDispatches: 3, retryPolicy: new(2)));
        var output = new RestrictedContextHost();
        var result = await Agent(provider, new SelectedAuthority(bytes, source.Checkpoint!)).ExecuteWithContextAsync(
            new(request, ContextExecutionIntent.ContinueRun, bytes, OrdinaryFixture.Grant(source, request.ExecutionId)), output);
        Assert.Equal(AgentTerminationReason.Completed, result.Outcome!.Reason); Assert.Equal(2, provider.Effects); Assert.Equal(1, capability.Effects);
        Assert.NotNull(requests[0].History!.OperationOrigin); Assert.Null(requests[1].History!.OperationOrigin);
        Assert.NotEqual(requests[0].Attempt.LogicalCallId, requests[1].Attempt.LogicalCallId);
        Assert.Equal(ContextCaptureStatus.Unavailable, result.CaptureStatus); // Newly executed tools do not become supported saved tool state.
    }

    [Theory]
    [InlineData("completed-failure")]
    [InlineData("cancelled-stop")]
    [InlineData("unsupported")]
    [InlineData("multi-final")]
    [InlineData("partial-final")]
    public async Task IndependentlyTrustedContradictoryTerminalStateStillRejects(string kind)
    {
        var (source, envelope) = await OrdinaryFixture.Capture(kind is not ("multi-final" or "partial-final"));
        var node = JsonNode.Parse(envelope.CopyRestrictedPayload())!;
        if (kind == "completed-failure") node["Rounds"]![0]!["Reason"] = (int)AgentTerminationReason.Completed;
        if (kind == "cancelled-stop") node["Rounds"]![0]!["Stop"] = (int)RuntimeStop.Cancelled;
        if (kind == "unsupported") node["Rounds"]![0]!["Reason"] = (int)AgentTerminationReason.UnsupportedCapability;
        if (kind == "partial-final") node["Rounds"]![0]!["Reason"] = (int)AgentTerminationReason.Partial;
        if (kind == "multi-final")
        {
            var round = node["Rounds"]![0]!; var logical = Guid.NewGuid(); var physical = Guid.NewGuid();
            var fact = round["Facts"]![0]!.DeepClone(); Change(fact["Attempt"]!);
            round["Facts"]!.AsArray().Add(fact); round["Completed"] = 2;
            var usage = round["Usage"]!; var observation = usage["Attempts"]![0]!.DeepClone(); Change(observation);
            usage["Attempts"]!.AsArray().Add(observation);
            var ledger = usage["Accounting"]!["Attempts"]!; var charge = ledger[0]!.DeepClone(); Change(charge); ledger.AsArray().Add(charge);
            var model = node["Records"]![1]!.DeepClone(); Change(model["Final"]!["Attempt"]!); Change(model["Final"]!["Continuation"]!["Origin"]!);
            node["Records"]!.AsArray().Add(model); Change(node["Provider"]!["Origin"]!);
            void Change(JsonNode attempt) { attempt["LogicalCallId"] = logical; attempt["PhysicalAttemptId"] = physical; }
        }
        var bytes = new AgentContextEnvelope(envelope.ImplementationId, 1, 1, JsonSerializer.SerializeToUtf8Bytes(node));
        var authority = new SelectedAuthority(bytes, source.Checkpoint!); var provider = new PersistentProvider(); var request = OrdinaryFixture.Request();
        var result = await OrdinaryFixture.Agent(provider, authority).ExecuteWithContextAsync(new(request,
            kind is "multi-final" or "partial-final" ? ContextExecutionIntent.NewRunFromContext : ContextExecutionIntent.ContinueRun, bytes,
            OrdinaryFixture.Grant(source, request.ExecutionId)));
        Assert.Equal(ContextAdmission.Rejected, result.Admission); Assert.Equal(ContextRejectionCode.InvalidContext, result.RejectionCode);
        Assert.Equal(0, authority.Claims); Assert.Equal(0, provider.Imports); Assert.Equal(0, provider.Effects);
    }
}

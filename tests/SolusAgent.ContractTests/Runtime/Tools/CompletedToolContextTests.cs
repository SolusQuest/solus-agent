using System.Text.Json;
using System.Text.Json.Nodes;
using CustomTools;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Tools;

public sealed class CompletedToolContextTests
{
    [Fact]
    public async Task CompletedBatchContinuesWithoutRetryPolicyOrRepeatedInvocations()
    {
        var source = await ToolContextFixture.Capture();
        Assert.Equal(AgentTerminationReason.ResourceLimit, source.Result.Outcome!.Reason);
        Assert.Equal(1, source.Counter.Effects); Assert.Equal(1, source.Transform.Effects);
        var provider = new PersistentProvider(tools: true); var request = ToolContextFixture.Request(limits: new(1, 1, 3, 2, 1, new(new(3, 2), 3, 2)));
        provider.Respond = ToolContextFixture.Final;
        var authority = new SelectedAuthority(source.Envelope, source.Result.Checkpoint!);
        var result = await ToolContextFixture.Agent(provider, source.Tools, authority).ExecuteWithContextAsync(
            new(request, ContextExecutionIntent.ContinueRun, source.Envelope, OrdinaryFixture.Grant(source.Result, request.ExecutionId)));
        Assert.Equal(ContextAdmission.Supplied, result.Admission); Assert.Equal(AgentTerminationReason.Completed, result.Outcome!.Reason);
        Assert.Equal(1, provider.Imports); Assert.Equal(1, provider.Effects); Assert.Equal(1, authority.Claims);
        Assert.Equal(0, result.Outcome.Usage!.ToolInvocations!.Invoked); Assert.Equal(2, Assert.Single(result.History).Usage.ToolInvocations!.Invoked);
        Assert.Equal(3, result.Outcome.Usage.InputTokens.ObservedTokens); Assert.Equal(3, result.History[0].Usage.InputTokens.ObservedTokens);
        Assert.Equal(1, source.Counter.Effects); Assert.Equal(1, source.Transform.Effects);
        var next = provider.LastRequest!;
        Assert.Equal(new[] { ProviderInputKind.HostInstruction, ProviderInputKind.ModelData, ProviderInputKind.ToolResultData, ProviderInputKind.ToolResultData }, next.Inputs.Select(i => i.Kind));
        Assert.True(Assert.Single(next.History!.AcceptedOrigins).Matches(next.Inputs[1].Model!.Attempt));
        Assert.Null(next.History.OperationOrigin); Assert.Equal(1, next.Attempt.AttemptNumber);
        var model = next.Inputs[1].Model!;
        Assert.Equal(source.Provider.LastRequest!.Attempt.PhysicalAttemptId, model.Attempt.PhysicalAttemptId);
        Assert.Equal(ToolContextFixture.ModelText, model.Text); Assert.Equal("{ \"amount\":7 }", model.Calls[0].ArgumentsJson);
        Assert.Equal("{\"total\":7}", next.Inputs[2].ToolResult!.Json);
        Assert.Contains(ToolContextFixture.Arguments, next.Inputs[3].ToolResult!.Json!);
        Assert.All(next.Inputs.Skip(2), i => { Assert.True(i.ToolResult!.IsHistorical); Assert.True(i.ToolResult.InvocationStarted); });
        foreach (var safe in new object[] { result, result.Outcome, result.Outcome.Usage, source.Result.Checkpoint!, source.Envelope })
        { Assert.DoesNotContain(ToolContextFixture.Arguments, safe.ToString()); Assert.DoesNotContain(ToolContextFixture.ModelText, safe.ToString()); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ToolBearingRetryCapacityIsDecidedBeforeExclusiveClaim(int delta)
    {
        var source = await ToolContextFixture.Capture(retry: true);
        var descriptorBytes = source.Tools.Sum(t => System.Text.Encoding.UTF8.GetByteCount(t.Descriptor.Name + t.Descriptor.Description
            + t.Descriptor.CapabilityId + t.Descriptor.InputSchema.NormalizedJson + t.Descriptor.ResultSchema.NormalizedJson));
        var size = source.Envelope.PayloadByteCount + descriptorBytes + source.Provider.LastRequest!.Bounds.MaximumResponseBytes + delta;
        var provider = new PersistentProvider(tools: true) { Respond = ToolContextFixture.Final };
        var authority = new SelectedAuthority(source.Envelope, source.Result.Checkpoint!);
        var request = ToolContextFixture.Request(limits: new(1, 1, 3, 2, 1, retryPolicy: new(2)));
        var result = await ToolContextFixture.Agent(provider, source.Tools, authority,
            new(maximumRetainedBytes: size, requireContinuation: true)).ExecuteWithContextAsync(
            new(request, ContextExecutionIntent.ContinueRun, source.Envelope, OrdinaryFixture.Grant(source.Result, request.ExecutionId)));
        Assert.Equal(delta == 0 ? ContextAdmission.Supplied : ContextAdmission.Rejected, result.Admission);
        Assert.Equal(delta == 0 ? 1 : 0, authority.Claims); Assert.Equal(delta == 0 ? 1 : 0, provider.Imports); Assert.Equal(delta == 0 ? 1 : 0, provider.Effects);
        Assert.Equal(1, source.Counter.Effects); Assert.Equal(1, source.Transform.Effects);
        if (delta == 0)
        {
            Assert.Equal(ToolContextFixture.History(source.Provider.LastRequest), ToolContextFixture.History(provider.LastRequest!));
            Assert.Equal(source.Provider.LastRequest.Bounds.MaximumResponseBytes, provider.LastRequest!.Bounds.MaximumResponseBytes);
            Assert.Equal(source.Provider.LastRequest.Attempt.PhysicalAttemptId, provider.LastRequest.History!.OperationOrigin!.PhysicalAttemptId);
            Assert.Equal(2, provider.LastRequest.Attempt.AttemptNumber);
        }
    }

    [Theory]
    [InlineData("missing-result")]
    [InlineData("missing-model")]
    [InlineData("missing-member")]
    [InlineData("foreign-member")]
    [InlineData("unknown-member-field")]
    [InlineData("missing-descriptor-field")]
    [InlineData("duplicate-result")]
    [InlineData("foreign-result")]
    [InlineData("raw-arguments")]
    [InlineData("call-id")]
    [InlineData("descriptor")]
    [InlineData("ordinal")]
    [InlineData("record-order")]
    [InlineData("member-order")]
    [InlineData("result-before-model")]
    [InlineData("cursor")]
    [InlineData("round-cursor")]
    [InlineData("count")]
    [InlineData("invocations")]
    [InlineData("started")]
    [InlineData("outcome")]
    [InlineData("original-bounds")]
    [InlineData("closure")]
    public async Task IndependentlyTrustedHistoryCorruptionRejectsBeforeEffects(string mutation)
    {
        var source = await ToolContextFixture.Capture(); var node = JsonNode.Parse(source.Envelope.CopyRestrictedPayload())!;
        var records = node["Records"]!.AsArray(); var members = node["Members"]!.AsArray(); var round = node["Rounds"]![0]!;
        switch (mutation)
        {
            case "missing-result": records.RemoveAt(3); break;
            case "missing-model": records.RemoveAt(1); break;
            case "missing-member": members.RemoveAt(0); break;
            case "foreign-member": members[0]!["ModelAttempt"]!["ExecutionId"] = Guid.NewGuid(); break;
            case "unknown-member-field": members[0]!["Unexpected"] = 1; break;
            case "missing-descriptor-field": members[0]!["Descriptor"]!.AsObject().Remove("Effect"); break;
            case "duplicate-result": records.Add(records[2]!.DeepClone()); break;
            case "foreign-result": members[0]!["Result"]!["Call"]!["CallId"] = "foreign"; break;
            case "raw-arguments": members[0]!["Result"]!["Call"]!["ArgumentsJson"] = "{\"amount\":7}"; break;
            case "call-id": records[1]!["ToolModel"]!["Calls"]![1]!["CallId"] = "Counter"; break;
            case "descriptor": members[0]!["Descriptor"]!["Description"] = "changed"; break;
            case "ordinal": members[0]!["Ordinal"] = 2; break;
            case "record-order": Swap(records, 2, 3); break;
            case "member-order": Swap(members, 0, 1); break;
            case "result-before-model": Swap(records, 1, 2); break;
            case "cursor": node["ToolCursor"]!["PhysicalAttemptId"] = Guid.NewGuid(); break;
            case "round-cursor": round["ToolCursor"] = null; break;
            case "count": round["Completed"] = 2; break;
            case "invocations": round["Usage"]!["ToolInvocations"]!["Invoked"] = 1; break;
            case "started": members[0]!["Result"]!["InvocationStarted"] = false; break;
            case "outcome": members[0]!["Result"]!["Outcome"] = (int)ToolOutcome.Failed; break;
            case "original-bounds": records[1]!["ToolModel"]!["Bounds"]!["MaximumResponseBytes"] = 1; break;
            case "closure": round["Facts"]![0]!["ClosureAcknowledged"] = false; break;
        }
        var envelope = new AgentContextEnvelope(source.Envelope.ImplementationId, 1, 1, JsonSerializer.SerializeToUtf8Bytes(node));
        var authority = new SelectedAuthority(envelope, source.Result.Checkpoint!); var provider = new PersistentProvider(tools: true); var hooks = new RuntimeHooks();
        var request = ToolContextFixture.Request();
        var result = await ToolContextFixture.Agent(provider, source.Tools, authority, hooks: hooks).ExecuteWithContextAsync(
            new(request, ContextExecutionIntent.ContinueRun, envelope, OrdinaryFixture.Grant(source.Result, request.ExecutionId)));
        Assert.Equal(ContextAdmission.Rejected, result.Admission); Assert.Null(result.Outcome);
        Assert.Equal(0, provider.Imports); Assert.Equal(0, provider.Effects); Assert.Empty(hooks.Exposures);
        Assert.Equal(1, source.Counter.Effects); Assert.Equal(1, source.Transform.Effects);
    }

    [Theory]
    [InlineData(CounterMode.FailAfterEffect)]
    [InlineData(CounterMode.WrongAssociation)]
    public async Task PartialBatchCapturesHonestFactsButNeverContinues(CounterMode mode)
    {
        var source = await ToolContextFixture.Capture(mode: mode); var node = JsonNode.Parse(source.Envelope.CopyRestrictedPayload())!;
        Assert.Null(node["ToolCursor"]); Assert.Equal(1, source.Counter.Effects); Assert.Equal(0, source.Transform.Effects);
        Assert.Equal(2, node["Members"]!.AsArray().Count); Assert.False((bool)node["Members"]![1]!["ReservationHeld"]!);
        Assert.Equal(1, source.Result.Outcome!.Usage!.ToolInvocations!.Invoked); Assert.Equal(1, source.Result.Outcome.Usage.ToolInvocations.ReleasedUnstarted);
        Assert.NotNull(node["Members"]![0]!["Result"]);
        var provider = new PersistentProvider(tools: true); var request = ToolContextFixture.Request();
        var result = await ToolContextFixture.Agent(provider, source.Tools, new SelectedAuthority(source.Envelope, source.Result.Checkpoint!)).ExecuteWithContextAsync(
            new(request, ContextExecutionIntent.ContinueRun, source.Envelope, OrdinaryFixture.Grant(source.Result, request.ExecutionId)));
        Assert.Equal(ContextAdmission.Rejected, result.Admission); Assert.Equal(0, provider.Effects); Assert.Equal(1, source.Counter.Effects);
    }
    private static void Swap(JsonArray array, int a, int b)
    { var first = array[a]!.DeepClone(); var second = array[b]!.DeepClone(); array[a] = second; array[b] = first; }
}

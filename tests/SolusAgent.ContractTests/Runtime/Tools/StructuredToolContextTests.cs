using System.Text.Json;
using System.Text.Json.Nodes;
using CustomTools;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Tools;

internal sealed record StructuredContextSource(ContextExecutionResult Result, AgentContextEnvelope Envelope,
    PersistentProvider Provider, StructuredReadCapability[] Capabilities);

internal static class StructuredContextFixture
{
    internal static AgentRequest Request(int units) => new(Guid.NewGuid(), "CURRENT_HOST_INSTRUCTIONS", [], new(units, TimeSpan.FromSeconds(30)),
        AgentCapability.None, new(maximumLogicalCalls: units, maximumPhysicalDispatches: units, maximumToolInvocations: 6));
    internal static (RuntimeToolRegistration[] Bindings, StructuredReadCapability[] Capabilities) Bindings(Action? entered = null)
    {
        var caps = StructuredReadFixtures.Names.Select(_ => new StructuredReadCapability(entered)).ToArray();
        return (StructuredReadFixtures.Names.Select((name, i) => new RuntimeToolRegistration(new StructuredReadTool(name), caps[i])).ToArray(), caps);
    }
    internal static IContextAgent Agent(PersistentProvider provider, RuntimeToolRegistration[] bindings, IRuntimeContextAuthority? authority = null) =>
        (IContextAgent)RuntimeAgentFactory.Create(new(provider, bindings, new RuntimeHooks(), StructuredToolRuntimeTests.Bounds(), contextAuthority: authority),
            new(requireContinuation: true));
    internal static ProviderResponse Final(ProviderRequest r) => new(r.Scope, r.Attempt, ProviderFinish.Final, "done", [], new(r.Scope, r.Attempt, [9]));
    internal static async Task<StructuredContextSource> Capture(Action? entered = null)
    {
        var (bindings, caps) = Bindings(entered); var provider = new PersistentProvider(tools: true);
        provider.Respond = r =>
        {
            var ordinal = provider.Effects;
            var names = ordinal == 1 ? StructuredReadFixtures.Names.Take(3) : StructuredReadFixtures.Names.Skip(3);
            return new(r.Scope, r.Attempt, ProviderFinish.ToolCalls, "STRUCTURED_MODEL_CANARY",
                names.Select(n => new ToolCall("original-" + n, n, StructuredReadFixtures.Arguments(n))).ToArray(), new(r.Scope, r.Attempt, [(byte)ordinal, 42]));
        };
        var sink = new RestrictedContextHost();
        var result = await Agent(provider, bindings).ExecuteWithContextAsync(new(Request(2), ContextExecutionIntent.Fresh), sink);
        Assert.Equal(ContextCaptureStatus.Delivered, result.CaptureStatus);
        Assert.Equal(AgentTerminationReason.ResourceLimit, result.Outcome!.Reason); Assert.Equal(2, provider.Effects);
        Assert.All(caps, c => Assert.Equal(1, c.Entries));
        return new(result, sink.CopyRestrictedContext(), provider, caps);
    }
}

public sealed class StructuredToolContextTests
{
    [Fact]
    public async Task ProductionCaptureAndRestorationPreserveAllFiveHistoricalResultsWithoutRepeatedEffects()
    {
        var source = await StructuredContextFixture.Capture(); var (bindings, caps) = StructuredContextFixture.Bindings();
        var provider = new PersistentProvider(tools: true) { Respond = StructuredContextFixture.Final };
        var authority = new SelectedAuthority(source.Envelope, source.Result.Checkpoint!); var request = StructuredContextFixture.Request(1);
        var restored = await StructuredContextFixture.Agent(provider, bindings, authority).ExecuteWithContextAsync(
            new(request, ContextExecutionIntent.ContinueRun, source.Envelope, OrdinaryFixture.Grant(source.Result, request.ExecutionId)));
        Assert.Equal(ContextAdmission.Supplied, restored.Admission); Assert.Equal(AgentTerminationReason.Completed, restored.Outcome!.Reason);
        Assert.Equal(1, authority.Claims); Assert.Equal(1, provider.Imports); Assert.Equal(1, provider.Effects);
        Assert.All(caps, cap => Assert.Equal(0, cap.Entries)); Assert.All(source.Capabilities, cap => Assert.Equal(1, cap.Entries));
        Assert.Equal(0, restored.Outcome.Usage!.ToolInvocations!.Invoked); Assert.Equal(5, Assert.Single(restored.History).Usage.ToolInvocations!.Invoked);
        Assert.Equal(3, restored.Outcome.Usage.InputTokens.ObservedTokens); Assert.Equal(6, restored.History[0].Usage.InputTokens.ObservedTokens);
        AssertHistory(provider.LastRequest!);
        foreach (var safe in new object[] { restored, restored.Outcome, source.Envelope, source.Result.Checkpoint! })
            Assert.DoesNotContain("STRUCTURED_MODEL_CANARY", safe.ToString());
    }

    [Theory]
    [InlineData("nested-range")] [InlineData("unknown")] [InlineData("missing")] [InlineData("encoding")]
    [InlineData("saved-descriptor")] [InlineData("raw-call")]
    public async Task IndependentlyTrustedMalformedHistoryRejectsDuringDecodeBeforeClaimImportOrEffects(string mutation)
    {
        var source = await StructuredContextFixture.Capture(); var data = JsonNode.Parse(source.Envelope.CopyRestrictedPayload())!;
        var member = data["Members"]![2]!;
        var result = JsonNode.Parse(member["Result"]!["Json"]!.GetValue<string>())!;
        switch (mutation)
        {
            case "nested-range": result["lines"]![0]!["line"] = 0; break;
            case "unknown": result["reviewed_identity"]!["authority"] = true; break;
            case "missing": result.AsObject().Remove("returned_start_line"); break;
            case "encoding": member["Result"]!["Json"] = """{"text":"\ud800"}"""; break;
            case "saved-descriptor":
                var schema = JsonNode.Parse(member["Descriptor"]!["ResultSchema"]!.GetValue<string>())!;
                schema["properties"]!["requested_line_count"]!["maximum"] = 799;
                member["Descriptor"]!["ResultSchema"] = schema.ToJsonString(); break;
            case "raw-call": member["Result"]!["Call"]!["ArgumentsJson"] = """{"path":"sample.cs","line_count":100}"""; break;
        }
        if (mutation is "nested-range" or "unknown" or "missing") member["Result"]!["Json"] = result.ToJsonString();
        var envelope = new AgentContextEnvelope(source.Envelope.ImplementationId, source.Envelope.FormatVersion, source.Envelope.CompatibilityVersion,
            JsonSerializer.SerializeToUtf8Bytes(data));
        var authority = new SelectedAuthority(envelope, source.Result.Checkpoint!);
        var (bindings, caps) = StructuredContextFixture.Bindings(); var provider = new PersistentProvider(tools: true); var request = StructuredContextFixture.Request(1);
        var restored = await StructuredContextFixture.Agent(provider, bindings, authority).ExecuteWithContextAsync(
            new(request, ContextExecutionIntent.ContinueRun, envelope, OrdinaryFixture.Grant(source.Result, request.ExecutionId)));
        Assert.Equal(ContextAdmission.Rejected, restored.Admission); Assert.Null(restored.Outcome);
        Assert.Equal(0, authority.Claims); Assert.Equal(0, provider.Imports); Assert.Equal(0, provider.Effects);
        Assert.All(caps, cap => Assert.Equal(0, cap.Entries));
    }

    [Theory]
    [InlineData("range")] [InlineData("enum")] [InlineData("nullable")] [InlineData("items")]
    public async Task ChangedCurrentDescriptorSemanticsRejectTheSavedBindingBeforeImport(string change)
    {
        var source = await StructuredContextFixture.Capture(); var (bindings, caps) = StructuredContextFixture.Bindings();
        var inner = bindings[2].Tool; var d = inner.Descriptor;
        var schema = JsonNode.Parse(d.ResultSchema.NormalizedJson)!;
        switch (change)
        {
            case "range": schema["properties"]!["requested_line_count"]!["maximum"] = 799; break;
            case "enum": schema["properties"]!["status"]!["enum"] = new JsonArray("start_after_eof"); break;
            case "nullable": schema["properties"]!["returned_start_line"]!["type"] = "integer"; break;
            case "items": schema["properties"]!["lines"]!["items"]!["properties"]!["text"]!["type"] = "boolean"; break;
        }
        var descriptor = new ToolDescriptor(d.Name, d.Description, d.InputSchema, ToolSchema.Parse(schema.ToJsonString()),
            d.CapabilityId, d.Effect, d.MaximumArgumentBytes, d.MaximumResultBytes);
        bindings[2] = new(new DescriptorTool(inner, descriptor), caps[2]);
        var provider = new PersistentProvider(tools: true); var authority = new SelectedAuthority(source.Envelope, source.Result.Checkpoint!);
        var request = StructuredContextFixture.Request(1);
        var result = await StructuredContextFixture.Agent(provider, bindings, authority).ExecuteWithContextAsync(
            new(request, ContextExecutionIntent.ContinueRun, source.Envelope, OrdinaryFixture.Grant(source.Result, request.ExecutionId)));
        Assert.Equal(ContextAdmission.Rejected, result.Admission); Assert.Equal(0, authority.Claims);
        Assert.Equal(0, provider.Imports); Assert.Equal(0, provider.Effects); Assert.All(caps, cap => Assert.Equal(0, cap.Entries));
    }

    internal static void AssertHistory(ProviderRequest request)
    {
        var results = request.Inputs.Where(i => i.ToolResult is not null).Select(i => i.ToolResult!).ToArray();
        Assert.Equal(StructuredReadFixtures.Names, results.Select(r => r.Call.ToolName));
        Assert.Equal(StructuredReadFixtures.Names.Select(StructuredReadFixtures.Arguments), results.Select(r => r.Call.ArgumentsJson));
        Assert.Equal(StructuredReadFixtures.Names.Select(n => "original-" + n), results.Select(r => r.Call.CallId));
        Assert.Equal(StructuredReadFixtures.Names.Select(n => StructuredReadFixtures.Result(n)), results.Select(r => r.Json));
        Assert.All(results, r => { Assert.True(r.IsHistorical); Assert.True(r.InvocationStarted); });
        Assert.Equal(new byte[] { 2, 42 }, request.Continuation!.CopyReplayBytes()); Assert.InRange(request.PayloadByteCount, 1, 65536);
    }

    private sealed class DescriptorTool(IFunctionTool inner, ToolDescriptor descriptor) : IFunctionTool
    {
        public ToolDescriptor Descriptor => descriptor;
        public ToolPreparation Prepare(ToolCall call) => inner.Prepare(call);
        public ToolError ValidateCapability(IToolCapability? capability) => inner.ValidateCapability(capability);
        public ToolError ValidateInvocation(PreparedToolInvocation prepared, ToolCall expectedCall, IToolCapability? capability) => inner.ValidateInvocation(prepared, expectedCall, capability);
        public ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation prepared, ToolCall expectedCall, IToolCapability? capability, CancellationToken cancellationToken = default) =>
            inner.InvokeAsync(prepared, expectedCall, capability, cancellationToken);
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ContractTests.DeepSeek.Adapter;
using SolusAgent.ContractTests.Providers;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Api.Configuration;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.RuntimeIntegration;

public sealed class FeedbackVariantTests
{
    [Fact]
    public async Task ActualVariantCheckpointFitsItsExactEncodedBoundAndRejectsOneUnder()
    {
        using var fixture = new FeedbackVariantFixture(); var sink = new RestrictedContextHost();
        await fixture.Execute(new(FeedbackVariantFixture.Request(), ContextExecutionIntent.Fresh), sink);
        var envelope = sink.CopyRestrictedContext();
        var checkpoint = JsonSerializer.Deserialize<SolusAgent.Runtime.Context.OrdinaryCheckpoint>(envelope.CopyRestrictedPayload())!;
        var measured = SolusAgent.Runtime.Context.OrdinaryContextCodec.Measure(checkpoint, 2097152);
        Assert.Equal(envelope.PayloadByteCount, measured);
        Assert.Equal(measured, SolusAgent.Runtime.Context.OrdinaryContextCodec.Encode(checkpoint, new(maximumRetainedBytes: measured)).PayloadByteCount);
        Assert.Throws<JsonException>(() => SolusAgent.Runtime.Context.OrdinaryContextCodec.Encode(checkpoint, new(maximumRetainedBytes: measured - 1)));
        Assert.Equal(2, fixture.Capability.Entries); Assert.Equal(3, fixture.Handler.Sends);
    }

    public static IEnumerable<object[]> Cases() => new[] { "domain" }.Concat(FeedbackVariants.FieldKeys)
        .SelectMany(mode => new[] { new object[] { mode, false }, [mode, true] });

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryStaticVariantHasExactWireAndRestoresWithoutHistoricalWork(string mode, bool candidates)
    {
        using var first = new FeedbackVariantFixture(mode, candidates: candidates); var sink = new RestrictedContextHost();
        var progress = new List<AgentProgress>();
        var produced = await first.Execute(new(FeedbackVariantFixture.Request(), ContextExecutionIntent.Fresh), sink, new InlineProgress(progress.Add));
        Assert.Equal(AgentTerminationReason.Completed, produced.Context.Outcome!.Reason);
        Assert.Equal(5, produced.Context.Outcome.Usage!.ToolCallAdmissions!.Admitted); Assert.Equal(2, produced.Context.Outcome.Usage.ToolInvocations!.Invoked);
        Assert.Equal(3, first.Handler.Sends); Assert.True(first.WireMatches); Assert.Equal(2, first.Capability.Entries);
        Assert.Equal(3, progress[0].Usage!.ToolCallAdmissions!.Admitted); Assert.Equal(0, progress[0].Usage!.ToolInvocations!.Invoked);
        Assert.Equal(1, first.Tools[0].Preparations);
        Assert.Equal(1, first.Tools[1].Preparations); Assert.Equal(0, first.Tools[2].Preparations); Assert.Equal(1, first.Tools[3].Preparations);
        var envelope = sink.CopyRestrictedContext(); Assert.Equal(2, envelope.FormatVersion); Assert.Equal(1, envelope.CompatibilityVersion);
        var text = Encoding.UTF8.GetString(envelope.CopyRestrictedPayload()); Assert.DoesNotContain("REJECTED_VARIANT_CANARY", text);
        var node = JsonNode.Parse(text)!; Assert.Equal(1, node["Provider"]!["FormatVersion"]!.GetValue<int>());
        var authority = new SelectedAuthority(envelope, produced.Context.Checkpoint!);
        using var second = new FeedbackVariantFixture(mode, false, candidates, authority);
        var request = FeedbackVariantFixture.Request(false);
        var consumed = await second.Execute(new(request, ContextExecutionIntent.NewRunFromContext, envelope,
            new(Guid.NewGuid(), produced.Context.Checkpoint!, request.ExecutionId, Guid.NewGuid())));
        Assert.Equal(ContextAdmission.Supplied, consumed.Context.Admission); Assert.Equal(AgentTerminationReason.Completed, consumed.Context.Outcome!.Reason);
        Assert.Equal(1, authority.Claims); Assert.Equal(1, second.Provider.Imports); Assert.Equal(1, second.Handler.Sends); Assert.True(second.WireMatches);
        Assert.Equal(0, second.Classifications); Assert.All(second.Tools, tool => Assert.Equal(0, tool.Preparations)); Assert.Equal(0, second.Capability.Entries);
        Assert.Equal(0, consumed.Context.Outcome.Usage!.ToolCallAdmissions!.Admitted); Assert.Equal(0, consumed.Context.Outcome.Usage.ToolInvocations!.Invoked);
        var history = Assert.Single(consumed.Context.History); Assert.Equal(5, history.Usage.ToolCallAdmissions!.Admitted);
        Assert.Equal(2, history.Usage.ToolInvocations!.Invoked); Assert.Equal(3, history.CompletedWorkUnits);
        Assert.All(second.Provider.Requests.Single().Inputs.Where(i => i.ToolResult is not null), i => Assert.True(i.ToolResult!.IsHistorical));
        Assert.DoesNotContain("indexed_scope", string.Join("", first.Bodies)); Assert.DoesNotContain("modified_scope", string.Join("", first.Bodies));
        if (candidates) { Assert.Equal(1, second.Hosts); Assert.Equal(0, consumed.Candidates!.RepairsAdmitted); }
    }

    [Theory]
    [InlineData("other_tool")] [InlineData("unknown")] [InlineData("missing")] [InlineData("fatal")]
    public async Task SameCodeCannotBypassOwningToolOrFatalPreflight(string fault)
    {
        using var fixture = new FeedbackVariantFixture(fault: fault);
        var result = await fixture.Execute(new(FeedbackVariantFixture.Request(), ContextExecutionIntent.Fresh));
        Assert.Equal(AgentTerminationReason.Failed, result.Context.Outcome!.Reason); Assert.Equal(1, fixture.Handler.Sends);
        Assert.Equal(0, fixture.Capability.Entries); Assert.Equal(0, result.Context.Outcome.Usage!.ToolCallAdmissions!.Admitted);
        Assert.Equal(0, result.Context.Outcome.Usage.ToolInvocations!.Invoked);
    }

    [Theory]
    [InlineData("unknown")] [InlineData("other_tool")] [InlineData("policy")] [InlineData("allowlist")]
    public async Task InvalidSelectorsOrChangedStaticBindingRejectBeforeClaim(string mutation)
    {
        using var first = new FeedbackVariantFixture("path_empty"); var sink = new RestrictedContextHost();
        var source = await first.Execute(new(FeedbackVariantFixture.Request(), ContextExecutionIntent.Fresh), sink);
        var original = sink.CopyRestrictedContext(); var node = JsonNode.Parse(original.CopyRestrictedPayload())!;
        if (mutation is "unknown" or "other_tool") node["Records"]![1]!["CorrectionModel"]!["Members"]![0]!["ErrorCode"] = mutation == "unknown" ? "unknown_key" : "indexed_scope";
        var envelope = new AgentContextEnvelope(original.ImplementationId, 2, 1, JsonSerializer.SerializeToUtf8Bytes(node));
        var authority = new SelectedAuthority(envelope, source.Context.Checkpoint!);
        using var second = new FeedbackVariantFixture("path_empty", false, authority: authority, changedPolicy: mutation == "policy", restrict: mutation == "allowlist");
        var request = FeedbackVariantFixture.Request(false);
        var result = await second.Execute(new(request, ContextExecutionIntent.NewRunFromContext, envelope,
            new(Guid.NewGuid(), source.Context.Checkpoint!, request.ExecutionId, Guid.NewGuid())));
        Assert.Equal(ContextAdmission.Rejected, result.Context.Admission); Assert.Equal(0, authority.Claims);
        Assert.Equal(0, second.Provider.Imports); Assert.Equal(0, second.Handler.Sends); Assert.Equal(0, second.Classifications); Assert.Equal(0, second.Capability.Entries);
        Assert.NotEqual(ContextRejectionCode.UnsupportedFormat, result.Context.RejectionCode);
    }

    [Fact]
    public async Task ConsistentAllowedAlternativeRequiresHostExactByteIntegrity()
    {
        using var first = new FeedbackVariantFixture(allowOther: true); var sink = new RestrictedContextHost();
        var source = await first.Execute(new(FeedbackVariantFixture.Request(), ContextExecutionIntent.Fresh), sink);
        var original = sink.CopyRestrictedContext(); var node = JsonNode.Parse(original.CopyRestrictedPayload())!;
        node["Records"]![1]!["CorrectionModel"]!["Members"]![0]!["ErrorCode"] = "modified_scope";
        var altered = new AgentContextEnvelope(original.ImplementationId, 2, 1, JsonSerializer.SerializeToUtf8Bytes(node));
        var authority = new IntegrityAuthority(original, source.Context.Checkpoint!);
        using var second = new FeedbackVariantFixture(producing: false, authority: authority, allowOther: true);
        var request = FeedbackVariantFixture.Request(false);
        var result = await second.Execute(new(request, ContextExecutionIntent.NewRunFromContext, altered,
            new(Guid.NewGuid(), source.Context.Checkpoint!, request.ExecutionId, Guid.NewGuid())));
        Assert.Equal(ContextAdmission.Rejected, result.Context.Admission); Assert.Equal(1, authority.Attempts); Assert.Equal(0, authority.Accepted);
        Assert.Equal(0, second.Provider.Imports); Assert.Equal(0, second.Handler.Sends); Assert.Equal(0, second.Capability.Entries);
    }

    [Theory]
    [InlineData(2, 0, 1)] [InlineData(4, 3, 2)] [InlineData(5, 5, 3)]
    public async Task WholeBatchAdmissionNeverRefundsCommittedCorrection(int limit, int admitted, int sends)
    {
        using var fixture = new FeedbackVariantFixture();
        var result = await fixture.Execute(new(FeedbackVariantFixture.Request(admissions: limit), ContextExecutionIntent.Fresh));
        Assert.Equal(limit == 5 ? AgentTerminationReason.Completed : AgentTerminationReason.ResourceLimit, result.Context.Outcome!.Reason);
        Assert.Equal(admitted, result.Context.Outcome.Usage!.ToolCallAdmissions!.Admitted); Assert.Equal(sends, fixture.Handler.Sends);
        Assert.Equal(limit == 5 ? 2 : 0, fixture.Capability.Entries);
    }

    [Theory]
    [InlineData(false, false, 0)] [InlineData(true, false, 3)]
    [InlineData(false, true, 0)] [InlineData(true, true, 3)]
    public async Task CancellationAndDeadlineCutsPreserveAtomicDebit(bool after, bool deadline, int admissions)
    {
        using var cancellation = new CancellationTokenSource(); var clock = new ControlledTimeProvider();
        void Stop() { if (deadline) clock.Advance(TimeSpan.FromSeconds(10)); else cancellation.Cancel(); }
        using var fixture = new FeedbackVariantFixture(options: new(timeProvider: clock, requireContinuation: true),
            validate: request => { if (!after && request.Inputs.Any(i => i.Model?.Correction is not null)) Stop(); });
        var result = await fixture.Execute(new(FeedbackVariantFixture.Request(), ContextExecutionIntent.Fresh),
            progress: new InlineProgress(_ => { if (after) Stop(); }), token: cancellation.Token);
        Assert.Equal(deadline ? AgentTerminationReason.ResourceLimit : AgentTerminationReason.Cancelled, result.Context.Outcome!.Reason);
        Assert.Equal(admissions, result.Context.Outcome.Usage!.ToolCallAdmissions!.Admitted); Assert.Equal(0, fixture.Capability.Entries); Assert.Equal(1, fixture.Handler.Sends);
    }

    [Fact]
    public async Task ProspectiveValidationRefusesVariantsBeforeAtomicAdmission()
    {
        using var fixture = new FeedbackVariantFixture(validate: request =>
        { if (request.Inputs.Any(i => i.Model?.Correction is not null)) throw new ProviderContractException(ProviderError.LimitExceeded); });
        var result = await fixture.Execute(new(FeedbackVariantFixture.Request(), ContextExecutionIntent.Fresh));
        Assert.Equal(AgentTerminationReason.ResourceLimit, result.Context.Outcome!.Reason);
        Assert.Equal(0, result.Context.Outcome.Usage!.ToolCallAdmissions!.Admitted); Assert.Equal(0, fixture.Capability.Entries); Assert.Equal(1, fixture.Handler.Sends);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public async Task EscapedWireBoundaryIsIndependentOfLogicalMetadata(int under)
    {
        using var baseline = new FeedbackVariantFixture(); await baseline.Execute(new(FeedbackVariantFixture.Request(), ContextExecutionIntent.Fresh));
        var original = baseline.Provider.Requests[1];
        var correctedRequest = new ProviderRequest(original.Scope, original.Attempt, original.Inputs, original.Tools, original.Continuation,
            requiredCapabilities: original.RequiredCapabilities, bounds: original.Bounds, toolArgumentCorrection: original.ToolArgumentCorrection);
        var bytes = Encoding.UTF8.GetByteCount(baseline.Bodies[1]);
        using var handler = new FakeHandler((_, _) => Task.FromResult(Adapter.AdapterFixture.Http(Adapter.AdapterFixture.Response("done", "wire-final"))));
        using var adapter = new SolusAgent.Providers.DeepSeek.DeepSeekProvider(new(Adapter.AdapterFixture.Credential, 2048, maximumRequestBodyBytes: bytes - under), handler);
        Assert.Equal(under == 0 ? ProviderError.None : ProviderError.LimitExceeded, adapter.ValidateRequest(correctedRequest));
        var exchanged = await adapter.ExchangeAsync(correctedRequest); Assert.Equal(under == 0 ? 1 : 0, handler.Sends);
        Assert.Equal(under == 0 ? ProviderOutcome.Succeeded : ProviderOutcome.Rejected, exchanged.Outcome);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public async Task SelectedContextRetentionReservationIncludesAllStaticVariants(int under)
    {
        using var first = new FeedbackVariantFixture(); var sink = new RestrictedContextHost();
        var source = await first.Execute(new(FeedbackVariantFixture.Request(), ContextExecutionIntent.Fresh), sink);
        var envelope = sink.CopyRestrictedContext(); var request = FeedbackVariantFixture.Request(false);
        var definitions = first.Tools.Sum(t => new[] { t.Descriptor.Name, t.Descriptor.Description, t.Descriptor.CapabilityId,
            t.Descriptor.InputSchema.NormalizedJson, t.Descriptor.ResultSchema.NormalizedJson }.Sum(Encoding.UTF8.GetByteCount)) + first.Policy.PayloadByteCount;
        var capacity = envelope.PayloadByteCount + definitions + Encoding.UTF8.GetByteCount(request.Instructions)
            + request.Data.Sum(d => Encoding.UTF8.GetByteCount(d.Text)) + 65536 - under;
        var authority = new SelectedAuthority(envelope, source.Context.Checkpoint!);
        using var second = new FeedbackVariantFixture(producing: false, authority: authority, options: new(maximumRetainedBytes: capacity, requireContinuation: true));
        var result = await second.Execute(new(request, ContextExecutionIntent.NewRunFromContext, envelope,
            new(Guid.NewGuid(), source.Context.Checkpoint!, request.ExecutionId, Guid.NewGuid())));
        Assert.Equal(under == 0 ? ContextAdmission.Supplied : ContextAdmission.Rejected, result.Context.Admission);
        Assert.Equal(under == 0 ? 1 : 0, authority.Claims); Assert.Equal(under == 0 ? 1 : 0, second.Handler.Sends); Assert.Equal(0, second.Capability.Entries);
    }

    private sealed class IntegrityAuthority(AgentContextEnvelope original, ContextCheckpointInfo source) : IRuntimeContextAuthority
    {
        internal int Attempts; internal int Accepted;
        public bool TryClaim(AgentContextEnvelope envelope, ContextRoundGrant grant)
        {
            Attempts++;
            if (grant.Source != source || !original.CopyRestrictedPayload().SequenceEqual(envelope.CopyRestrictedPayload())) return false;
            Accepted++; return true;
        }
    }
}

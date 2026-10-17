using System.Security.Cryptography;
using System.Text;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ApiOnlyConsumer.Candidates;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Candidates;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Tools;

public sealed class ArgumentCorrectionCapacityTests
{
    [Theory]
    [InlineData("selected", false)] [InlineData("selected", true)]
    [InlineData("default", false)] [InlineData("text_under", false)]
    public async Task SelectedLargeCorrectionPreservesActualBoundsAndDefaultRefusal(string mode, bool candidates)
    {
        var good = "{\"path\":\"" + new string('s', 20000) + "\"}";
        var bad = "{\"path\":0,\"discard\":\"" + new string('r', 70000) + "\"}";
        var text = new string('m', 20000); var capability = new SnapshotCapability(); var tool = new SnapshotTool(maximumArgumentBytes: 131072);
        ProviderRequest? original = null; ProviderResponse? corrected = null;
        var provider = Producer(bad, good, text, r => original = r, model => corrected = model);
        var bounds = mode == "default" ? new ProviderExchangeBounds() : new(maximumRequestBytes: 262144,
            maximumResponseBytes: 131072, maximumTextBytes: mode == "text_under" ? 19999 : 32768);
        var agent = RuntimeAgentFactory.Create(new(provider, [new(tool, capability)], new RuntimeHooks(), bounds,
            toolArgumentCorrection: new("large_policy")), new(maximumRetainedBytes: 1048576, requireContinuation: true));
        AgentOutcome outcome;
        if (!candidates) outcome = await agent.ExecuteAsync(Request());
        else
        {
            var host = new ScriptedCandidateHost((s, _) => CandidateFixture.Feedback(s));
            var result = await ((ICandidateAgent)agent).ExecuteCandidatesAsync(new(Request(), new(1, 0, 0)), host);
            Assert.Equal(0, result.RepairsAdmitted); Assert.Equal(0, result.ContinuationsAdmitted); Assert.Single(host.Submissions);
            outcome = result.Outcome;
        }
        var accepted = mode == "selected";
        Assert.Equal(accepted ? AgentTerminationReason.Completed : AgentTerminationReason.ResourceLimit, outcome.Reason);
        Assert.Equal(accepted ? 2 : 0, outcome.CompletedWorkUnits); Assert.Equal(accepted ? 2 : 0, outcome.Usage!.ToolCallAdmissions!.Admitted);
        Assert.Equal(0, outcome.Usage.ToolInvocations!.Invoked); Assert.Equal(0, capability.Entries); Assert.Equal(0, tool.DomainChecks);
        Assert.Equal(accepted ? 2 : 1, provider.Effects);
        if (!accepted) return;
        Assert.NotNull(corrected); Assert.True(corrected.Correction!.OriginalResponseBytes > ProviderLimits.ResponseBytes);
        Assert.Equal(text, corrected.Text); Assert.Equal(good, corrected.Correction.Members[1].Call.ArgumentsJson);
        var narrow = new ProviderRequest(original!.Scope, original.Attempt, original.Inputs, original.Tools,
            requiredCapabilities: original.RequiredCapabilities,
            bounds: new(maximumRequestBytes: 262144, maximumResponseBytes: corrected.Correction.OriginalResponseBytes - 1, maximumTextBytes: 32768),
            toolArgumentCorrection: original.ToolArgumentCorrection);
        Assert.Equal(ProviderError.LimitExceeded, Assert.Throws<ProviderContractException>(() => corrected.ValidateFor(narrow)).Error);
        var textNarrow = new ProviderRequest(original.Scope, original.Attempt, original.Inputs, original.Tools,
            requiredCapabilities: original.RequiredCapabilities,
            bounds: new(maximumRequestBytes: 262144, maximumResponseBytes: 131072, maximumTextBytes: 19999),
            toolArgumentCorrection: original.ToolArgumentCorrection);
        Assert.Equal(ProviderError.LimitExceeded, Assert.Throws<ProviderContractException>(() => corrected.ValidateFor(textNarrow)).Error);
    }

    [Theory]
    [InlineData(300000, true, false)] [InlineData(400000, false, false)]
    [InlineData(300000, true, true)] [InlineData(400000, false, true)]
    public async Task EncodedCorrectionCapRemainsIndependentOfLargerRuntimeRetention(int characters, bool admitted, bool variants)
    {
        var good = "{\"path\":\"" + new string('<', characters) + "\"}";
        const string bad = "{\"path\":0,\"discard\":\"REJECTED_CAPACITY_CANARY\"}";
        var provider = Producer(bad, good, "text"); var capability = new SnapshotCapability();
        var tool = new SnapshotTool(maximumArgumentBytes: 1048576); var sink = new RestrictedContextHost();
        var agent = (IContextAgent)RuntimeAgentFactory.Create(new(new CaptureOnly(provider), [new(tool, capability)], new RuntimeHooks(),
            new(maximumRequestBytes: 8388608, maximumResponseBytes: 4194304),
            toolArgumentCorrection: variants ? Providers.FeedbackVariants.Policy() : new("encoded_policy")),
            new(maximumRetainedBytes: 16777216, requireContinuation: true));
        var result = await agent.ExecuteWithContextAsync(new(Request(), ContextExecutionIntent.Fresh), sink);
        Assert.Equal(admitted ? AgentTerminationReason.Completed : AgentTerminationReason.ResourceLimit, result.Outcome!.Reason);
        Assert.Equal(admitted ? 2 : 1, result.Outcome.CompletedWorkUnits);
        Assert.Equal(admitted ? 2 : 0, result.Outcome.Usage!.ToolCallAdmissions!.Admitted);
        Assert.Equal(0, result.Outcome.Usage.ToolInvocations!.Invoked); Assert.Equal(0, capability.Entries); Assert.Equal(0, tool.DomainChecks);
        Assert.Equal(admitted ? 2 : 1, provider.Effects); Assert.Equal(ContextCaptureStatus.Delivered, result.CaptureStatus);
        var envelope = sink.CopyRestrictedContext(); Assert.True(envelope.PayloadByteCount <= 2 * 1024 * 1024);
        Assert.DoesNotContain("REJECTED_CAPACITY_CANARY", Encoding.UTF8.GetString(envelope.CopyRestrictedPayload()));
    }

    private static AgentRequest Request() => new(Guid.NewGuid(), "host", [], new(2, TimeSpan.FromSeconds(30)),
        AgentCapability.ToolCallAdmissionLimit | AgentCapability.ToolInvocationLimit,
        new(maximumLogicalCalls: 2, maximumPhysicalDispatches: 2, maximumToolCallAdmissions: 2, maximumToolInvocations: 1));

    private static ScriptedProvider Producer(string bad, string good, string text, Action<ProviderRequest>? first = null,
        Action<ProviderResponse>? corrected = null) => new([
        (r, o, _) =>
        {
            first?.Invoke(r); o.CaptureUsage(new(3, 2));
            return ValueTask.FromResult(new ProviderResponse(r.Scope, r.Attempt, ProviderFinish.ToolCalls, text,
                [new("bad", "read_snapshot", bad), new("good", "read_snapshot", good)], new(r.Scope, r.Attempt, [1, 42])));
        },
        (r, o, token) =>
        {
            var model = Assert.Single(r.Inputs, i => i.Model?.Correction is not null).Model!;
            Assert.Equal("{\"_solus_rejected\":true}", model.Correction!.Members[0].Call.ArgumentsJson);
            Assert.Equal(good, model.Correction.Members[1].Call.ArgumentsJson);
            Assert.Equal(new[] { "bad", "good" }, r.Inputs.Where(i => i.CorrectionFeedback is not null).Select(i => i.CorrectionFeedback!.Call.CallId));
            Assert.DoesNotContain(r.Inputs, i => i.ToolResult is not null); corrected?.Invoke(model);
            return ScriptedProvider.Final(r, o, token);
        },
    ], capabilities: ArgumentCorrectionFixture.Capabilities);

    // Actual guarded producer and pure request seam; this fixture qualifies capture only, never import/restore.
    private sealed class CaptureOnly(ScriptedProvider inner) : IModelProvider, IProviderRequestValidation, IProviderContextPersistence
    {
        public ProviderScope Scope => inner.Scope;
        public ProviderCapabilities Capabilities => inner.Capabilities;
        public ProviderError ValidateRequest(ProviderRequest request) => inner.ValidateRequest(request);
        public ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken token = default) => inner.ExchangeAsync(request, token);
        public ProviderSavedState? ExportContext(ProviderContextBinding binding) => new(binding.Scope, binding.Origin, 1,
            SHA256.HashData(binding.Continuation?.CopyReplayBytes() ?? []));
        public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state) => false;
    }
}

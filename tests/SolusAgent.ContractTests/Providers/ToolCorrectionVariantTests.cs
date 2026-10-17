using CustomTools;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Providers;

public sealed class ToolCorrectionVariantTests
{
    [Fact]
    public void StaticInventorySeparatesKeysFromCodesAndCopiesInputsDeterministically()
    {
        var errors = FeedbackVariants.Errors(); var policy = new ToolArgumentCorrectionPolicy("variant_policy", errors);
        Assert.Equal(23, policy.Errors.Count);
        Assert.All(policy.Errors, e => Assert.Equal(FeedbackVariants.Expected(e.Key), e.Json));
        Assert.Equal(18, policy.Errors.Count(e => e.Code == "entry_path_invalid"));
        Assert.False(policy.ContainsError("entry_unavailable"));
        Assert.Throws<ProviderContractException>(() => policy.GetError("entry_unavailable"));
        var reverse = new ToolArgumentCorrectionPolicy("variant_policy", errors.Reverse().ToArray());
        Assert.Equal(policy.SemanticFingerprint, reverse.SemanticFingerprint); Assert.Equal(policy.PayloadByteCount, reverse.PayloadByteCount);
        errors[0] = new("replacement", "other"); Assert.True(policy.ContainsError("indexed_scope"));
        var attrs = new KeyValuePair<string, string>[] { new("field", "path"), new("rule", "empty") };
        var declaration = new ToolArgumentError("selected", "shared", attrs); attrs[0] = new("field", "changed");
        Assert.Equal("path", declaration.Attributes[0].Value);
        var original = new ToolArgumentCorrectionPolicy("p", [declaration]);
        foreach (var neighbor in new[] { new ToolArgumentError("different", "shared", declaration.Attributes),
            new ToolArgumentError("selected", "different", declaration.Attributes),
            new ToolArgumentError("selected", "shared", [new("field", "prefix"), new("rule", "empty")]),
            new ToolArgumentError("selected", "shared", declaration.Attributes.Reverse().ToArray()) })
            Assert.NotEqual(original.SemanticFingerprint, new ToolArgumentCorrectionPolicy("p", [neighbor]).SemanticFingerprint);
        var sameRoles = new ToolArgumentCorrectionPolicy("p", [new("shared", "shared")]);
        Assert.Equal("shared", sameRoles.GetError("shared").Key);
    }

    [Fact]
    public void DeclarationAndPerToolLimitsAreIndependentOfToolInventory()
    {
        var errors = Enumerable.Range(0, 30).Select(i => new ToolArgumentError("variant_" + i, "shared", [new("ordinal", "v" + i)])).ToArray();
        var policy = new ToolArgumentCorrectionPolicy("p", errors); Assert.Equal(32, policy.Errors.Count);
        Assert.Equal(16, ProviderLimits.Tools);
        Assert.Equal(ProviderError.LimitExceeded, Assert.Throws<ProviderContractException>(() => new ToolArgumentCorrectionPolicy("p",
            [.. errors, new("extra", "shared", [new("ordinal", "extra")])])).Error);
        var keys = policy.Errors.Where(e => e.Key != ToolArgumentCorrectionPolicy.BatchNotExecuted).Select(e => e.Key).Reverse().ToArray();
        var binding = new ToolArgumentCorrectionBinding("b", keys, (_, _) => ToolArgumentDisposition.Allow());
        Assert.Equal(31, binding.AllowedErrorKeys.Count); Assert.Equal(keys.Order(StringComparer.Ordinal), binding.AllowedErrorKeys);
        keys[0] = "mutated"; Assert.DoesNotContain("mutated", binding.AllowedErrorKeys);
        // The one-over input is a length rejection, not 32 eligible installed declarations.
        Assert.Equal(ProviderError.LimitExceeded, Assert.Throws<ProviderContractException>(() => new ToolArgumentCorrectionBinding("b",
            [.. binding.AllowedErrorKeys, "extra_not_installed"], (_, _) => ToolArgumentDisposition.Allow())).Error);
        Assert.Throws<ProviderContractException>(() => new ToolArgumentCorrectionBinding("b", ["x", "x"], (_, _) => ToolArgumentDisposition.Allow()));
        Assert.Throws<ProviderContractException>(() => new ToolArgumentCorrectionBinding("b", [ToolArgumentCorrectionPolicy.BatchNotExecuted], (_, _) => ToolArgumentDisposition.Allow()));
    }

    [Theory]
    [InlineData("duplicate_key")] [InlineData("duplicate_envelope")] [InlineData("builtin_key")]
    [InlineData("builtin_code")] [InlineData("invalid_key")] [InlineData("reserved_attribute")]
    public void AmbiguousOrReservedDeclarationsAreRejected(string mode)
    {
        Assert.Throws<ProviderContractException>(() => new ToolArgumentCorrectionPolicy("p", mode switch
        {
            "duplicate_key" => [new("x", "one"), new("x", "two")],
            "duplicate_envelope" => [new("x", "same"), new("y", "same")],
            "builtin_key" => [new(ToolArgumentCorrectionPolicy.ArgumentsInvalid, "other")],
            "builtin_code" => [new("other", ToolArgumentCorrectionPolicy.BatchNotExecuted)],
            "invalid_key" => [new("../source", "other")],
            _ => [new("x", "one", [new("retryable", "no")])],
        }));
    }

    [Fact]
    public async Task TwoSameCodeDenialsKeepExactAssociationAndRejectExchangedFeedback()
    {
        var request = Request(FeedbackVariants.Policy()); var source = await Source(request);
        var corrected = ProviderResponse.WithholdToolArguments(request, source, [new("a", "indexed_scope"), new("b", "modified_scope")]);
        Assert.Equal("model text", corrected.Text); Assert.Equal(new byte[] { 1, 42 }, corrected.Continuation!.CopyReplayBytes());
        Assert.Empty(corrected.Calls); Assert.Equal(new[] { "a", "b", "c" }, corrected.Correction!.Members.Select(m => m.Call.CallId));
        Assert.Equal(new[] { "indexed_scope", "modified_scope", ToolArgumentCorrectionPolicy.BatchNotExecuted }, corrected.Correction.Members.Select(m => m.ErrorKey));
        Assert.Equal("{ \"amount\" : 3 }", corrected.Correction.Members[2].Call.ArgumentsJson);
        var complete = History(request, corrected);
        Assert.Equal(new[] { "entry_unavailable", "entry_unavailable", "batch_not_executed" }, complete.Where(i => i.CorrectionFeedback is not null).Select(i => i.CorrectionFeedback!.ErrorCode));
        Assert.All(complete.Where(i => i.CorrectionFeedback is not null), i => Assert.Equal(FeedbackVariants.Expected(i.CorrectionFeedback!.ErrorKey), i.CorrectionFeedback.Json));
        _ = Next(request, corrected, complete);
        var alternate = ProviderResponse.WithholdToolArguments(request, source, [new("a", "modified_scope"), new("b", "indexed_scope")]);
        complete[^3] = ProviderInput.FromCorrectionFeedback(alternate, 0);
        Assert.Equal(ProviderError.InvalidAssociation, Assert.Throws<ProviderContractException>(() => Next(request, corrected, complete)).Error);
        var changed = Request(FeedbackVariants.Policy(true), request.Attempt);
        Assert.Equal(ProviderError.InvalidAssociation, Assert.Throws<ProviderContractException>(() => corrected.ValidateFor(changed)).Error);
        Assert.Throws<ProviderContractException>(() => ProviderResponse.WithholdToolArguments(request, source, [new("a", "entry_unavailable")]));
    }

    [Fact]
    public async Task LogicalAccountingChargesSelectorAndWireCodeSeparatelyAndEnforcesExactBound()
    {
        async Task<(int Feedback, int Manifest, int Declaration)> Measure(string key, string code)
        {
            var policy = new ToolArgumentCorrectionPolicy("p", [new(key, code)]); var request = Request(policy);
            var corrected = ProviderResponse.WithholdToolArguments(request, await Source(request), [new("a", key)]);
            return (ProviderInput.FromCorrectionFeedback(corrected, 0).PayloadByteCount, corrected.Correction!.PayloadByteCount, policy.PayloadByteCount);
        }
        var small = await Measure("k", "c"); var longKey = await Measure(new string('k', 64), "c"); var longCode = await Measure("k", new string('c', 64));
        Assert.Equal(63, longKey.Feedback - small.Feedback); Assert.Equal(63, longKey.Manifest - small.Manifest); Assert.Equal(63, longKey.Declaration - small.Declaration);
        Assert.Equal(126, longCode.Feedback - small.Feedback); Assert.Equal(126, longCode.Manifest - small.Manifest);
        var request = Request(FeedbackVariants.Policy()); var model = ProviderResponse.WithholdToolArguments(request, await Source(request), [new("a", "indexed_scope")]);
        var inputs = History(request, model); var next = Next(request, model, inputs);
        _ = Next(request, model, inputs, next.PayloadByteCount);
        Assert.Equal(ProviderError.LimitExceeded, Assert.Throws<ProviderContractException>(() => Next(request, model, inputs, next.PayloadByteCount - 1)).Error);
    }

    private static ProviderRequest Request(ToolArgumentCorrectionPolicy policy, ProviderAttempt? attempt = null) => new(new("p", "m"),
        attempt ?? new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1), [ProviderInput.Instruction("h")],
        [new("counter", "counter", ToolSchema.Parse(CounterTool.InputSchema), ToolSchema.Parse(CounterTool.ResultSchema), "counter_increment", ToolEffect.Mutating, 2048, 64)],
        requiredCapabilities: DelegateProvider.All, toolArgumentCorrection: policy);
    private static async Task<ProviderResponse> Source(ProviderRequest request)
    {
        var provider = new ScriptedProvider([(r, o, _) => { o.CaptureUsage(new(3, 2)); return ValueTask.FromResult(new ProviderResponse(r.Scope, r.Attempt,
            ProviderFinish.ToolCalls, "model text", [new("a", "counter", "{\"amount\":1}"), new("b", "counter", "{\"amount\":2}"), new("c", "counter", "{ \"amount\" : 3 }")], new(r.Scope, r.Attempt, [1, 42]))); }],
            capabilities: DelegateProvider.All | ProviderCapabilities.ToolArgumentCorrection);
        return (await provider.ExchangeAsync(request)).Response!;
    }
    private static ProviderInput[] History(ProviderRequest request, ProviderResponse model) => [.. request.Inputs, ProviderInput.FromModel(model),
        .. Enumerable.Range(0, model.Correction!.Members.Count).Select(i => ProviderInput.FromCorrectionFeedback(model, i))];
    private static ProviderRequest Next(ProviderRequest original, ProviderResponse model, IReadOnlyList<ProviderInput> inputs, int? bytes = null) =>
        new(original.Scope, new(original.Attempt.ExecutionId, Guid.NewGuid(), Guid.NewGuid(), 1), inputs, original.Tools, model.Continuation,
            requiredCapabilities: DelegateProvider.All, bounds: new(maximumRequestBytes: bytes ?? ProviderLimits.RequestBytes), toolArgumentCorrection: original.ToolArgumentCorrection);
}

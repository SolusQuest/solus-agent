using System.Text;
using System.Text.Json;
using CustomTools;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Providers;

public sealed class ToolCorrectionTests
{
    [Fact]
    public async Task ProviderCoreCannotReturnTheHostOwnedDenialOrigin()
    {
        var request = Request(); var source = (await Script("{\"amount\":1}").ExchangeAsync(request)).Response!;
        var corrected = ProviderResponse.WithholdToolArguments(request, source, [new("bad", ToolArgumentCorrectionPolicy.ArgumentsInvalid)]);
        var freshChannel = new ProviderRequest(request.Scope, request.Attempt, request.Inputs, request.Tools,
            requiredCapabilities: DelegateProvider.All, toolArgumentCorrection: request.ToolArgumentCorrection);
        var provider = new ScriptedProvider([(r, o, _) => { o.CaptureUsage(new(3, 2)); return ValueTask.FromResult(corrected); }], capabilities: Capabilities);
        var result = await provider.ExchangeAsync(freshChannel);
        Assert.Equal(ProviderOutcome.Rejected, result.Outcome); Assert.Null(result.Response); Assert.Equal(3, result.Observation.Usage.InputTokens);
    }

    private const string Rejected = "REJECTED_ARGUMENT_CANARY";
    private const string Good = "{ \"amount\" : 2 }";
    private static readonly ProviderCapabilities Capabilities = DelegateProvider.All | ProviderCapabilities.ToolArgumentCorrection;

    [Theory]
    [InlineData("{\"amount\":\"REJECTED_ARGUMENT_CANARY\"}", ToolError.InvalidArguments)]
    [InlineData("{\"amount\":1,\"amount\":2}", ToolError.InvalidJson)]
    [InlineData("not json", ToolError.InvalidJson)]
    [InlineData("{\"amount\":1.5}", ToolError.InvalidArguments)]
    [InlineData("{\"amount\":[[[[[[[0]]]]]]]}", ToolError.InvalidJson)]
    public async Task GuardCreatesOnlySanitizedNonInvocationDataAndCompleteTypedNextHistory(string bad, ToolError expected)
    {
        var request = Request(); var provider = Script(bad);
        var result = await provider.ExchangeAsync(request);
        Assert.Equal(ProviderOutcome.Succeeded, result.Outcome); Assert.Equal(3, result.Observation.Usage.InputTokens);
        var model = result.Response!; Assert.True(model.Accepted); Assert.Empty(model.Calls);
        Assert.Equal(ProviderFinish.ToolCorrection, model.Finish); Assert.Equal(ToolCorrectionOrigin.SchemaArguments, model.Correction!.Origin);
        var members = model.Correction.Members;
        Assert.Equal(new[] { "bad", "good" }, members.Select(m => m.Call.CallId));
        Assert.All(members, m => Assert.Equal("counter", m.Call.ToolName));
        Assert.Equal(request.ToolArgumentCorrection!.MarkerArgumentsJson, members[0].Call.ArgumentsJson);
        Assert.Equal(expected, members[0].SchemaError); Assert.Equal(Encoding.UTF8.GetByteCount(bad), members[0].OriginalArgumentBytes);
        Assert.Equal(Good, members[1].Call.ArgumentsJson); Assert.Equal(ToolError.None, members[1].SchemaError);
        Assert.Equal(ToolArgumentCorrectionPolicy.BatchNotExecuted, members[1].ErrorCode);
        Assert.NotEqual(ToolError.None, request.Tools[0].InputSchema.Validate(members[0].Call.ArgumentsJson));
        Assert.Equal("assistant text", model.Text); Assert.Equal(new byte[] { 1, 42 }, model.Continuation!.CopyReplayBytes());
        Assert.DoesNotContain(Rejected, JsonSerializer.Serialize(model.Correction) + model.Text + Convert.ToBase64String(model.Continuation.CopyReplayBytes())); Assert.DoesNotContain(Rejected, JsonSerializer.Serialize(result.Diagnostic));
        Assert.Throws<ProviderContractException>(() => ProviderResponse.RestoreToolCalls(request, model));
        var inputs = Complete(request, model);
        Assert.Equal(ProviderInputKind.ToolCorrectionFeedbackData, inputs[^1].Kind);
        Assert.Null(inputs[^1].ToolResult);
        Assert.Equal("{\"status\":\"error\",\"code\":\"arguments_invalid\",\"retryable\":true}", inputs[^2].CorrectionFeedback!.Json);
        Assert.Equal("{\"status\":\"error\",\"code\":\"batch_not_executed\",\"retryable\":true}", inputs[^1].CorrectionFeedback!.Json);
        var next = Next(request, model, inputs);
        Assert.Equal(ProviderOutcome.Succeeded, (await provider.ExchangeAsync(next)).Outcome); Assert.Equal(2, provider.Effects);
    }

    [Theory]
    [InlineData("byte", ProviderError.LimitExceeded)]
    [InlineData("elements", ProviderError.LimitExceeded)]
    [InlineData("encoding", ProviderError.InvalidEncoding)]
    [InlineData("unknown", ProviderError.InvalidAssociation)]
    [InlineData("missing_support", ProviderError.UnsupportedCapability)]
    [InlineData("default", ProviderError.InvalidResponse)]
    public async Task StructuralResourceEncodingAndDefaultNeighborsNeverBecomeCorrection(string mode, ProviderError expected)
    {
        var request = Request(argumentBytes: mode == "byte" ? 64 : 4096, enabled: mode != "default");
        var bad = mode switch
        {
            "byte" => new string(' ', 65),
            "elements" => "{\"amount\":[" + string.Join(',', Enumerable.Repeat("0", 1001)) + "]}",
            "encoding" => "{\"amount\":\"\\ud800\"}",
            _ => "{\"amount\":\"" + Rejected + "\"}",
        };
        var provider = Script(bad, secondName: mode == "unknown" ? "unknown" : "counter", capabilities: mode == "missing_support" ? DelegateProvider.All : Capabilities);
        var result = await provider.ExchangeAsync(request);
        Assert.Equal(ProviderOutcome.Rejected, result.Outcome); Assert.Equal(expected, result.Error); Assert.Null(result.Response);
        Assert.Equal(mode == "missing_support" ? 0 : 1, provider.Effects);
        Assert.DoesNotContain(Rejected, JsonSerializer.Serialize(result.Diagnostic));
    }

    [Fact]
    public async Task SmallerSanitizedSizeCannotLaunderTheOriginalResponseAgainstForwardedConsumerBounds()
    {
        var request = Request(); var provider = Script("{\"amount\":\"" + new string('x', 1500) + "\"}");
        var result = await provider.ExchangeAsync(request); var model = result.Response!;
        Assert.True(model.Correction!.OriginalResponseBytes > model.PayloadByteCount);
        var strict = new ProviderRequest(request.Scope, request.Attempt, request.Inputs, request.Tools,
            requiredCapabilities: DelegateProvider.All, bounds: new(maximumResponseBytes: model.PayloadByteCount),
            toolArgumentCorrection: request.ToolArgumentCorrection);
        Assert.Equal(ProviderError.LimitExceeded, Assert.Throws<ProviderContractException>(() => result.ValidateFor(strict)).Error);
        var changed = new ProviderRequest(request.Scope, request.Attempt, request.Inputs, request.Tools,
            requiredCapabilities: DelegateProvider.All, toolArgumentCorrection: new("changed"));
        Assert.Equal(ProviderError.InvalidAssociation, Assert.Throws<ProviderContractException>(() => result.ValidateFor(changed)).Error);
        Assert.Equal(1, provider.Effects);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("reorder")]
    public async Task IncompleteOrForeignFeedbackCannotCloseCorrectionHistory(string mode)
    {
        var request = Request(); var model = (await Script("{}").ExchangeAsync(request)).Response!;
        var complete = Complete(request, model);
        var entries = mode == "missing" ? complete[..^1] : mode == "duplicate" ? [.. complete, complete[^1]] : complete;
        if (mode == "foreign")
        {
            var other = Request(); var foreign = (await Script("{}").ExchangeAsync(other)).Response!;
            entries[^1] = ProviderInput.FromCorrectionFeedback(foreign, 1);
        }
        if (mode == "reorder") (entries[^2], entries[^1]) = (entries[^1], entries[^2]);
        Assert.Equal(ProviderError.InvalidAssociation, Assert.Throws<ProviderContractException>(() => Next(request, model, entries)).Error);
    }

    [Fact]
    public async Task ARealInvocationResultCannotMasqueradeAsWithheldSiblingFeedback()
    {
        var request = Request(); var model = (await Script("{}").ExchangeAsync(request)).Response!;
        var entries = Complete(request, model); var tool = new CounterTool(); var capability = new CounterCapability();
        var call = model.Correction!.Members[1].Call;
        var real = await tool.InvokeAsync(tool.Prepare(call).Prepared!, call, capability);
        entries[^1] = ProviderInput.FromTool(real);
        Assert.Equal(ProviderError.InvalidAssociation, Assert.Throws<ProviderContractException>(() => Next(request, model, entries)).Error);
        Assert.Equal(1, capability.Effects);
        Assert.Throws<ProviderContractException>(() => new ProviderResponse(request.Scope, request.Attempt, ProviderFinish.ToolCorrection, null, [call]));
    }

    [Fact]
    public async Task BothRejectedAndWithheldCallIdsStayConsumedByTheGuard()
    {
        foreach (var reused in new[] { "bad", "good" })
        {
            var request = Request(); var provider = new ScriptedProvider([
                (r, o, _) => Respond(r, o, "{}"),
                (r, o, _) => Respond(r, o, "{}", secondId: reused, firstId: "fresh"),
            ], capabilities: Capabilities);
            var model = (await provider.ExchangeAsync(request)).Response!;
            var next = Next(request, model, Complete(request, model)); var result = await provider.ExchangeAsync(next);
            Assert.Equal(ProviderOutcome.Rejected, result.Outcome); Assert.Equal(ProviderError.InvalidAssociation, result.Error);
            Assert.Null(result.Response); Assert.Equal(2, provider.Effects);
        }
    }

    [Fact]
    public async Task HostDenialUsesSameGrammarWithOnlyStartupFixedAttributes()
    {
        var policy = new ToolArgumentCorrectionPolicy("fixed", [new("path_not_tracked", [new("domain", "current_files")])], "_rejected");
        var request = Request(policy: policy); var source = (await Script("{\"amount\":1}").ExchangeAsync(request)).Response!;
        Assert.Equal(ProviderFinish.ToolCalls, source.Finish);
        var corrected = ProviderResponse.WithholdToolArguments(request, source, [new("bad", "path_not_tracked")]);
        Assert.Equal(ToolCorrectionOrigin.HostArguments, corrected.Correction!.Origin);
        Assert.Equal("{\"_rejected\":true}", corrected.Correction.Members[0].Call.ArgumentsJson);
        Assert.Equal("{\"status\":\"error\",\"code\":\"path_not_tracked\",\"domain\":\"current_files\",\"retryable\":true}",
            ProviderInput.FromCorrectionFeedback(corrected, 0).CorrectionFeedback!.Json);
        _ = Next(request, corrected, Complete(request, corrected));
        Assert.Throws<ProviderContractException>(() => ProviderResponse.WithholdToolArguments(request, source, [new("bad", "not_configured")]));
        Assert.Throws<ProviderContractException>(() => new ToolArgumentError("fixed", [new("code", "other")]));
        Assert.Throws<ProviderContractException>(() => new ToolArgumentError("fixed", [new("domain", "../private")]));
        var classifier = new ToolArgumentCorrectionBinding("classifier", ["path_not_tracked"], (_, _) => ToolArgumentDisposition.Correct("not_configured"));
        Assert.Equal(ToolArgumentDispositionKind.Fatal, classifier.Classify(source.Calls[0], new CounterCapability()).Kind);
        var throwing = new ToolArgumentCorrectionBinding("throwing", ["path_not_tracked"], (_, _) => throw new Exception(Rejected));
        Assert.Equal(ToolArgumentDispositionKind.Fatal, throwing.Classify(source.Calls[0], new CounterCapability()).Kind);
        Assert.Equal(nameof(ToolArgumentCorrectionBinding), throwing.ToString()); Assert.Equal(nameof(ProviderToolCorrection), corrected.Correction.ToString());
    }

    private static ProviderRequest Request(int argumentBytes = 2048, bool enabled = true, ToolArgumentCorrectionPolicy? policy = null)
    {
        var descriptor = new ToolDescriptor("counter", "Synthetic counter", ToolSchema.Parse(CounterTool.InputSchema),
            ToolSchema.Parse(CounterTool.ResultSchema), "counter_increment", ToolEffect.Mutating, argumentBytes, 64);
        return new(new("p", "m"), new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1), [ProviderInput.Instruction("h"), ProviderInput.Data("d")],
            [descriptor], requiredCapabilities: DelegateProvider.All, toolArgumentCorrection: enabled ? policy ?? new("fixed") : null);
    }
    private static ScriptedProvider Script(string bad, string secondName = "counter", ProviderCapabilities? capabilities = null) => new([
        (r, o, _) => Respond(r, o, bad, secondName), ScriptedProvider.Final,
    ], capabilities: capabilities ?? Capabilities);
    private static ValueTask<ProviderResponse> Respond(ProviderRequest request, ProviderObservation observation, string bad,
        string secondName = "counter", string secondId = "good", string firstId = "bad")
    {
        observation.CaptureUsage(new(3, 2));
        return ValueTask.FromResult(new ProviderResponse(request.Scope, request.Attempt, ProviderFinish.ToolCalls, "assistant text",
            [new(firstId, "counter", bad), new(secondId, secondName, Good)], new(request.Scope, request.Attempt, [1, 42])));
    }
    private static ProviderInput[] Complete(ProviderRequest request, ProviderResponse model) =>
        [.. request.Inputs, ProviderInput.FromModel(model), ProviderInput.FromCorrectionFeedback(model, 0), ProviderInput.FromCorrectionFeedback(model, 1)];
    private static ProviderRequest Next(ProviderRequest original, ProviderResponse model, IReadOnlyList<ProviderInput> inputs) =>
        new(original.Scope, new(original.Attempt.ExecutionId, Guid.NewGuid(), Guid.NewGuid(), 1), inputs, original.Tools, model.Continuation,
            requiredCapabilities: DelegateProvider.All, toolArgumentCorrection: original.ToolArgumentCorrection);
}

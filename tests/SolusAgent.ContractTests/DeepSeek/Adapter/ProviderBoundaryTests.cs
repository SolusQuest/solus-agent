using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Net;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Api.Usage;
using SolusAgent.ContractTests.Architecture;
using SolusAgent.Providers.DeepSeek;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.Adapter;

public sealed class ProviderBoundaryTests
{
    [Theory]
    [InlineData("tool-count", ProviderLimitKind.ResponseToolCallCount, true)]
    [InlineData("continuation", ProviderLimitKind.ResponseReplayFrameBytes, true)]
    [InlineData("content", ProviderLimitKind.ResponseTextBytes, true)]
    [InlineData("identifier", ProviderLimitKind.ResponseIdentifierBytes, true)]
    [InlineData("declared-body", ProviderLimitKind.ResponseDeclaredBodyBytes, false)]
    [InlineData("stream-body", ProviderLimitKind.ResponseStreamBodyBytes, false)]
    public async Task ClosedLimitKindsRetainOriginalErrorUsageDispatchAndNoPayload(string mode, ProviderLimitKind expected, bool coreKnown)
    {
        var bounds = mode == "continuation" ? new ProviderExchangeBounds(maximumContinuationBytes: 128)
            : new ProviderExchangeBounds(maximumToolCalls: 1);
        var body = mode == "tool-count"
            ? AdapterFixture.Response(null, "short", "tool_calls", calls: [AdapterFixture.Call("one"), AdapterFixture.Call("two")])
            : AdapterFixture.Response(mode == "content" ? new string('x', ProviderLimits.TextBytes + 1) : "synthetic-body-canary",
                mode == "continuation" ? new string('r', 256) : "short");
        if (mode == "identifier") { var root = JsonNode.Parse(body)!; root["id"] = new string('i', 129); body = root.ToJsonString(); }
        var cap = mode.EndsWith("body", StringComparison.Ordinal) ? Encoding.UTF8.GetByteCount(body) - 1 : 524288;
        using var handler = mode == "stream-body"
            ? new FakeHandler((_, _) =>
            {
                var content = new UnmeasuredContent(Encoding.UTF8.GetBytes(body));
                content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }) : FakeHandler.Reply(body);
        using var provider = AdapterFixture.Provider(handler, responseCap: cap);
        var result = await provider.ExchangeAsync(AdapterFixture.Request(tools: [AdapterFixture.Tool()], bounds: bounds));
        Assert.Equal(ProviderOutcome.Rejected, result.Outcome); Assert.Equal(ProviderError.LimitExceeded, result.Error);
        Assert.Equal(expected, result.LimitKind); Assert.Equal(expected, result.Diagnostic.LimitKind);
        Assert.Null(result.Response); Assert.Equal(0, result.Diagnostic.AcceptedToolCalls); Assert.Equal(1, handler.Sends);
        Assert.Equal(DispatchExposure.Dispatched, result.Observation.Exposure);
        Assert.Equal(coreKnown, result.Observation.Usage.InputTokens.HasValue && result.Observation.Usage.OutputTokens.HasValue);
        if (coreKnown) { Assert.Equal(10, result.Observation.Usage.InputTokens); Assert.Equal(5, result.Observation.Usage.OutputTokens); }
        var diagnostic = JsonSerializer.Serialize(result.Diagnostic);
        Assert.DoesNotContain(AdapterFixture.Credential, diagnostic); Assert.DoesNotContain("synthetic-body-canary", diagnostic);
        Assert.DoesNotContain(new string('r', 32), diagnostic);
    }

    [Fact]
    public async Task ClosedLimitAggregateExactBoundaryAndOldConstructorsRemainHonest()
    {
        var body = AdapterFixture.Response("synthetic-body-canary", "short");
        using var handler = FakeHandler.Reply(body); using var provider = AdapterFixture.Provider(handler);
        var first = await provider.ExchangeAsync(AdapterFixture.Request());
        Assert.Equal(ProviderOutcome.Succeeded, first.Outcome); Assert.Equal(ProviderLimitKind.Unknown, first.LimitKind);
        var size = first.Response!.PayloadByteCount;
        var exact = await provider.ExchangeAsync(AdapterFixture.Request(bounds: new(maximumResponseBytes: size)));
        Assert.Equal(ProviderOutcome.Succeeded, exact.Outcome); Assert.Equal(ProviderLimitKind.Unknown, exact.Diagnostic.LimitKind);
        var request = AdapterFixture.Request(bounds: new(maximumResponseBytes: size - 1));
        var rejected = await provider.ExchangeAsync(request);
        Assert.Equal(ProviderError.LimitExceeded, rejected.Error); Assert.Equal(ProviderLimitKind.RequestResponseBytes, rejected.LimitKind);
        Assert.Equal(10, rejected.Observation.Usage.InputTokens); Assert.Equal(5, rejected.Observation.Usage.OutputTokens);
        var exposure = new RuntimeExposure(request.Scope, request.Attempt, ExposureStrength.Volatile);
        var oldSettlement = new RuntimeSettlement(exposure, rejected.Observation, RuntimeStop.None, true, rejected.Outcome, rejected.Error);
        Assert.Equal(ProviderLimitKind.Unknown, oldSettlement.ProviderLimitKind);
        var oldDiagnostic = new ProviderDiagnostic(request.Attempt.ExecutionId, request.Attempt.LogicalCallId, request.Attempt.PhysicalAttemptId,
            request.Attempt.AttemptNumber, rejected.Outcome, rejected.Error, rejected.Observation.Exposure, rejected.Observation.Usage.Completeness, 0);
        var (_, _, _, _, outcome, error, _, _, _, _, _) = oldDiagnostic;
        Assert.Equal(rejected.Outcome, outcome); Assert.Equal(rejected.Error, error); Assert.Equal(ProviderLimitKind.Unknown, oldDiagnostic.LimitKind);
        Assert.Equal(ProviderLimitKind.Unknown, new ProviderContractException(ProviderError.LimitExceeded).LimitKind);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderContractException(ProviderError.LimitExceeded, (ProviderLimitKind)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderContractException(ProviderError.InvalidResponse, ProviderLimitKind.RequestResponseBytes));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeSettlement(exposure, rejected.Observation, RuntimeStop.None, true,
            rejected.Outcome, rejected.Error, null, (ProviderLimitKind)999));
    }

    [Fact]
    public async Task ClosedLimitGeneratedResponseReplayUsesItsExactMeasuredFrameBoundary()
    {
        var body = AdapterFixture.Response("synthetic-body-canary", new string('r', 256));
        using var handler = FakeHandler.Reply(body); using var provider = AdapterFixture.Provider(handler);
        var first = await provider.ExchangeAsync(AdapterFixture.Request(tools: [AdapterFixture.Tool()]));
        Assert.Equal(ProviderOutcome.Succeeded, first.Outcome);
        var length = first.Response!.Continuation!.CopyReplayBytes().Length;
        var exact = await provider.ExchangeAsync(AdapterFixture.Request(tools: [AdapterFixture.Tool()],
            bounds: new(maximumContinuationBytes: length)));
        Assert.Equal(ProviderOutcome.Succeeded, exact.Outcome); Assert.Equal(ProviderLimitKind.Unknown, exact.LimitKind);
        var below = await provider.ExchangeAsync(AdapterFixture.Request(tools: [AdapterFixture.Tool()],
            bounds: new(maximumContinuationBytes: length - 1)));
        Assert.Equal(ProviderOutcome.Rejected, below.Outcome); Assert.Equal(ProviderError.LimitExceeded, below.Error);
        Assert.Equal(ProviderLimitKind.ResponseReplayFrameBytes, below.LimitKind);
        Assert.Equal(10, below.Observation.Usage.InputTokens); Assert.Equal(5, below.Observation.Usage.OutputTokens);
        Assert.Equal(0, below.Diagnostic.AcceptedToolCalls); Assert.Equal(3, handler.Sends);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void ClosedLimitDiagnosticJsonRoundTripPreservesOldFieldsAndValidNewKind(bool provider, bool includeKind)
    {
        var execution = Guid.NewGuid(); var logical = Guid.NewGuid(); var physical = Guid.NewGuid();
        var expectedKind = includeKind ? ProviderLimitKind.ResponseReplayFrameBytes : ProviderLimitKind.Unknown;
        if (provider)
        {
            var value = new ProviderDiagnostic(execution, logical, physical, 1, ProviderOutcome.Rejected,
                ProviderError.LimitExceeded, DispatchExposure.Dispatched, UsageCompleteness.Complete, 0, null, null,
                ProviderLimitKind.ResponseReplayFrameBytes);
            var json = JsonSerializer.SerializeToNode(value)!.AsObject();
            if (!includeKind) Assert.True(json.Remove("LimitKind"));
            var restored = JsonSerializer.Deserialize<ProviderDiagnostic>(json.ToJsonString())!;
            Assert.Equal(execution, restored.ExecutionId); Assert.Equal(logical, restored.LogicalCallId); Assert.Equal(physical, restored.PhysicalAttemptId);
            Assert.Equal(1, restored.AttemptNumber); Assert.Equal(ProviderOutcome.Rejected, restored.Outcome);
            Assert.Equal(ProviderError.LimitExceeded, restored.Error); Assert.Equal(DispatchExposure.Dispatched, restored.Exposure);
            Assert.Equal(UsageCompleteness.Complete, restored.UsageCompleteness); Assert.Equal(0, restored.AcceptedToolCalls);
            Assert.Null(restored.RetryKind); Assert.Null(restored.RetryAfter); Assert.Equal(expectedKind, restored.LimitKind);
        }
        else
        {
            var value = new RuntimeAttemptDiagnostic(execution, logical, physical, 1, RuntimeStop.None, RuntimeStop.None,
                DispatchExposure.Dispatched, UsageCompleteness.Complete, ProviderOutcome.Rejected, ProviderError.LimitExceeded,
                ProviderLimitKind.ResponseReplayFrameBytes);
            var json = JsonSerializer.SerializeToNode(value)!.AsObject();
            if (!includeKind) Assert.True(json.Remove("ProviderLimitKind"));
            var restored = JsonSerializer.Deserialize<RuntimeAttemptDiagnostic>(json.ToJsonString())!;
            Assert.Equal(execution, restored.ExecutionId); Assert.Equal(logical, restored.LogicalCallId); Assert.Equal(physical, restored.PhysicalAttemptId);
            Assert.Equal(1, restored.AttemptNumber); Assert.Equal(RuntimeStop.None, restored.AdmissionStop); Assert.Equal(RuntimeStop.None, restored.SettlementStop);
            Assert.Equal(DispatchExposure.Dispatched, restored.Exposure); Assert.Equal(UsageCompleteness.Complete, restored.UsageCompleteness);
            Assert.Equal(ProviderOutcome.Rejected, restored.ProviderOutcome); Assert.Equal(ProviderError.LimitExceeded, restored.ProviderError);
            Assert.Equal(expectedKind, restored.ProviderLimitKind);
        }
    }

    private sealed class UnmeasuredContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [Fact]
    public void ActualProviderEvaluatesOnlyRuntimeApiNoPackagesAndNoRuntimeAssemblyEdge()
    {
        var project = RepositoryLayout.ProductionProjectPath("SolusAgent.Providers.DeepSeek");
        var evaluated = MsbuildProjectEvaluation.Evaluate(project);
        ProjectBoundaryAssertions.AssertExactProjectReferences(evaluated, [RepositoryLayout.ProductionProjectPath("SolusAgent.Runtime.Api")]);
        ProjectBoundaryAssertions.AssertNoPackages(evaluated); ProjectBoundaryAssertions.AssertManagedNet10(evaluated);
        ProjectBoundaryAssertions.AssertCompileSourcesWithinRoot(evaluated, Path.GetDirectoryName(project)!);
        var references = typeof(DeepSeekProvider).Assembly.GetReferencedAssemblies();
        Assert.Contains(references, a => a.Name == "SolusAgent.Runtime.Api"); Assert.DoesNotContain(references, a => a.Name == "SolusAgent.Runtime");
    }
    [Theory]
    [InlineData("https://untrusted.invalid/chat/completions")]
    [InlineData("http://api.deepseek.com/chat/completions")]
    [InlineData("https://api.deepseek.com/chat/completions?key=canary")]
    [InlineData("https://synthetic-credential-canary@api.deepseek.com/chat/completions")]
    [InlineData("https://api.deepseek.com/v1/chat/completions")]
    public void HostEndpointMustBeTheExactSupportedCredentialDestination(string endpoint)
    {
        var failure = Assert.Throws<ArgumentException>(() => new DeepSeekOptions(AdapterFixture.Credential, 1, new(endpoint)));
        Assert.DoesNotContain(AdapterFixture.Credential, failure.ToString()); Assert.DoesNotContain(endpoint, failure.ToString());
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(393217)]
    public void TokenAllowanceMustBeExplicitPositiveAndWithinOfficialCeiling(int tokens) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeepSeekOptions(AdapterFixture.Credential, tokens));
    [Theory]
    [InlineData(1)]
    [InlineData(393216)]
    public void TokenAllowanceIncludesBothEndpoints(int tokens) => Assert.Equal(tokens, new DeepSeekOptions(AdapterFixture.Credential, tokens).MaximumTokens);
    [Theory]
    [InlineData("")]
    [InlineData("canary\r\nAuthorization: injected")]
    [InlineData("contains space")]
    [InlineData("non-ascii-密钥")]
    public void InvalidCredentialNeverCopiesRejectedBytesIntoException(string credential)
    {
        var error = Assert.Throws<ArgumentException>(() => new DeepSeekOptions(credential, 1));
        if (credential.Length != 0) Assert.DoesNotContain(credential, error.ToString());
    }
    [Fact]
    public async Task MissingFirstCallContinuationParticipationAndEmptyInputRefuseBeforeHttp()
    {
        using var handler = FakeHandler.Reply(AdapterFixture.Response()); using var provider = AdapterFixture.Provider(handler);
        var missing = await provider.ExchangeAsync(AdapterFixture.Request(tools: [AdapterFixture.Tool()], capabilities: ProviderCapabilities.ToolCalls));
        Assert.Equal(ProviderError.UnsupportedCapability, missing.Error); Assert.Equal(DispatchExposure.NotDispatched, missing.Observation.Exposure);
        var empty = await provider.ExchangeAsync(AdapterFixture.Request(inputs: []));
        Assert.Equal(ProviderError.InvalidInput, empty.Error); Assert.Equal(DispatchExposure.NotDispatched, empty.Observation.Exposure);
        Assert.Equal(0, handler.Sends);
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("frame")]
    [InlineData("utf8")]
    public async Task EveryHistoricalTurnMustHaveValidRequiredReplayBeforeSendEvenIfLatestTurnIsValid(string defect)
    {
        var origin = AdapterFixture.Request();
        var fake = new HistoricalProvider(defect); var accepted = await fake.ExchangeAsync(origin);
        Assert.Equal(ProviderOutcome.Succeeded, accepted.Outcome);
        using var handler = FakeHandler.Reply(AdapterFixture.Response()); using var provider = AdapterFixture.Provider(handler);
        var latestRequest = AdapterFixture.Request(); var good = await provider.ExchangeAsync(latestRequest);
        var inputs = new[] { ProviderInput.Data("input"), ProviderInput.FromModel(accepted.Response!), ProviderInput.FromModel(good.Response!) };
        var result = await provider.ExchangeAsync(AdapterFixture.Request(inputs, [AdapterFixture.Tool()], good.Response!.Continuation));
        Assert.Equal(ProviderError.ContinuationMismatch, result.Error); Assert.Null(result.Response);
        Assert.Equal(DispatchExposure.NotDispatched, result.Observation.Exposure); Assert.Equal(1, handler.Sends);
    }
    [Fact]
    public async Task EscapedRequestByteLimitIsEnforcedBeforeSendAndHasExactBoundary()
    {
        var request = AdapterFixture.Request([ProviderInput.Data(new string('\u0001', 1000))]);
        var bytes = DeepSeekRequestWriter.Write(request, new(AdapterFixture.Credential, 2048)); Assert.True(bytes.Length > request.PayloadByteCount);
        using var exactHandler = FakeHandler.Reply(AdapterFixture.Response()); using var exact = AdapterFixture.Provider(exactHandler, requestCap: bytes.Length);
        Assert.Equal(ProviderOutcome.Succeeded, (await exact.ExchangeAsync(request)).Outcome);
        using var smallHandler = FakeHandler.Reply(AdapterFixture.Response()); using var small = AdapterFixture.Provider(smallHandler, requestCap: bytes.Length - 1);
        var result = await small.ExchangeAsync(AdapterFixture.Request(request.Inputs)); Assert.Equal(ProviderError.LimitExceeded, result.Error);
        Assert.Equal(DispatchExposure.NotDispatched, result.Observation.Exposure); Assert.Equal(0, smallHandler.Sends);
    }
    [Fact]
    public async Task OrdinaryResultDiagnosticsAndConfigurationSerializationHaveNoRestrictedCanary()
    {
        using var handler = FakeHandler.Reply(AdapterFixture.Response("synthetic-body-canary")); using var provider = AdapterFixture.Provider(handler);
        var result = await provider.ExchangeAsync(AdapterFixture.Request());
        var text = JsonSerializer.Serialize(result.Diagnostic) + provider + new DeepSeekOptions(AdapterFixture.Credential, 1)
            + JsonSerializer.Serialize(new DeepSeekOptions(AdapterFixture.Credential, 1)) + result + result.Response + result.Response!.Continuation;
        foreach (var secret in new[] { AdapterFixture.Credential, AdapterFixture.Replay, "synthetic-body-canary" }) Assert.DoesNotContain(secret, text);
    }
    [Fact]
    public void KnownRetryOrRedirectCapableHandlersAreRejected()
    {
        using var automatic = new SocketsHttpHandler();
        Assert.Throws<ArgumentException>(() => AdapterFixture.Provider(automatic));
        using var httpClient = new HttpClientHandler(); Assert.Throws<ArgumentException>(() => AdapterFixture.Provider(httpClient));
        using var middleware = new Middleware(FakeHandler.Reply(AdapterFixture.Response())); Assert.Throws<ArgumentException>(() => AdapterFixture.Provider(middleware));
    }
    private sealed class Middleware(HttpMessageHandler inner) : DelegatingHandler(inner);
    private sealed class HistoricalProvider(string defect) : ModelProvider(AdapterFixture.Scope, AdapterFixture.All)
    {
        protected override ValueTask<ProviderResponse> ExchangeCoreAsync(ProviderRequest request, ProviderObservation observation, CancellationToken token)
        {
            ProviderContinuation? continuation = defect == "missing" ? null : new(request.Scope, request.Attempt, defect == "frame" ? [2, 65] : [1, 255]);
            return ValueTask.FromResult(new ProviderResponse(request.Scope, request.Attempt, ProviderFinish.Final, "previous final", [], continuation));
        }
    }
}

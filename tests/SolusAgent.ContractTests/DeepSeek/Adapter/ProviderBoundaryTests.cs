using System.Text.Json;
using SolusAgent.Api.Usage;
using SolusAgent.ContractTests.Architecture;
using SolusAgent.Providers.DeepSeek;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.Adapter;

public sealed class ProviderBoundaryTests
{
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
        Assert.DoesNotContain(typeof(ProviderBoundaryTests).Assembly.GetReferencedAssemblies(), a => a.Name == "SolusAgent.Runtime");
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
        using var firstHandler = FakeHandler.Reply(AdapterFixture.Response()); using var first = AdapterFixture.Provider(firstHandler);
        var bytes = DeepSeekRequestWriter.Write(request, new(AdapterFixture.Credential, 2048)); Assert.True(bytes.Length > request.PayloadByteCount);
        using var exactHandler = FakeHandler.Reply(AdapterFixture.Response()); using var exact = AdapterFixture.Provider(exactHandler, requestCap: bytes.Length);
        Assert.Equal(ProviderOutcome.Succeeded, (await exact.ExchangeAsync(request)).Outcome);
        using var smallHandler = FakeHandler.Reply(AdapterFixture.Response()); using var small = AdapterFixture.Provider(smallHandler, requestCap: bytes.Length - 1);
        var result = await small.ExchangeAsync(request); Assert.Equal(ProviderError.LimitExceeded, result.Error);
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

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.Adapter;

public sealed class RetryClassificationTests
{
    [Theory]
    [InlineData(408, ProviderRetryKind.Transient)] [InlineData(429, ProviderRetryKind.Throttled)]
    [InlineData(500, ProviderRetryKind.Transient)] [InlineData(502, ProviderRetryKind.Transient)]
    [InlineData(503, ProviderRetryKind.Transient)] [InlineData(504, ProviderRetryKind.Transient)]
    [InlineData(307, null)] [InlineData(400, null)] [InlineData(401, null)] [InlineData(403, null)]
    [InlineData(404, null)] [InlineData(409, null)] [InlineData(422, null)] [InlineData(501, null)] [InlineData(599, null)]
    public async Task ActualAdapterAndIndependentConsumerUseOnlyFiniteStatusAllowlistAndRetainUsage(int status, ProviderRetryKind? kind)
    {
        using var handler = FakeHandler.Reply(AdapterFixture.Response(), (HttpStatusCode)status);
        using var provider = AdapterFixture.Provider(handler);
        var result = await CustomProviderConsumer.ExchangeAsync(provider, AdapterFixture.Request());
        Assert.Equal(ProviderOutcome.Failed, result.Outcome); Assert.Equal(ProviderError.ProviderFailed, result.Error);
        Assert.Equal(kind, result.Retry?.Kind); Assert.Null(result.Retry?.RetryAfter); Assert.Null(result.Response);
        Assert.Equal(10, result.Observation.Usage.InputTokens); Assert.Equal(5, result.Observation.Usage.OutputTokens);
        Assert.Equal(DispatchExposure.Dispatched, result.Observation.Exposure); Assert.Equal(1, handler.Sends);
    }

    public static IEnumerable<object[]> TransportCategories() => Enum.GetValues<HttpRequestError>().Append((HttpRequestError)999)
        .Select(error => new object[] { error });

    [Theory]
    [MemberData(nameof(TransportCategories))]
    public async Task TypedTransportEvidenceIsClassifiedBeforeSanitizationAndUnknownCategoriesFailClosed(HttpRequestError error)
    {
        using var handler = new FakeHandler((_, _) => throw new HttpRequestException(error,
            "synthetic-exception-canary https://endpoint-canary.invalid", new IOException(AdapterFixture.Credential)));
        using var provider = AdapterFixture.Provider(handler);
        var result = await provider.ExchangeAsync(AdapterFixture.Request());
        Assert.Equal(ProviderOutcome.Failed, result.Outcome); Assert.Equal(ProviderError.ProviderFailed, result.Error);
        Assert.Equal(error is HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded ? ProviderRetryKind.Transient : (ProviderRetryKind?)null,
            result.Retry?.Kind);
        Assert.Equal(DispatchExposure.Unknown, result.Observation.Exposure); Assert.Null(result.Observation.Usage.InputTokens);
        Assert.Null(result.Response); Assert.Equal(1, handler.Sends);
        var ordinary = result + JsonSerializer.Serialize(result.Diagnostic);
        foreach (var canary in new[] { "synthetic-exception-canary", "endpoint-canary", AdapterFixture.Credential, AdapterFixture.Replay })
            Assert.DoesNotContain(canary, ordinary);
    }

    [Theory]
    [InlineData(401)] [InlineData(429)] [InlineData(503)]
    public async Task ExceptionStatusCannotBypassActualResponseValidationByClaimingATransportCategory(int status)
    {
        using var handler = new FakeHandler((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError,
            "synthetic-exception-canary", statusCode: (HttpStatusCode)status));
        using var provider = AdapterFixture.Provider(handler); var result = await provider.ExchangeAsync(AdapterFixture.Request());
        Assert.Equal(ProviderOutcome.Failed, result.Outcome); Assert.Null(result.Retry); Assert.Equal(1, handler.Sends);
        Assert.Equal(DispatchExposure.Unknown, result.Observation.Exposure);
    }

    [Theory]
    [InlineData(null, null)] [InlineData("0", 0)] [InlineData("12", 12)] [InlineData("600", 600)]
    [InlineData("601", null)] [InlineData("-1", null)] [InlineData("1.5", null)] [InlineData("garbage", null)]
    [InlineData("999999999999999999999999999999", null)] [InlineData("1, 2", null)]
    public async Task DeltaHintsAreOptionalBoundedAndCannotCauseAnotherSend(string? hint, int? seconds)
    {
        using var handler = new FakeHandler((_, _) =>
        {
            var response = AdapterFixture.Http(AdapterFixture.Response(), HttpStatusCode.TooManyRequests);
            if (hint is not null) response.Headers.TryAddWithoutValidation("Retry-After", hint);
            return Task.FromResult(response);
        });
        using var provider = AdapterFixture.Provider(handler);
        var result = await provider.ExchangeAsync(AdapterFixture.Request());
        Assert.Equal(ProviderRetryKind.Throttled, result.Retry!.Kind);
        Assert.Equal(seconds is { } value ? TimeSpan.FromSeconds(value) : (TimeSpan?)null, result.Retry.RetryAfter);
        Assert.Equal(1, handler.Sends);
    }

    [Theory]
    [InlineData("valid-date")] [InlineData("past-date")] [InlineData("excessive-date")]
    [InlineData("duplicate")] [InlineData("ineligible")] [InlineData("success")]
    public async Task DateAndConflictingHintsRemainAdvisoryAndNeverPromoteIneligibleOutcomes(string mode)
    {
        using var handler = new FakeHandler((_, _) =>
        {
            var status = mode == "ineligible" ? HttpStatusCode.Unauthorized : mode == "success" ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable;
            var response = AdapterFixture.Http(AdapterFixture.Response(), status);
            var date = DateTimeOffset.UtcNow.AddSeconds(mode == "past-date" ? -120 : mode == "excessive-date" ? 1200 : 120);
            response.Headers.TryAddWithoutValidation("Retry-After", mode == "duplicate" ? new[] { "1", "2" } : new[] { date.ToString("R", CultureInfo.InvariantCulture) });
            response.Headers.Date = DateTimeOffset.UtcNow.AddYears(-1);
            return Task.FromResult(response);
        });
        using var provider = AdapterFixture.Provider(handler); var result = await provider.ExchangeAsync(AdapterFixture.Request());
        if (mode is "ineligible" or "success") Assert.Null(result.Retry);
        else
        {
            Assert.Equal(ProviderRetryKind.Transient, result.Retry!.Kind);
            if (mode == "valid-date") Assert.InRange(result.Retry.RetryAfter!.Value.TotalSeconds, 100, 120);
            else Assert.Null(result.Retry.RetryAfter);
        }
        Assert.Equal(1, handler.Sends);
    }

    [Theory]
    [InlineData("malformed")] [InlineData("utf8")] [InlineData("mime")] [InlineData("encoding")]
    [InlineData("bound")] [InlineData("length")] [InlineData("usage")]
    public async Task LocalErrorBodyValidationFailureKeepsHttpFailurePrecedenceWithoutRetry(string mode)
    {
        using var handler = new FakeHandler((_, _) =>
        {
            var response = AdapterFixture.Http(mode == "malformed" ? "{malformed" : mode == "usage"
                ? "{\"usage\":{\"prompt_tokens\":-1,\"completion_tokens\":5}}" : AdapterFixture.Response(), HttpStatusCode.ServiceUnavailable);
            if (mode == "utf8") response.Content = new ByteArrayContent([0xff]);
            if (mode == "mime") response.Content.Headers.ContentType = new("text/html");
            if (mode == "encoding") response.Content.Headers.ContentEncoding.Add("gzip");
            if (mode == "length") response.Content.Headers.ContentLength = 999;
            response.Headers.TryAddWithoutValidation("Retry-After", "1");
            return Task.FromResult(response);
        });
        using var provider = AdapterFixture.Provider(handler, responseCap: mode == "bound" ? 32 : 524288);
        var result = await provider.ExchangeAsync(AdapterFixture.Request());
        Assert.Equal(ProviderOutcome.Failed, result.Outcome); Assert.Equal(ProviderError.ProviderFailed, result.Error);
        Assert.Null(result.Retry); Assert.Null(result.Response); Assert.Equal(1, handler.Sends);
        Assert.Equal(DispatchExposure.Dispatched, result.Observation.Exposure);
        if (mode is "mime" or "encoding") Assert.Equal(10, result.Observation.Usage.InputTokens);
        if (mode == "usage") Assert.Equal(5, result.Observation.Usage.OutputTokens);
        if (mode is "malformed" or "utf8" or "bound" or "length") Assert.Equal(UsageCompleteness.Unavailable, result.Observation.Usage.Completeness);
    }

    [Theory]
    [InlineData("pre-cancel")] [InlineData("caller")] [InlineData("unrelated")] [InlineData("timeout")] [InlineData("generic")]
    public async Task CancellationAttributionAndOwnedTimeoutCannotTurnOtherFailuresIntoEligibility(string mode)
    {
        using var caller = new CancellationTokenSource();
        using var handler = new FakeHandler(async (_, token) =>
        {
            if (mode == "caller") { caller.Cancel(); token.ThrowIfCancellationRequested(); }
            if (mode == "unrelated") throw new OperationCanceledException("retry transient", new CancellationToken(true));
            if (mode == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new HttpRequestException("retry transient throttled CANARY");
        });
        if (mode == "pre-cancel") caller.Cancel();
        using var provider = AdapterFixture.Provider(handler, timeout: mode == "timeout" ? TimeSpan.FromMilliseconds(30) : null);
        var result = await provider.ExchangeAsync(AdapterFixture.Request(), caller.Token);
        Assert.Equal(mode is "pre-cancel" or "caller" ? ProviderOutcome.Cancelled : ProviderOutcome.Failed, result.Outcome);
        Assert.Equal(mode == "timeout" ? ProviderRetryKind.Transient : (ProviderRetryKind?)null, result.Retry?.Kind);
        Assert.Equal(mode == "pre-cancel" ? 0 : 1, handler.Sends); Assert.Null(result.Response);
    }

    [Fact]
    public async Task EligibleActualAdapterFailureStillStopsProductionRuntimeAfterOneAttempt()
    {
        using var handler = FakeHandler.Reply(AdapterFixture.Response(), HttpStatusCode.TooManyRequests);
        using var provider = AdapterFixture.Provider(handler);
        var result = await RuntimeFixture.Agent(provider).ExecuteAsync(RuntimeFixture.Request(units: 4));
        Assert.Equal(AgentTerminationReason.Failed, result.Reason); Assert.Equal(1, handler.Sends);
        Assert.Equal(10, Assert.Single(result.Usage!.Attempts).Usage.InputTokens);
    }

    [Theory]
    [InlineData(200, ProviderRetryKind.Transient)] [InlineData(503, null)]
    public async Task InterruptedBodyUsesTypedTransportEvidenceUnlessErrorStatusRequiresConservativeOmission(int status, ProviderRetryKind? kind)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new InterruptedContent() }));
        using var provider = AdapterFixture.Provider(handler); var result = await provider.ExchangeAsync(AdapterFixture.Request());
        Assert.Equal(ProviderOutcome.Failed, result.Outcome); Assert.Equal(kind, result.Retry?.Kind);
        Assert.Equal(DispatchExposure.Dispatched, result.Observation.Exposure); Assert.Null(result.Observation.Usage.InputTokens);
        Assert.Equal(1, handler.Sends);
    }

    [Fact]
    public async Task RejectedSuccessPayloadRetainsUsageButCannotAcquireHintOrRetryMetadata()
    {
        using var handler = new FakeHandler((_, _) =>
        {
            var response = AdapterFixture.Http(AdapterFixture.Response(finish: "length"));
            response.Headers.TryAddWithoutValidation("Retry-After", "1"); return Task.FromResult(response);
        });
        using var provider = AdapterFixture.Provider(handler); var result = await provider.ExchangeAsync(AdapterFixture.Request());
        Assert.Equal(ProviderOutcome.Rejected, result.Outcome); Assert.Null(result.Retry); Assert.Null(result.Response);
        Assert.Equal(10, result.Observation.Usage.InputTokens); Assert.Equal(1, handler.Sends);
    }

    [Fact]
    public async Task ErrorResponseAfterExternalObservationSealCannotSupplyLateRetryOrUsage()
    {
        var entered = RuntimeFixture.Barrier(); var release = RuntimeFixture.Barrier();
        using var handler = new FakeHandler(async (_, _) =>
        {
            entered.SetResult(); await release.Task; return AdapterFixture.Http(AdapterFixture.Response(), HttpStatusCode.TooManyRequests);
        });
        using var provider = AdapterFixture.Provider(handler); var request = AdapterFixture.Request();
        var pending = provider.ExchangeAsync(request).AsTask(); await RuntimeFixture.Await(entered.Task);
        var sealedObservation = request.Observation.Seal(); release.SetResult();
        var result = await RuntimeFixture.Await(pending);
        Assert.Equal(ProviderError.ObservationClosed, result.Error); Assert.Null(result.Retry); Assert.Null(result.Response);
        Assert.Same(sealedObservation, result.Observation); Assert.Null(result.Observation.Usage.InputTokens); Assert.Equal(1, handler.Sends);
    }

    private sealed class InterruptedContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(new HttpRequestException(HttpRequestError.ResponseEnded, "synthetic-read-canary"));
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}

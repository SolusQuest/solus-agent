using System.Net;
using System.Text.Json.Nodes;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.Adapter;

public sealed class UsageRetentionTests
{
    [Theory]
    [InlineData("{}", null, null, UsageCompleteness.Unavailable)]
    [InlineData("null", null, null, UsageCompleteness.Unavailable)]
    [InlineData("{\"prompt_tokens\":0,\"completion_tokens\":0}", 0L, 0L, UsageCompleteness.Complete)]
    [InlineData("{\"prompt_tokens\":10}", 10L, null, UsageCompleteness.Partial)]
    [InlineData("{\"completion_tokens\":5}", null, 5L, UsageCompleteness.Partial)]
    [InlineData("{\"total_tokens\":15}", null, null, UsageCompleteness.Unavailable)]
    [InlineData("{\"prompt_tokens\":9223372036854775807}", long.MaxValue, null, UsageCompleteness.Partial)]
    public async Task MissingCountersAreUnknownAndKnownZeroOrMaximumAreRetained(string usage, long? input, long? output, UsageCompleteness completeness)
    {
        using var handler = FakeHandler.Reply(WithUsage(usage)); using var provider = AdapterFixture.Provider(handler);
        var result = await provider.ExchangeAsync(AdapterFixture.Request()); Assert.Equal(ProviderOutcome.Succeeded, result.Outcome);
        Assert.Equal(input, result.Observation.Usage.InputTokens); Assert.Equal(output, result.Observation.Usage.OutputTokens);
        Assert.Equal(completeness, result.Observation.Usage.Completeness);
    }
    [Fact]
    public async Task EquivalentCacheFieldsAreOneInputSubsetAndReasoningIsOneOutputSubset()
    {
        using var handler = FakeHandler.Reply(WithUsage("""{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15,"prompt_cache_hit_tokens":4,"prompt_cache_miss_tokens":6,"prompt_tokens_details":{"cached_tokens":4},"completion_tokens_details":{"reasoning_tokens":3}}"""));
        using var provider = AdapterFixture.Provider(handler); var result = await provider.ExchangeAsync(AdapterFixture.Request());
        Assert.Equal(ProviderOutcome.Succeeded, result.Outcome); var usage = result.Observation.Usage;
        Assert.Equal(10, usage.InputTokens); Assert.Equal(5, usage.OutputTokens); Assert.Equal(3, usage.ProviderCounters.Count);
        Assert.Equal(4, usage.ProviderCounters.Single(c => c.Kind == ProviderTokenCounterKind.CacheRead).Value);
        Assert.Equal(6, usage.ProviderCounters.Single(c => c.Kind == ProviderTokenCounterKind.UncachedInput).Value);
        Assert.Equal(TokenCounterRelationship.IncludedInInput, usage.ProviderCounters.Single(c => c.Kind == ProviderTokenCounterKind.CacheRead).Relationship);
        Assert.Equal(3, usage.ProviderCounters.Single(c => c.Kind == ProviderTokenCounterKind.Reasoning).Value);
        Assert.Equal(TokenCounterRelationship.IncludedInOutput, usage.ProviderCounters.Single(c => c.Kind == ProviderTokenCounterKind.Reasoning).Relationship);
        Assert.DoesNotContain(usage.ProviderCounters, c => c.Kind == ProviderTokenCounterKind.CacheWrite);
    }
    [Theory]
    [InlineData("{\"prompt_cache_hit_tokens\":4}", ProviderTokenCounterKind.CacheRead, 4L)]
    [InlineData("{\"prompt_tokens_details\":{\"cached_tokens\":4}}", ProviderTokenCounterKind.CacheRead, 4L)]
    [InlineData("{\"prompt_cache_miss_tokens\":6}", ProviderTokenCounterKind.UncachedInput, 6L)]
    [InlineData("{\"completion_tokens_details\":{\"reasoning_tokens\":3}}", ProviderTokenCounterKind.Reasoning, 3L)]
    public async Task OptionalDetailsDoNotInventCoreOrMissingPartitionPeer(string json, ProviderTokenCounterKind kind, long value)
    {
        using var handler = FakeHandler.Reply(WithUsage(json)); using var provider = AdapterFixture.Provider(handler);
        var result = await provider.ExchangeAsync(AdapterFixture.Request()); Assert.Equal(ProviderOutcome.Succeeded, result.Outcome);
        Assert.Null(result.Observation.Usage.InputTokens); Assert.Null(result.Observation.Usage.OutputTokens);
        Assert.Equal(UsageCompleteness.Partial, result.Observation.Usage.Completeness);
        var detail = Assert.Single(result.Observation.Usage.ProviderCounters); Assert.Equal(kind, detail.Kind); Assert.Equal(value, detail.Value);
    }
    [Theory]
    [InlineData("{\"prompt_tokens\":-1,\"completion_tokens\":5}", null, 5L)]
    [InlineData("{\"prompt_tokens\":1.0,\"completion_tokens\":5}", null, 5L)]
    [InlineData("{\"prompt_tokens\":1e2,\"completion_tokens\":5}", null, 5L)]
    [InlineData("{\"prompt_tokens\":9223372036854775808,\"completion_tokens\":5}", null, 5L)]
    [InlineData("{\"prompt_tokens\":10,\"completion_tokens\":\"5\"}", 10L, null)]
    [InlineData("{\"prompt_tokens\":10,\"prompt_tokens\":20,\"completion_tokens\":5}", null, 5L)]
    [InlineData("{\"prompt_tokens\":10,\"completion_tokens\":5,\"prompt_cache_hit_tokens\":11}", 10L, 5L)]
    [InlineData("{\"prompt_tokens\":10,\"completion_tokens\":5,\"prompt_cache_hit_tokens\":4,\"prompt_tokens_details\":{\"cached_tokens\":3}}", 10L, 5L)]
    [InlineData("{\"prompt_tokens\":10,\"completion_tokens\":5,\"prompt_cache_hit_tokens\":8,\"prompt_cache_miss_tokens\":8}", 10L, 5L)]
    [InlineData("{\"prompt_tokens\":10,\"completion_tokens\":5,\"completion_tokens_details\":{\"reasoning_tokens\":6}}", 10L, 5L)]
    [InlineData("{\"prompt_tokens\":10,\"completion_tokens\":5,\"total_tokens\":99}", 10L, 5L)]
    [InlineData("{\"prompt_tokens\":10,\"completion_tokens\":5,\"completion_tokens_details\":4}", 10L, 5L)]
    public async Task InvalidOrContradictoryMeasurementRejectsPayloadWithoutErasingOtherValidatedFacts(string json, long? input, long? output)
    {
        using var handler = FakeHandler.Reply(WithUsage(json)); using var provider = AdapterFixture.Provider(handler);
        var result = await provider.ExchangeAsync(AdapterFixture.Request()); Assert.Equal(ProviderError.InvalidResponse, result.Error);
        Assert.Null(result.Response); Assert.Equal(input, result.Observation.Usage.InputTokens); Assert.Equal(output, result.Observation.Usage.OutputTokens);
        Assert.Empty(result.Observation.Usage.ProviderCounters);
    }
    [Fact]
    public async Task DuplicateUsageEnvelopeIsUnavailableButDuplicatePayloadRetainsUniqueUsage()
    {
        var body = WithUsage("{\"prompt_tokens\":10}"); var usageStart = body.LastIndexOf("\"usage\":", StringComparison.Ordinal);
        var duplicated = body.Insert(usageStart, "\"usage\":{\"prompt_tokens\":20},");
        using var handler = FakeHandler.Reply(duplicated); using var provider = AdapterFixture.Provider(handler);
        var result = await provider.ExchangeAsync(AdapterFixture.Request()); Assert.Equal(ProviderError.InvalidResponse, result.Error);
        Assert.Equal(UsageCompleteness.Unavailable, result.Observation.Usage.Completeness); Assert.Null(result.Response);
    }
    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(307)]
    public async Task HttpErrorOrRedirectRetainsUsageAndDoesNotRetry(int status)
    {
        using var handler = FakeHandler.Reply(WithUsage("{\"prompt_tokens\":10}"), (HttpStatusCode)status);
        using var provider = AdapterFixture.Provider(handler); var result = await provider.ExchangeAsync(AdapterFixture.Request());
        Assert.Equal(ProviderOutcome.Failed, result.Outcome); Assert.Equal(ProviderError.ProviderFailed, result.Error);
        Assert.Equal(10, result.Observation.Usage.InputTokens); Assert.Null(result.Observation.Usage.OutputTokens);
        Assert.Equal(DispatchExposure.Dispatched, result.Observation.Exposure); Assert.Null(result.Response); Assert.Equal(1, handler.Sends);
    }
    [Theory]
    [InlineData("synthetic-non-json-error-body")]
    [InlineData("{malformed")]
    public async Task MalformedErrorBodyStillHasHttpFailureClassification(string body)
    {
        using var handler = FakeHandler.Reply(body, HttpStatusCode.BadGateway); using var provider = AdapterFixture.Provider(handler);
        var result = await provider.ExchangeAsync(AdapterFixture.Request()); Assert.Equal(ProviderOutcome.Failed, result.Outcome);
        Assert.Equal(ProviderError.ProviderFailed, result.Error); Assert.Null(result.Response);
        Assert.Equal(UsageCompleteness.Unavailable, result.Observation.Usage.Completeness);
    }
    private static string WithUsage(string usage)
    { var node = JsonNode.Parse(AdapterFixture.Response())!; node["usage"] = JsonNode.Parse(usage); return node.ToJsonString(); }
}

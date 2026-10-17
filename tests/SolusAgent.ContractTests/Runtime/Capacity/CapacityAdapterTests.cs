using SolusAgent.Api.Usage;
using SolusAgent.Providers.DeepSeek;
using SolusAgent.Runtime.Api.Providers;
using Xunit;
using SolusAgent.ContractTests.DeepSeek.Adapter;

namespace SolusAgent.ContractTests.Runtime.Capacity;

public sealed class CapacityAdapterTests
{
    [Fact]
    public async Task LargerGenericReplayBoundDoesNotExpandTheAdapterFrameProfile()
    {
        var handler = FakeHandler.Reply(AdapterFixture.Response(reasoning: new string('r', 8192)));
        using var adapter = AdapterFixture.Provider(handler);
        var request = new ProviderRequest(adapter.Scope, new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), [ProviderInput.Data("synthetic input")],
            requiredCapabilities: ProviderCapabilities.Continuation, bounds: CapacityFixture.Bounds);
        var result = await adapter.ExchangeAsync(request);
        Assert.Equal(ProviderOutcome.Rejected, result.Outcome); Assert.Equal(ProviderError.LimitExceeded, result.Error);
        Assert.Equal(1, handler.Sends); Assert.Equal(10, result.Observation.Usage.InputTokens);
    }

    [Fact]
    public async Task GenericOptInDoesNotRemoveIndependentEscapedWireBodyLimit()
    {
        var handler = new CountingHandler(); using var adapter = new DeepSeekProvider(new("synthetic-unused", 16), handler);
        // Logical bytes fit the generic selected profile; escaping exceeds the adapter's separate 1 MiB request body.
        var request = new ProviderRequest(adapter.Scope, new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            [ProviderInput.Data(new string('\0', 200_000))], bounds: CapacityFixture.Bounds);
        var result = await adapter.ExchangeAsync(request);
        Assert.Equal(ProviderOutcome.Rejected, result.Outcome); Assert.Equal(ProviderError.LimitExceeded, result.Error);
        Assert.Equal(0, handler.Sends); Assert.Equal(DispatchExposure.NotDispatched, result.Observation.Exposure);
        Assert.Null(result.Observation.Usage.InputTokens);
    }
    private sealed class CountingHandler : HttpMessageHandler
    {
        internal int Sends;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Sends++; throw new InvalidOperationException("Synthetic boundary must refuse before HTTP."); }
    }
}

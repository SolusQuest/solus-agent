using System.Text;
using SolusAgent.Providers.DeepSeek;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.Adapter;

public sealed class PersistenceTests
{
    private static ProviderContextBinding Binding(string? reasoning = AdapterFixture.Replay)
    {
        var attempt = AdapterFixture.Request().Attempt;
        return new(AdapterFixture.Scope, attempt, reasoning is null ? null
            : new(AdapterFixture.Scope, attempt, [1, .. Encoding.UTF8.GetBytes(reasoning)]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(AdapterFixture.Replay)]
    public void ExactCurrentFormatIsPureImmutableAndAllowsNewCredentials(string? reasoning)
    {
        var handler = FakeHandler.Reply(AdapterFixture.Response());
        using var producer = AdapterFixture.Provider(handler);
        var binding = Binding(reasoning);
        var state = Assert.IsType<ProviderSavedState>(producer.ExportContext(binding));
        Assert.Equal(1, state.FormatVersion); Assert.Equal(36, state.PayloadByteCount);
        Assert.Equal("DSCP", Encoding.ASCII.GetString(state.CopyRestrictedPayload()[..4]));
        var bytes = state.CopyRestrictedPayload(); bytes[0] = 0;
        binding.Continuation?.CopyReplayBytes().AsSpan().Clear();
        var freshHandler = FakeHandler.Reply(AdapterFixture.Response());
        using var consumer = new DeepSeekProvider(new("rotated-synthetic-credential", 2048), freshHandler);
        Assert.True(consumer.AdmitContext(binding, state)); Assert.True(consumer.AdmitContext(binding, state));
        Assert.Equal(0, handler.Sends); Assert.Equal(0, freshHandler.Sends);
        var ordinary = state + " " + producer + " " + binding.Continuation;
        Assert.DoesNotContain(AdapterFixture.Credential, ordinary); Assert.DoesNotContain(AdapterFixture.Replay, ordinary);
        Assert.False(Contains(state.CopyRestrictedPayload(), Encoding.UTF8.GetBytes(AdapterFixture.Credential)));
        Assert.False(Contains(state.CopyRestrictedPayload(), Encoding.UTF8.GetBytes(AdapterFixture.Replay)));
    }

    [Theory]
    [InlineData(8190)]
    [InlineData(8191)]
    public void MaximumReplayAndDistinctEarlierOriginRemainSupported(int reasoningBytes)
    {
        using var provider = AdapterFixture.Provider(FakeHandler.Reply(AdapterFixture.Response()));
        var binding = Binding(new string('r', reasoningBytes));
        Assert.Equal(reasoningBytes + 1, binding.Continuation!.ByteCount);
        var later = binding with { Origin = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2) };
        var state = Assert.IsType<ProviderSavedState>(provider.ExportContext(later));
        Assert.True(provider.AdmitContext(later, state));
        Assert.False(provider.AdmitContext(binding, state));
        Assert.Throws<ProviderContractException>(() => new ProviderContinuation(binding.Scope, binding.Origin, new byte[8193]));
        Assert.Throws<ArgumentException>(() => new ProviderSavedState(binding.Scope, binding.Origin, 1, new byte[8193]));
    }

    [Fact]
    public void EveryCommitmentByteAndMalformedGrammarRejectWithoutSending()
    {
        var handler = FakeHandler.Reply(AdapterFixture.Response()); using var provider = AdapterFixture.Provider(handler);
        var binding = Binding(); var state = provider.ExportContext(binding)!;
        for (var index = 0; index < 36; index++)
        {
            var bytes = state.CopyRestrictedPayload(); bytes[index] ^= 1;
            Assert.False(provider.AdmitContext(binding, new(binding.Scope, binding.Origin, 1, bytes)));
        }
        foreach (var length in new[] { 1, 4, 35, 37, 8192 })
            Assert.False(provider.AdmitContext(binding, new(binding.Scope, binding.Origin, 1, new byte[length])));
        Assert.False(provider.AdmitContext(binding, new(binding.Scope, binding.Origin, 2, state.CopyRestrictedPayload())));
        Assert.Equal(0, handler.Sends);
    }

    [Fact]
    public void EveryScopeAttemptAndReplayFieldIsBoundEvenWithMatchingOuterMetadata()
    {
        var handler = FakeHandler.Reply(AdapterFixture.Response()); using var provider = AdapterFixture.Provider(handler);
        var binding = Binding(); var original = binding.Origin; var state = provider.ExportContext(binding)!;
        ProviderAttempt[] attempts = [new(Guid.NewGuid(), original.LogicalCallId, original.PhysicalAttemptId),
            new(original.ExecutionId, Guid.NewGuid(), original.PhysicalAttemptId),
            new(original.ExecutionId, original.LogicalCallId, Guid.NewGuid()),
            new(original.ExecutionId, original.LogicalCallId, original.PhysicalAttemptId, 2)];
        foreach (var attempt in attempts)
        {
            var changed = binding with { Origin = attempt };
            Assert.False(provider.AdmitContext(changed, state));
            Assert.False(provider.AdmitContext(changed, new(changed.Scope, changed.Origin, 1, state.CopyRestrictedPayload())));
            changed = binding with { Continuation = new(binding.Scope, attempt, binding.Continuation!.CopyReplayBytes()) };
            Assert.False(provider.AdmitContext(changed, state));
        }
        foreach (var scope in new[] { new ProviderScope("foreign", DeepSeekOptions.Model), new("deepseek", "foreign-model") })
        {
            Assert.Null(provider.ExportContext(binding with { Scope = scope }));
            Assert.Null(provider.ExportContext(binding with { Continuation = new(scope, original, [1]) }));
            Assert.False(provider.AdmitContext(binding, new(scope, original, 1, state.CopyRestrictedPayload())));
        }
        Assert.False(provider.AdmitContext(binding with { Continuation = null }, state));
        Assert.False(provider.AdmitContext(binding with { Continuation = new(binding.Scope, original, [1, 65]) }, state));
        var absent = provider.ExportContext(binding with { Continuation = null })!;
        Assert.False(provider.AdmitContext(binding, absent));
        Assert.Equal(0, handler.Sends);
    }

    [Theory]
    [InlineData(new byte[] { 0 })]
    [InlineData(new byte[] { 2, 65 })]
    [InlineData(new byte[] { 1, 255 })]
    [InlineData(new byte[] { 1, 192, 128 })]
    [InlineData(new byte[] { 1, 237, 160, 128 })]
    public void UnknownFrameAndInvalidUtf8CannotBeExportedOrAdmitted(byte[] bytes)
    {
        var handler = FakeHandler.Reply(AdapterFixture.Response()); using var provider = AdapterFixture.Provider(handler);
        var binding = Binding(); var state = provider.ExportContext(binding)!;
        var malformed = binding with { Continuation = new(binding.Scope, binding.Origin, bytes) };
        Assert.Null(provider.ExportContext(malformed)); Assert.False(provider.AdmitContext(malformed, state));
        Assert.Equal(0, handler.Sends);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EachChangedConfigurationRejectsIndependently(int dimension)
    {
        var handler = FakeHandler.Reply(AdapterFixture.Response());
        using var producer = AdapterFixture.Provider(FakeHandler.Reply(AdapterFixture.Response()));
        var binding = Binding(); var state = producer.ExportContext(binding)!;
        using var consumer = new DeepSeekProvider(new(AdapterFixture.Credential, dimension == 0 ? 2049 : 2048,
            maximumRequestBodyBytes: dimension == 1 ? 1048575 : 1048576,
            maximumResponseBodyBytes: dimension == 2 ? 524287 : 524288,
            timeout: dimension == 3 ? TimeSpan.FromSeconds(119) : TimeSpan.FromSeconds(120)), handler);
        Assert.False(consumer.AdmitContext(binding, state)); Assert.Equal(0, handler.Sends);
    }

    [Fact]
    public async Task ConcurrentBindingsRemainSeparateAndDisposalDisablesPersistence()
    {
        var handler = FakeHandler.Reply(AdapterFixture.Response()); var provider = AdapterFixture.Provider(handler);
        var bindings = Enumerable.Range(0, 16).Select(i => Binding("reasoning-" + i)).ToArray();
        var states = await Task.WhenAll(bindings.Select(binding => Task.Run(() => provider.ExportContext(binding)!)));
        for (var i = 0; i < states.Length; i++)
        {
            Assert.True(provider.AdmitContext(bindings[i], states[i]));
            Assert.False(provider.AdmitContext(bindings[(i + 1) % states.Length], states[i]));
        }
        provider.Dispose(); Assert.Null(provider.ExportContext(bindings[0]));
        Assert.False(provider.AdmitContext(bindings[0], states[0])); Assert.Equal(0, handler.Sends);
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}

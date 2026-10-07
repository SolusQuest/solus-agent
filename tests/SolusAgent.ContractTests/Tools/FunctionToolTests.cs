using System.Text;
using CustomTools;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Tools;

/// <summary>Executable producer/consumer proofs of preparation, authority, association and effect honesty.</summary>
public sealed class FunctionToolTests
{
    [Fact]
    public async Task PreparationIsEffectFreeAndExplicitInvocationIsAssociatedAndSingleUse()
    {
        IFunctionTool tool = new CounterTool();
        var capability = new CounterCapability();
        var bytes = Encoding.UTF8.GetBytes("{\"amount\":3}");
        var call = ToolCall.FromUtf8("Call-A", "counter", bytes);
        var prepared = tool.Prepare(call);
        Array.Fill(bytes, (byte)'x');
        Assert.True(prepared.Accepted);
        Assert.True(tool.Prepare(call).Accepted);
        Assert.Equal(0, capability.Effects);
        Assert.Equal("{\"amount\":3}", prepared.Prepared!.Call.ArgumentsJson);
        var result = await tool.InvokeAsync(prepared.Prepared, call, capability);
        Assert.Same(call, result.Call);
        Assert.Equal(ToolOutcome.Succeeded, result.Outcome);
        Assert.Equal(ToolError.None, result.Error);
        Assert.True(result.InvocationStarted);
        Assert.Equal("{\"total\":3}", result.Json);
        Assert.Equal(1, capability.Effects);
        Assert.Equal(ToolError.AlreadyInvoked, (await tool.InvokeAsync(prepared.Prepared, call, capability)).Error);
        Assert.Equal(1, capability.Effects);
    }

    [Theory]
    [InlineData("{", ToolError.InvalidJson)]
    [InlineData("{}", ToolError.InvalidArguments)]
    [InlineData("{\"amount\":1,\"extra\":true}", ToolError.InvalidArguments)]
    [InlineData("{\"amount\":1,\"amount\":2}", ToolError.InvalidJson)]
    [InlineData("{\"amount\":1,\"a\\u006dount\":2}", ToolError.InvalidJson)]
    [InlineData("{\"amount\":\"1\"}", ToolError.InvalidArguments)]
    [InlineData("{\"amount\":true}", ToolError.InvalidArguments)]
    [InlineData("{\"amount\":null}", ToolError.InvalidArguments)]
    [InlineData("{\"amount\":1.0}", ToolError.InvalidArguments)]
    [InlineData("{\"amount\":1e0}", ToolError.InvalidArguments)]
    [InlineData("{\"amount\":9223372036854775808}", ToolError.InvalidArguments)]
    [InlineData("{\"amount\":-1}", ToolError.DomainRejected)]
    [InlineData("{\"amount\":101}", ToolError.DomainRejected)]
    [InlineData("{\"amount\":[[[[0]]]]}", ToolError.InvalidJson)]
    public void ActualArgumentAndDomainFailuresRejectBeforeEffects(string json, ToolError expected)
    {
        var capability = new CounterCapability();
        var tool = new CounterTool();
        var result = tool.Prepare(new("call", "counter", json));
        Assert.False(result.Accepted);
        Assert.Null(result.Prepared);
        Assert.Equal(expected, result.Error);
        Assert.Equal(0, capability.Effects);
    }

    [Fact]
    public void DescriptorByteLimitChecksActualArguments()
    {
        var json = "{\"amount\":1}";
        var call = new ToolCall("id", "counter", json);
        Assert.True(new CounterTool(maximumArgumentBytes: Encoding.UTF8.GetByteCount(json)).Prepare(call).Accepted);
        Assert.Equal(ToolError.LimitExceeded, new CounterTool(maximumArgumentBytes: Encoding.UTF8.GetByteCount(json) - 1).Prepare(call).Error);
    }

    [Theory]
    [InlineData("other", "counter", "{\"amount\":1}")]
    [InlineData("id", "other", "{\"amount\":1}")]
    [InlineData("id", "counter", "{\"amount\":2}")]
    [InlineData("id", "counter", "{ \"amount\":1}")]
    public async Task FullExpectedAssociationCannotRebindAPreparedCall(string id, string name, string json)
    {
        var tool = new CounterTool();
        var original = new ToolCall("id", "counter", "{\"amount\":1}");
        var capability = new CounterCapability();
        var prepared = tool.Prepare(original).Prepared!;
        var expected = new ToolCall(id, name, json);
        var result = await tool.InvokeAsync(prepared, expected, capability);
        Assert.Equal(ToolError.CallMismatch, result.Error);
        Assert.Same(expected, result.Call);
        Assert.False(result.InvocationStarted);
        Assert.Equal(0, capability.Effects);
        Assert.Equal(ToolOutcome.Succeeded, (await tool.InvokeAsync(prepared, original, capability)).Outcome);
    }

    [Fact]
    public async Task ToolInstanceBindingCannotBeReusedByAnIdenticallyNamedTool()
    {
        var first = new CounterTool();
        var second = new CounterTool();
        var call = new ToolCall("id", "counter", "{\"amount\":1}");
        var capability = new CounterCapability();
        Assert.Equal(ToolError.PreparedMismatch, (await second.InvokeAsync(first.Prepare(call).Prepared!, call, capability)).Error);
        Assert.Equal(ToolError.CallMismatch, first.Prepare(new("id", "other", "{}")).Error);
        Assert.Equal(0, capability.Effects);
    }

    [Fact]
    public async Task ReadOnlyMetadataDoesNotAuthorizeMissingWrongOrSpoofedCapability()
    {
        var tool = new CounterTool(effect: ToolEffect.ReadOnly);
        var call = new ToolCall("id", "counter", "{\"amount\":1}");
        var prepared = tool.Prepare(call).Prepared!;
        foreach (var denied in new IToolCapability?[] { null, new WrongTypeCapability(), new CounterCapability("other"), new ThrowingCapability() })
        {
            var result = await tool.InvokeAsync(prepared, call, denied);
            Assert.Equal(ToolError.UnsupportedCapability, result.Error);
            Assert.False(result.InvocationStarted);
        }
        var accepted = new CounterCapability();
        Assert.Equal(ToolOutcome.Succeeded, (await tool.InvokeAsync(prepared, call, accepted)).Outcome);
        Assert.Equal(1, accepted.Effects);
    }

    [Theory]
    [InlineData(CounterMode.ThrowAfterEffect, ToolOutcome.Failed, ToolError.InvocationFailed)]
    [InlineData(CounterMode.FailAfterEffect, ToolOutcome.Failed, ToolError.InvocationFailed)]
    [InlineData(CounterMode.UnrelatedCancellation, ToolOutcome.Failed, ToolError.InvocationFailed)]
    [InlineData(CounterMode.WrongAssociation, ToolOutcome.Rejected, ToolError.ResultMismatch)]
    [InlineData(CounterMode.OversizedResult, ToolOutcome.Rejected, ToolError.InvalidResult)]
    [InlineData(CounterMode.InvalidResult, ToolOutcome.Rejected, ToolError.InvalidResult)]
    [InlineData(CounterMode.InvalidResultEncoding, ToolOutcome.Rejected, ToolError.InvalidResult)]
    [InlineData(CounterMode.DuplicateResult, ToolOutcome.Rejected, ToolError.InvalidResult)]
    [InlineData(CounterMode.MalformedResult, ToolOutcome.Rejected, ToolError.InvalidResult)]
    [InlineData(CounterMode.WrongArguments, ToolOutcome.Rejected, ToolError.ResultMismatch)]
    public async Task PostEffectFailuresAreAssociatedAndNeverImplyRollbackOrRetry(CounterMode mode, ToolOutcome outcome, ToolError error)
    {
        var tool = new CounterTool(mode);
        var call = new ToolCall("id", "counter", "{\"amount\":1}");
        var capability = new CounterCapability();
        var prepared = tool.Prepare(call).Prepared!;
        var result = await tool.InvokeAsync(prepared, call, capability);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(error, result.Error);
        Assert.True(result.InvocationStarted);
        Assert.Same(call, result.Call);
        Assert.Null(result.Json);
        Assert.Equal(1, capability.Effects);
        Assert.DoesNotContain("synthetic-private-detail", result.ToString());
        Assert.Equal(ToolError.AlreadyInvoked, (await tool.InvokeAsync(prepared, call, capability)).Error);
        Assert.Equal(1, capability.Effects);
    }

    [Fact]
    public async Task ResultLimitIsEnforcedAtExactBytesAndOneOver()
    {
        const string json = "{\"total\":1}";
        var call = new ToolCall("id", "counter", "{\"amount\":1}");
        var exact = new CounterTool(maximumResultBytes: json.Length);
        Assert.Equal(json, (await exact.InvokeAsync(exact.Prepare(call).Prepared!, call, new CounterCapability())).Json);
        var small = new CounterTool(maximumResultBytes: json.Length - 1);
        var result = await small.InvokeAsync(small.Prepare(call).Prepared!, call, new CounterCapability());
        Assert.Equal(ToolError.InvalidResult, result.Error);
        Assert.True(result.InvocationStarted);
    }

    [Fact]
    public async Task PreCancellationHasZeroEffectsAndDoesNotConsumeAuthority()
    {
        var tool = new CounterTool();
        var call = new ToolCall("id", "counter", "{\"amount\":1}");
        var capability = new CounterCapability();
        var prepared = tool.Prepare(call).Prepared!;
        var result = await tool.InvokeAsync(prepared, call, capability, new CancellationToken(true));
        Assert.Equal(ToolOutcome.Cancelled, result.Outcome);
        Assert.False(result.InvocationStarted);
        Assert.Equal(0, capability.Effects);
        Assert.Equal(ToolOutcome.Succeeded, (await tool.InvokeAsync(prepared, call, capability)).Outcome);
    }

    [Fact]
    public async Task InFlightCancellationAndConcurrentReplayKeepEffectsHonest()
    {
        using var cancellation = new CancellationTokenSource();
        var tool = new CounterTool(CounterMode.WaitAfterEffect);
        var call = new ToolCall("id", "counter", "{\"amount\":1}");
        var capability = new CounterCapability();
        var prepared = tool.Prepare(call).Prepared!;
        var first = tool.InvokeAsync(prepared, call, capability, cancellation.Token).AsTask();
        await tool.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var repeats = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => tool.InvokeAsync(prepared, call, capability).AsTask()));
        Assert.All(repeats, result => Assert.Equal(ToolError.AlreadyInvoked, result.Error));
        cancellation.Cancel();
        var result = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ToolOutcome.Cancelled, result.Outcome);
        Assert.True(result.InvocationStarted);
        Assert.Same(call, result.Call);
        Assert.Null(result.Json);
        Assert.Equal(1, capability.Effects);
    }

    [Fact]
    public async Task CancellationSignalledAfterACompletedEffectDoesNotEraseSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        var tool = new CounterTool(CounterMode.SignalCancellationThenSucceed, completionCancellation: cancellation);
        var call = new ToolCall("id", "counter", "{\"amount\":1}");
        var result = await tool.InvokeAsync(tool.Prepare(call).Prepared!, call, new CounterCapability(), cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(ToolOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public void BoundedHostBatchAdmissionRefusesInvalidAmbiguousOrUnauthorizedMembersBeforeEffects()
    {
        var tool = new CounterTool();
        var capability = new CounterCapability();
        ToolCall[] valid = [new("a", "counter", "{\"amount\":1}"), new("b", "counter", "{\"amount\":2}")];
        Assert.True(Admit(valid, capability));
        Assert.False(Admit([valid[0], new("b", "counter", "{}")], capability));
        Assert.False(Admit([valid[0], new("a", "counter", "{\"amount\":2}")], capability));
        Assert.False(Admit(valid, null));
        Assert.False(Admit(Enumerable.Repeat(valid[0], 5).ToArray(), capability));
        Assert.False(Admit([new("c", "counter", new string(' ', 60) + "{\"amount\":1}"), valid[0]], capability));
        Assert.Equal(0, capability.Effects);

        bool Admit(ToolCall[] calls, CounterCapability? supplied)
        {
            // Synthetic host policy, deliberately absent from production Tools.Api.
            if (calls.Length is < 1 or > 4 || calls.Sum(c => Encoding.UTF8.GetByteCount(c.ArgumentsJson)) > 64) { return false; }
            var preparations = calls.Select(tool.Prepare).ToArray();
            return preparations.All(p => p.Accepted) && calls.Select(c => c.CallId).Distinct(StringComparer.Ordinal).Count() == calls.Length
                && supplied?.CapabilityId == tool.Descriptor.CapabilityId;
        }
    }

    private sealed class WrongTypeCapability : IToolCapability { public string CapabilityId => "counter_increment"; }
    private sealed class ThrowingCapability : IToolCapability { public string CapabilityId => throw new InvalidOperationException("synthetic-private-detail"); }
}

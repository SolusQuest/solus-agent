using System.Collections;
using System.Text;
using System.Text.Json;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Providers;

public sealed class ProviderBoundsTests
{
    [Fact]
    public void RequestCountsRejectBeforeCopyingAndTextUsesStrictUtf8Bytes()
    {
        var scope = new ProviderScope("p", "m");
        _ = new ProviderRequest(scope, ProviderExchangeTests.Attempt(), Enumerable.Repeat(ProviderInput.Data(""), ProviderLimits.Inputs).ToArray());
        Assert.Equal(ProviderError.LimitExceeded, Assert.Throws<ProviderContractException>(() =>
            new ProviderRequest(scope, ProviderExchangeTests.Attempt(), new OversizedInputs())).Error);
        Assert.Equal(ProviderError.LimitExceeded, Assert.Throws<ProviderContractException>(() => ProviderInput.Data(new('é', ProviderLimits.TextBytes / 2 + 1))).Error);
        Assert.Equal(ProviderLimits.TextBytes, Encoding.UTF8.GetByteCount(ProviderInput.Data(new('é', ProviderLimits.TextBytes / 2)).Text!));
        Assert.Equal(ProviderError.InvalidEncoding, Assert.Throws<ProviderContractException>(() => ProviderInput.Data("\uD800")).Error);
        Assert.Equal(ProviderError.InvalidEncoding, Assert.Throws<ProviderContractException>(() => ProviderInput.DataFromUtf8([0xFF])).Error);
        Assert.Equal(ProviderError.LimitExceeded, Assert.Throws<ProviderContractException>(() => ProviderInput.DataFromUtf8(new byte[ProviderLimits.TextBytes + 1])).Error);
    }

    [Fact]
    public void AggregateRequestExactAndOverBoundIncludesScopeEvenWithoutInputs()
    {
        var scope = new ProviderScope("p", "m");
        var inputs = Enumerable.Repeat(ProviderInput.Data(new('x', ProviderLimits.TextBytes)), 7)
            .Append(ProviderInput.Data(new('x', ProviderLimits.TextBytes - 2))).ToArray();
        var request = new ProviderRequest(scope, ProviderExchangeTests.Attempt(), inputs);
        Assert.Equal(ProviderLimits.RequestBytes, request.PayloadByteCount);
        inputs[^1] = ProviderInput.Data(new('x', ProviderLimits.TextBytes - 1));
        Assert.Equal(ProviderError.LimitExceeded, Assert.Throws<ProviderContractException>(() => new ProviderRequest(scope, ProviderExchangeTests.Attempt(), inputs)).Error);
        _ = new ProviderRequest(scope, ProviderExchangeTests.Attempt(), [ProviderInput.Data("é")], bounds: new(maximumRequestBytes: 4));
        Assert.Throws<ProviderContractException>(() => new ProviderRequest(scope, ProviderExchangeTests.Attempt(), [ProviderInput.Data("é")], bounds: new(maximumRequestBytes: 3)));
        Assert.Throws<ProviderContractException>(() => new ProviderRequest(scope, ProviderExchangeTests.Attempt(), [], bounds: new(maximumRequestBytes: 1)));
    }

    [Fact]
    public void HostDefinitionsHaveBothCountAndAggregateBounds()
    {
        var definitions = Enumerable.Range(0, ProviderLimits.Tools).Select(i => new ProbeTool("echo_" + i).Descriptor).ToArray();
        _ = new ProviderRequest(ProviderExchangeTests.Scope, ProviderExchangeTests.Attempt(), [], definitions);
        Assert.Equal(ProviderError.LimitExceeded, Assert.Throws<ProviderContractException>(() => new ProviderRequest(
            ProviderExchangeTests.Scope, ProviderExchangeTests.Attempt(), [], definitions.Append(new ProbeTool("extra").Descriptor).ToArray())).Error);
        Assert.Equal(ProviderError.LimitExceeded, Assert.Throws<ProviderContractException>(() => new ProviderRequest(
            ProviderExchangeTests.Scope, ProviderExchangeTests.Attempt(), [], definitions, bounds: new(maximumRequestBytes: 100))).Error);
    }

    [Fact]
    public void AggregateResponseExactAndOneOverBoundIncludesAllCallArguments()
    {
        var scope = new ProviderScope("p", "m");
        var attempt = ProviderExchangeTests.Attempt();
        static ToolCall Call(string id, int bytes) => new(id, "echo_a", "{\"text\":\"" + new string('x', bytes - 11) + "\"}");
        ToolCall[] calls = [Call("a", 16384), Call("b", 16384), Call("c", 16384), Call("d", 16354)];
        _ = new ProviderResponse(scope, attempt, ProviderFinish.ToolCalls, null, calls);
        calls[^1] = Call("d", 16355);
        Assert.Equal(ProviderError.LimitExceeded, Assert.Throws<ProviderContractException>(() => new ProviderResponse(scope, attempt, ProviderFinish.ToolCalls, null, calls)).Error);
        Assert.Equal(ProviderError.LimitExceeded, Assert.Throws<ProviderContractException>(() => new ProviderResponse(scope, attempt, ProviderFinish.ToolCalls, null,
            Enumerable.Range(0, ProviderLimits.Tools + 1).Select(i => new ToolCall("id" + i, "echo_a", "{}")).ToArray())).Error);
    }

    [Theory]
    [InlineData("text", false)]
    [InlineData("text", true)]
    [InlineData("calls", false)]
    [InlineData("calls", true)]
    [InlineData("replay", false)]
    [InlineData("replay", true)]
    public async Task LowerResponseBoundsHaveAcceptedAndRejectedNeighborsRetainingUsage(string dimension, bool over)
    {
        var scope = new ProviderScope("p", "m");
        var bounds = dimension == "text" ? new ProviderExchangeBounds(maximumResponseBytes: over ? 3 : 4)
            : dimension == "calls" ? new(maximumToolCalls: over ? 1 : 2) : new(maximumContinuationBytes: over ? 1 : 2);
        var request = new ProviderRequest(scope, ProviderExchangeTests.Attempt(), [], [new ProbeTool("echo_a").Descriptor],
            requiredCapabilities: DelegateProvider.All, bounds: bounds);
        var provider = new DelegateProvider(scope, (input, capture, _) =>
        {
            capture.ObserveDispatch(DispatchExposure.Dispatched); capture.CaptureUsage(new(1, 0));
            return ValueTask.FromResult(dimension == "calls"
                ? new ProviderResponse(scope, input.Attempt, ProviderFinish.ToolCalls, null,
                    [new("a", "echo_a", "{\"text\":\"x\"}"), new("b", "echo_a", "{\"text\":\"x\"}")])
                : new(scope, input.Attempt, ProviderFinish.Final, "é", [], dimension == "replay" ? new(scope, input.Attempt, [0x00, 0xFF]) : null));
        });
        var result = await provider.ExchangeAsync(request);
        Assert.Equal(over ? ProviderOutcome.Rejected : ProviderOutcome.Succeeded, result.Outcome);
        Assert.Equal(over ? ProviderError.LimitExceeded : ProviderError.None, result.Error);
        Assert.Equal(1, result.Observation.Usage.InputTokens);
    }

    [Fact]
    public void OpaqueReplayIsExactOwnedAndBoundedIncludingNonUtf8Bytes()
    {
        var scope = new ProviderScope("SCOPE_CANARY", "MODEL_SCOPE_CANARY");
        byte[] bytes = [0, 0xFF, 0x20, 1];
        var attempt = ProviderExchangeTests.Attempt();
        var continuation = new ProviderContinuation(scope, attempt, bytes);
        bytes[0] = 9;
        var copy = continuation.CopyReplayBytes(); copy[1] = 4;
        Assert.Equal(new byte[] { 0, 0xFF, 0x20, 1 }, continuation.CopyReplayBytes());
        Assert.True(continuation.Matches(new(scope, attempt, [0, 0xFF, 0x20, 1])));
        Assert.False(scope.Matches(new("scope_canary", "MODEL_SCOPE_CANARY")));
        _ = new ProviderContinuation(scope, attempt, new byte[ProviderLimits.ContinuationBytes]);
        Assert.Equal(ProviderError.LimitExceeded, Assert.Throws<ProviderContractException>(() => new ProviderContinuation(scope, attempt, new byte[ProviderLimits.ContinuationBytes + 1])).Error);
        Assert.Equal(ProviderError.ContinuationMismatch, Assert.Throws<ProviderContractException>(() => new ProviderContinuation(scope, attempt, [])).Error);
        Assert.DoesNotContain("CANARY", scope + continuation.ToString());
    }

    [Fact]
    public async Task MutableSourceCollectionsCannotChangeRetainedRequestOrAcceptedResponse()
    {
        var request = ProviderExchangeTests.Request();
        ProviderInput[] inputs = [ProviderInput.Data("original")];
        ToolDescriptor[] tools = [new ProbeTool("echo_a").Descriptor];
        var owned = new ProviderRequest(request.Scope, request.Attempt, inputs, tools);
        inputs[0] = ProviderInput.Instruction("changed"); tools[0] = new ProbeTool("echo_b").Descriptor;
        Assert.Equal("original", owned.Inputs[0].Text);
        Assert.Equal("echo_a", owned.Tools[0].Name);
        ToolCall[] calls = [new("id", "echo_a", "{\"text\":\"original\"}")];
        var candidate = new ProviderResponse(request.Scope, request.Attempt, ProviderFinish.ToolCalls, null, calls);
        calls[0] = new("other", "echo_b", "{}");
        var accepted = await new DelegateProvider(request.Scope, (_, _, _) => ValueTask.FromResult(candidate)).ExchangeAsync(owned);
        Assert.Equal("id", accepted.Response!.Calls[0].CallId);
        Assert.Throws<NotSupportedException>(() => ((IList<ToolCall>)accepted.Response.Calls)[0] = calls[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(33)]
    public void InvalidBoundsRejectExplicitly(int count) => Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderExchangeBounds(maximumInputs: count));

    [Fact]
    public void UndefinedFlagsAndInvalidIdentityRejectWithSafeErrors()
    {
        Assert.Equal(ProviderError.UnsupportedCapability, Assert.Throws<ProviderContractException>(() => new ProviderRequest(
            ProviderExchangeTests.Scope, ProviderExchangeTests.Attempt(), [], requiredCapabilities: (ProviderCapabilities)8)).Error);
        Assert.Equal(ProviderError.InvalidAssociation, Assert.Throws<ProviderContractException>(() => new ProviderAttempt(Guid.Empty, Guid.NewGuid(), Guid.NewGuid())).Error);
        Assert.Equal(ProviderError.InvalidAssociation, Assert.Throws<ProviderContractException>(() => new ProviderAttempt(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0)).Error);
        var error = Assert.Throws<ProviderContractException>(() => new ProviderScope("SCOPE_CANARY\n", "model"));
        Assert.DoesNotContain("CANARY", error.ToString());
    }

    [Fact]
    public async Task WrongInstalledScopeRejectsWithoutCoreAndScopeLabelsNeverReachDiagnostics()
    {
        var scope = new ProviderScope("SCOPE_CANARY", "MODEL_SCOPE_CANARY");
        var provider = new DelegateProvider(scope, (request, _, _) => ValueTask.FromResult(ProviderExchangeTests.Final(request)));
        var result = await provider.ExchangeAsync(ProviderExchangeTests.Request());
        Assert.Equal(ProviderError.InvalidAssociation, result.Error);
        Assert.Equal(0, provider.Invocations);
        Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(result.Diagnostic));
    }

    private sealed class OversizedInputs : IReadOnlyList<ProviderInput>
    {
        public int Count => ProviderLimits.Inputs + 1;
        public ProviderInput this[int index] => throw new InvalidOperationException("Must reject before accessing values.");
        public IEnumerator<ProviderInput> GetEnumerator() => throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

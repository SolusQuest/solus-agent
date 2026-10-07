using System.Text.Json;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Providers;

public sealed class ProviderExchangeTests
{
    internal static ProviderScope Scope => new("synthetic", "model");
    internal static ProviderAttempt Attempt(Guid? execution = null) => new(execution ?? Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    internal static ProviderRequest Request(ProviderAttempt? attempt = null, ProviderExchangeBounds? bounds = null) =>
        new(Scope, attempt ?? Attempt(), [ProviderInput.Instruction(CustomProviderConsumer.Trusted), ProviderInput.Data(CustomProviderConsumer.InputCanary)],
            [new ProbeTool("echo_a").Descriptor, new ProbeTool("echo_b").Descriptor], requiredCapabilities: DelegateProvider.All, bounds: bounds);
    internal static ProviderResponse Final(ProviderRequest request) => new(request.Scope, request.Attempt, ProviderFinish.Final, TwoTurnProvider.ModelCanary, []);

    [Fact]
    public async Task ActualConsumerCompletesTwoCallsByIdentityAndExactRequiredReplayWithoutPromotingData()
    {
        var run = await CustomProviderConsumer.RunAsync();
        Assert.Equal(ProviderOutcome.Succeeded, run.First.Outcome);
        Assert.Equal(ProviderOutcome.Succeeded, run.Second.Outcome);
        Assert.Equal(2, run.Effects);
        Assert.Equal(2, run.ToolResults.Count);
        foreach (var call in run.First.Response!.Calls)
            Assert.True(call.Matches(Assert.Single(run.ToolResults, result => result.Call.CallId == call.CallId).Call));
        Assert.Equal(new[] { ProviderInputKind.HostInstruction, ProviderInputKind.InputData, ProviderInputKind.ModelData,
            ProviderInputKind.ToolResultData, ProviderInputKind.ToolResultData }, run.SecondRequest.Inputs.Select(input => input.Kind));
        Assert.Equal(CustomProviderConsumer.Trusted, run.SecondRequest.Inputs[0].Text);
        Assert.Equal(new[] { "echo_a", "echo_b" }, run.SecondRequest.Tools.Select(tool => tool.Name));
        Assert.Equal("call-B", run.SecondRequest.Inputs[3].ToolResult!.Call.CallId);
        Assert.True(run.First.Response.Continuation!.Matches(run.SecondRequest.Continuation!));
        Assert.Equal(TwoTurnProvider.ModelCanary, run.Second.Response!.Text);
        Assert.Equal(DispatchExposure.Dispatched, run.First.Observation.Exposure);
        Assert.Equal(10, run.First.Observation.Usage.InputTokens);
        Assert.Equal(11, run.Second.Observation.Usage.InputTokens);
        var ordinary = string.Join("|", run.First, run.Second, run.First.Response, run.Second.Response,
            run.SecondRequest, run.SecondRequest.Scope, run.SecondRequest.Continuation, run.First.Response.Calls[0], run.ToolResults[0],
            JsonSerializer.Serialize(run.First.Diagnostic), JsonSerializer.Serialize(run.Second.Diagnostic));
        foreach (var canary in new[] { "HOST_CANARY", "INPUT_CANARY", "MODEL_CANARY", "TOOL_CANARY", "REPLAY_CANARY", "call-A", "call-B" })
            Assert.DoesNotContain(canary, ordinary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("execution")]
    [InlineData("logical")]
    [InlineData("physical")]
    [InlineData("ordinal")]
    [InlineData("scope")]
    [InlineData("final_calls")]
    [InlineData("no_output")]
    [InlineData("empty_calls")]
    [InlineData("undefined")]
    [InlineData("duplicate")]
    [InlineData("unknown_tool")]
    [InlineData("arguments")]
    [InlineData("oversized_text")]
    [InlineData("encoding")]
    [InlineData("continuation_scope")]
    [InlineData("continuation_anchor")]
    [InlineData("oversized_continuation")]
    [InlineData("null")]
    public async Task UsageSurvivesEveryLaterInvalidResponseClassWithNoCandidateOrReplay(string mode)
    {
        var request = Request();
        var provider = new DelegateProvider(Scope, (input, capture, _) =>
        {
            capture.ObserveDispatch(DispatchExposure.Dispatched);
            capture.CaptureUsage(new(9, 4), new(UsageSettlement.Unsettled));
            var attempt = input.Attempt;
            var wrong = new ProviderAttempt(mode == "execution" ? Guid.NewGuid() : attempt.ExecutionId,
                mode == "logical" ? Guid.NewGuid() : attempt.LogicalCallId,
                mode == "physical" ? Guid.NewGuid() : attempt.PhysicalAttemptId, mode == "ordinal" ? 2 : 1);
            var call = new ToolCall("call", "echo_a", "{\"text\":\"CANARY\"}");
            ProviderResponse? response = mode switch
            {
                "execution" or "logical" or "physical" or "ordinal" => new(Scope, wrong, ProviderFinish.Final, "CANARY", []),
                "scope" => new(new("other", "model"), attempt, ProviderFinish.Final, "CANARY", []),
                "final_calls" => new(Scope, attempt, ProviderFinish.Final, "CANARY", [call]),
                "no_output" => new(Scope, attempt, ProviderFinish.Final, null, []),
                "empty_calls" => new(Scope, attempt, ProviderFinish.ToolCalls, "CANARY", []),
                "undefined" => new(Scope, attempt, (ProviderFinish)99, "CANARY", []),
                "duplicate" => new(Scope, attempt, ProviderFinish.ToolCalls, null, [call, call]),
                "unknown_tool" => new(Scope, attempt, ProviderFinish.ToolCalls, null, [new("call", "extra_tool", "{}")]),
                "arguments" => new(Scope, attempt, ProviderFinish.ToolCalls, null, [new("call", "echo_a", "{\"text\":1}")]),
                "oversized_text" => new(Scope, attempt, ProviderFinish.Final, new('x', ProviderLimits.TextBytes + 1), []),
                "encoding" => new(Scope, attempt, ProviderFinish.Final, "\uD800", []),
                "continuation_scope" => new(Scope, attempt, ProviderFinish.Final, "CANARY", [], new(new("other", "model"), attempt, [1])),
                "continuation_anchor" => new(Scope, attempt, ProviderFinish.Final, "CANARY", [], new(Scope, Attempt(), [1])),
                "oversized_continuation" => new(Scope, attempt, ProviderFinish.Final, "CANARY", [], new(Scope, attempt, new byte[ProviderLimits.ContinuationBytes + 1])),
                _ => null,
            };
            return ValueTask.FromResult(response!);
        });
        var result = await provider.ExchangeAsync(request);
        Assert.Equal(ProviderOutcome.Rejected, result.Outcome);
        Assert.NotEqual(ProviderError.None, result.Error);
        Assert.Null(result.Response);
        Assert.Equal(9, result.Observation.Usage.InputTokens);
        Assert.Equal(4, result.Observation.Usage.OutputTokens);
        Assert.Equal(DispatchExposure.Dispatched, result.Observation.Exposure);
        Assert.Equal(request.Attempt.PhysicalAttemptId, result.Observation.PhysicalAttemptId);
        Assert.Equal(UsageSettlement.Unsettled, result.Observation.Accounting!.Settlement);
        Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(result.Diagnostic));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredCapabilitiesAndPreCancellationRejectBeforeCore(bool cancel)
    {
        var provider = new DelegateProvider(Scope, (request, _, _) => ValueTask.FromResult(Final(request)),
            cancel ? DelegateProvider.All : ProviderCapabilities.UsageReporting);
        using var source = new CancellationTokenSource();
        if (cancel) source.Cancel();
        var result = await provider.ExchangeAsync(Request(), source.Token);
        Assert.Equal(cancel ? ProviderOutcome.Cancelled : ProviderOutcome.Rejected, result.Outcome);
        Assert.Equal(cancel ? ProviderError.Cancelled : ProviderError.UnsupportedCapability, result.Error);
        Assert.Equal(0, provider.Invocations);
        Assert.Equal(DispatchExposure.NotDispatched, result.Observation.Exposure);
        Assert.Equal(UsageCompleteness.Unavailable, result.Observation.Usage.Completeness);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InFlightCancellationRetainsActualExposureAndAlreadyCapturedUsage(bool knownDispatch)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new DelegateProvider(Scope, async (_, capture, token) =>
        {
            if (knownDispatch) capture.ObserveDispatch(DispatchExposure.Dispatched);
            capture.CaptureUsage(knownDispatch ? new(7, null) : new(0, null));
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        });
        using var source = new CancellationTokenSource();
        var task = provider.ExchangeAsync(Request(), source.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); source.Cancel();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ProviderOutcome.Cancelled, result.Outcome);
        Assert.Equal(knownDispatch ? DispatchExposure.Dispatched : DispatchExposure.Unknown, result.Observation.Exposure);
        Assert.Equal(knownDispatch ? 7 : 0, result.Observation.Usage.InputTokens);
        Assert.Null(result.Observation.Usage.OutputTokens);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task CancellationHasExplicitBeforeAcceptanceAndAfterAcceptanceNeighbors()
    {
        using var before = new CancellationTokenSource();
        var provider = new DelegateProvider(Scope, (request, capture, _) =>
        {
            capture.ObserveDispatch(DispatchExposure.Dispatched); capture.CaptureUsage(new(3, 1));
            before.Cancel(); return ValueTask.FromResult(Final(request));
        });
        var cancelled = await provider.ExchangeAsync(Request(), before.Token);
        Assert.Equal(ProviderOutcome.Cancelled, cancelled.Outcome);
        Assert.Equal(3, cancelled.Observation.Usage.InputTokens);
        using var after = new CancellationTokenSource();
        var complete = await new DelegateProvider(Scope, (request, _, _) => ValueTask.FromResult(Final(request))).ExchangeAsync(Request(), after.Token);
        after.Cancel();
        Assert.Equal(ProviderOutcome.Succeeded, complete.Outcome);
        Assert.True(complete.Response!.Accepted);
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("unrelated_cancel")]
    [InlineData("duplicate_usage")]
    [InlineData("regress_dispatch")]
    public async Task FailureClassesCannotEraseCapturedEvidenceOrLeakExceptions(string mode)
    {
        var provider = new DelegateProvider(Scope, (_, capture, _) =>
        {
            capture.ObserveDispatch(DispatchExposure.Dispatched); capture.CaptureUsage(new(5, 2));
            if (mode == "duplicate_usage") capture.CaptureUsage(new());
            if (mode == "regress_dispatch") capture.ObserveDispatch(DispatchExposure.Unknown);
            if (mode == "unrelated_cancel") throw new OperationCanceledException("EXCEPTION_CANARY", new CancellationToken(true));
            throw new InvalidOperationException("EXCEPTION_CANARY");
        });
        var result = await provider.ExchangeAsync(Request());
        Assert.Equal(mode is "duplicate_usage" or "regress_dispatch" ? ProviderOutcome.Rejected : ProviderOutcome.Failed, result.Outcome);
        Assert.Equal(5, result.Observation.Usage.InputTokens);
        Assert.Equal(DispatchExposure.Dispatched, result.Observation.Exposure);
        Assert.DoesNotContain("EXCEPTION_CANARY", result + JsonSerializer.Serialize(result.Diagnostic));
    }

    [Fact]
    public async Task UnknownFailureIsNotFabricatedAsNoDispatchOrZeroAndCanCarrySeparateCharge()
    {
        var provider = new DelegateProvider(Scope, (_, capture, _) =>
        {
            capture.CaptureUsage(new(), new(conservativeUnobservedCharge: new(8, 2)));
            throw new InvalidOperationException();
        });
        var result = await provider.ExchangeAsync(Request());
        Assert.Equal(DispatchExposure.Unknown, result.Observation.Exposure);
        Assert.Null(result.Observation.Usage.InputTokens);
        Assert.Null(result.Observation.Usage.OutputTokens);
        Assert.Equal(8, result.Observation.Accounting!.ConservativeUnobservedCharge!.InputTokens);
    }

    [Theory]
    [InlineData(DispatchExposure.Unknown, false)]
    [InlineData(DispatchExposure.NotDispatched, false)]
    [InlineData(DispatchExposure.NotDispatched, true)]
    public async Task AcceptedUsageVocabularyRejectsExposureContradictions(DispatchExposure exposure, bool charge)
    {
        var provider = new DelegateProvider(Scope, (_, capture, _) =>
        {
            capture.ObserveDispatch(exposure);
            capture.CaptureUsage(charge ? new() : new(1, 0), charge ? new(conservativeUnobservedCharge: new(1, 0)) : null);
            throw new InvalidOperationException();
        });
        var result = await provider.ExchangeAsync(Request());
        Assert.Equal(ProviderError.ObservationConflict, result.Error);
        Assert.Equal(exposure, result.Observation.Exposure);
        Assert.Equal(UsageCompleteness.Unavailable, result.Observation.Usage.Completeness);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureClosesOnReturnAndThrowRejectingLateWriters(bool fail)
    {
        ProviderObservation? saved = null;
        var provider = new DelegateProvider(Scope, (request, capture, _) =>
        {
            saved = capture;
            if (fail) throw new InvalidOperationException();
            return ValueTask.FromResult(Final(request));
        });
        var result = await provider.ExchangeAsync(Request());
        Assert.Equal(ProviderError.ObservationClosed, Assert.Throws<ProviderContractException>(() => saved!.CaptureUsage(new())).Error);
        Assert.Equal(ProviderError.ObservationClosed, Assert.Throws<ProviderContractException>(() => saved!.ObserveDispatch(DispatchExposure.Dispatched)).Error);
        Assert.Equal(UsageCompleteness.Unavailable, result.Observation.Usage.Completeness);
    }

    [Fact]
    public async Task ConcurrentExchangesAndRetriesKeepSeparatePhysicalEvidence()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var provider = new DelegateProvider(Scope, async (request, capture, _) =>
        {
            capture.ObserveDispatch(DispatchExposure.Dispatched); capture.CaptureUsage(new(request.Attempt.AttemptNumber, 0));
            if (Interlocked.Increment(ref count) == 2) entered.SetResult();
            await gate.Task;
            if (request.Attempt.AttemptNumber == 1) throw new InvalidOperationException();
            return Final(request);
        });
        var firstAttempt = Attempt();
        var secondAttempt = new ProviderAttempt(firstAttempt.ExecutionId, firstAttempt.LogicalCallId, Guid.NewGuid(), 2);
        var first = provider.ExchangeAsync(Request(firstAttempt)).AsTask();
        var second = provider.ExchangeAsync(Request(secondAttempt)).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); gate.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ProviderOutcome.Failed, results[0].Outcome);
        Assert.Equal(ProviderOutcome.Succeeded, results[1].Outcome);
        var inventory = new AgentRunUsage(firstAttempt.ExecutionId, UsageInventoryCoverage.Complete, results.Select(result => result.Observation).ToArray());
        Assert.Equal(new long?[] { 1, 2 }, inventory.Attempts.Select(attempt => attempt.Usage.InputTokens));
        Assert.NotEqual(inventory.Attempts[0].PhysicalAttemptId, inventory.Attempts[1].PhysicalAttemptId);
    }

    [Fact]
    public async Task HostInventoryRejectsReusedPhysicalIdentityAcrossActualRetryResults()
    {
        var first = Attempt();
        var retry = new ProviderAttempt(first.ExecutionId, first.LogicalCallId, first.PhysicalAttemptId, 2);
        var provider = new DelegateProvider(Scope, (_, _, _) => throw new InvalidOperationException());
        var one = await provider.ExchangeAsync(Request(first));
        var two = await provider.ExchangeAsync(Request(retry));
        Assert.Throws<ArgumentException>(() => new AgentRunUsage(first.ExecutionId, UsageInventoryCoverage.Complete, [one.Observation, two.Observation]));
    }
}

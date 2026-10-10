using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Context;

public sealed class OrdinaryContextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedCutRetainsUsageAndCannotAdmitLateCompletion(bool deadline)
    {
        var entered = RuntimeFixture.Barrier(); var release = RuntimeFixture.Barrier(); var finished = RuntimeFixture.Barrier();
        var clock = new ControlledTimeProvider(); using var cancellation = new CancellationTokenSource();
        var provider = new ScriptedProvider([async (r, o, _) =>
        {
            o.CaptureUsage(new(3, 2)); entered.SetResult(); await release.Task;
            finished.SetResult(); return RuntimeFixture.Final(r);
        }]);
        var sink = new RestrictedContextHost();
        var agent = (IContextAgent)RuntimeFixture.Agent(provider, options: new(timeProvider: clock));
        var pending = agent.ExecuteWithContextAsync(new(RuntimeFixture.Request(), ContextExecutionIntent.Fresh), sink,
            cancellationToken: cancellation.Token).AsTask();
        await RuntimeFixture.Await(entered.Task);
        if (deadline) clock.Advance(TimeSpan.FromSeconds(10)); else cancellation.Cancel();
        var result = await RuntimeFixture.Await(pending);
        Assert.Equal(deadline ? AgentTerminationReason.ResourceLimit : AgentTerminationReason.Cancelled, result.Outcome!.Reason);
        Assert.Equal(ContextCaptureStatus.Delivered, result.CaptureStatus); Assert.Equal(3, result.Outcome.Usage!.InputTokens.ObservedTokens);
        var bytes = sink.CopyRestrictedContext().CopyRestrictedPayload();
        release.SetResult(); await RuntimeFixture.Await(finished.Task);
        Assert.Equal(bytes, sink.CopyRestrictedContext().CopyRestrictedPayload()); Assert.Equal(0, result.Outcome.CompletedWorkUnits);
        var request = RuntimeFixture.Request(); var resumedProvider = new ScriptedProvider([ScriptedProvider.Final]);
        var retryAgent = (IContextAgent)RuntimeAgentFactory.Create(new(resumedProvider, [], new RuntimeHooks(),
            contextAuthority: new SelectedAuthority(sink.CopyRestrictedContext(), result.Checkpoint!)));
        var retry = await retryAgent.ExecuteWithContextAsync(new(request, ContextExecutionIntent.ContinueRun,
            sink.CopyRestrictedContext(), OrdinaryFixture.Grant(result, request.ExecutionId)));
        Assert.Equal(ContextAdmission.Rejected, retry.Admission); Assert.Equal(ContextRejectionCode.InvalidRunTransition, retry.RejectionCode);
        Assert.Equal(0, resumedProvider.Effects);
    }
    [Fact]
    public async Task ThreeRoundsPreserveGlobalOperationAndSeparateUnknownHistoricalUsage()
    {
        var (first, firstBytes) = await OrdinaryFixture.Capture(provider: new PersistentProvider(true) { UnknownUsage = true });
        Assert.Null(Assert.Single(first.Outcome!.Usage!.Attempts).Usage.InputTokens);
        var secondSink = new RestrictedContextHost(); var secondRequest = OrdinaryFixture.Request();
        var second = await OrdinaryFixture.Agent(new PersistentProvider(true), new SelectedAuthority(firstBytes, first.Checkpoint!))
            .ExecuteWithContextAsync(new(secondRequest, ContextExecutionIntent.ContinueRun, firstBytes,
                OrdinaryFixture.Grant(first, secondRequest.ExecutionId)), secondSink);
        Assert.Equal(ContextAdmission.Supplied, second.Admission);
        Assert.Equal(2, Assert.Single(second.Outcome!.Usage!.Attempts).AttemptNumber);
        Assert.Equal(ContextCaptureStatus.Delivered, second.CaptureStatus);
        var thirdRequest = OrdinaryFixture.Request(); var thirdBytes = secondSink.CopyRestrictedContext();
        var third = await OrdinaryFixture.Agent(new PersistentProvider(), new SelectedAuthority(thirdBytes, second.Checkpoint!))
            .ExecuteWithContextAsync(new(thirdRequest, ContextExecutionIntent.ContinueRun, thirdBytes,
                OrdinaryFixture.Grant(second, thirdRequest.ExecutionId)));
        Assert.Equal(AgentTerminationReason.Completed, third.Outcome!.Reason);
        Assert.Equal(2, third.History.Count);
        Assert.Null(Assert.Single(third.History[0].Usage.Attempts).Usage.InputTokens);
        Assert.Equal(3, Assert.Single(third.Outcome.Usage!.Attempts).AttemptNumber);
        Assert.Equal(3, third.Outcome.Usage.InputTokens.ObservedTokens);
        Assert.Equal(Assert.Single(first.Outcome.Usage.Attempts).LogicalCallId, Assert.Single(third.Outcome.Usage.Attempts).LogicalCallId);
    }
    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, false)]
    public async Task RetainedCapacityPreservesOriginalRequestAtExactBoundary(int delta, bool admitted)
    {
        var producer = new PersistentProvider(true);
        var (source, envelope) = await OrdinaryFixture.Capture(provider: producer);
        var size = envelope.PayloadByteCount + producer.LastRequest!.Bounds.MaximumResponseBytes + delta;
        var provider = new PersistentProvider(); var authority = new SelectedAuthority(envelope, source.Checkpoint!);
        var request = OrdinaryFixture.Request();
        var result = await OrdinaryFixture.Agent(provider, authority, options: new(maximumRetainedBytes: size, requireContinuation: true))
            .ExecuteWithContextAsync(new(request, ContextExecutionIntent.ContinueRun, envelope, OrdinaryFixture.Grant(source, request.ExecutionId)));
        Assert.Equal(admitted ? ContextAdmission.Supplied : ContextAdmission.Rejected, result.Admission);
        Assert.Equal(admitted ? 1 : 0, provider.Effects); Assert.Equal(admitted ? 1 : 0, authority.Claims);
        if (admitted) Assert.Equal(producer.LastRequest.Bounds.MaximumResponseBytes, provider.LastRequest!.Bounds.MaximumResponseBytes);
    }
    [Theory]
    [InlineData(1, 64, false)]
    [InlineData(2, 64, true)]
    [InlineData(64, 1, false)]
    [InlineData(64, 2, true)]
    public async Task HistoricalInventoryAndRecordCapacityNeverRenew(int attempts, int records, bool admitted)
    {
        var (source, envelope) = await OrdinaryFixture.Capture();
        var provider = new PersistentProvider(); var authority = new SelectedAuthority(envelope, source.Checkpoint!);
        var request = OrdinaryFixture.Request();
        var result = await OrdinaryFixture.Agent(provider, authority, options: new(maximumAttempts: attempts, maximumRecords: records, requireContinuation: true))
            .ExecuteWithContextAsync(new(request, ContextExecutionIntent.ContinueRun, envelope, OrdinaryFixture.Grant(source, request.ExecutionId)));
        Assert.Equal(admitted ? ContextAdmission.Supplied : ContextAdmission.Rejected, result.Admission);
        Assert.Equal(admitted ? 1 : 0, provider.Effects); Assert.Equal(admitted ? 1 : 0, authority.Claims);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedFinalWithUnknownClosureOrUnsupportedExportCannotStartNewTask(bool missingExport)
    {
        var hooks = missingExport ? new RuntimeHooks() : new RuntimeHooks { After = (s, _) => ValueTask.FromResult<SettlementAcknowledgement?>(new(s.Exposure, RuntimeHookStatus.Unknown, accounting: s.Accounting)) };
        var (source, envelope) = await OrdinaryFixture.Capture(false, hooks, provider: new PersistentProvider { RejectExport = missingExport });
        var request = OrdinaryFixture.Request(); var provider = new PersistentProvider();
        var result = await OrdinaryFixture.Agent(provider, new SelectedAuthority(envelope, source.Checkpoint!)).ExecuteWithContextAsync(
            new(request, ContextExecutionIntent.NewRunFromContext, envelope, OrdinaryFixture.Grant(source, request.ExecutionId)));
        Assert.Equal(ContextAdmission.Rejected, result.Admission); Assert.Equal(0, provider.Effects);
        Assert.Equal(3, source.Outcome!.Usage!.InputTokens.ObservedTokens);
    }
    [Fact]
    public async Task FailedOperationContinuesWithFullRoundAndOriginalLineage()
    {
        var (source, envelope) = await OrdinaryFixture.Capture();
        Assert.Equal(AgentTerminationReason.ResourceLimit, source.Outcome!.Reason);
        Assert.Equal(ContextCaptureStatus.Delivered, source.CaptureStatus);
        Assert.Equal(3, source.Outcome.Usage!.InputTokens.ObservedTokens);
        var original = Assert.Single(source.Outcome.Usage.Attempts);
        var provider = new PersistentProvider();
        var authority = new SelectedAuthority(envelope, source.Checkpoint!);
        var request = OrdinaryFixture.Request();
        var observed = await ContextConsumer.RunAsync(OrdinaryFixture.Agent(provider, authority),
            new(request, ContextExecutionIntent.ContinueRun, envelope, OrdinaryFixture.Grant(source, request.ExecutionId)));
        var result = observed.Result;
        Assert.Equal(ContextAdmission.Supplied, result.Admission);
        Assert.Equal(AgentTerminationReason.Completed, result.Outcome!.Reason);
        Assert.Equal(1, provider.Effects); Assert.Equal(1, provider.Imports);
        var historical = Assert.Single(result.History);
        Assert.Equal(original.PhysicalAttemptId, Assert.Single(historical.Usage.Attempts).PhysicalAttemptId);
        Assert.Equal(3, historical.Usage.InputTokens.ObservedTokens);
        var current = Assert.Single(result.Outcome.Usage!.Attempts);
        Assert.Equal(original.LogicalCallId, current.LogicalCallId); Assert.Equal(2, current.AttemptNumber);
        Assert.NotEqual(original.PhysicalAttemptId, current.PhysicalAttemptId);
        Assert.Equal(3, result.Outcome.Usage.InputTokens.ObservedTokens);
        Assert.Equal(0, result.Outcome.Usage.Accounting!.Input.RemainingAllowance);
        Assert.Equal(original.PhysicalAttemptId, provider.LastRequest!.History!.OperationOrigin!.PhysicalAttemptId);
        Assert.Equal("CURRENT_HOST_INSTRUCTIONS", Assert.Single(provider.LastRequest.Inputs).Text);
        Assert.Single(observed.Progress);
    }
    [Fact]
    public async Task CompletedFinalStartsNewHostTaskAndReplaysAcceptedContinuation()
    {
        var (source, envelope) = await OrdinaryFixture.Capture(false);
        var provider = new PersistentProvider();
        var request = OrdinaryFixture.Request(instructions: "NEW_INSTRUCTIONS", data: [new(AgentInputSource.Repository, "new-task")]);
        var result = await OrdinaryFixture.Agent(provider, new SelectedAuthority(envelope, source.Checkpoint!)).ExecuteWithContextAsync(
            new(request, ContextExecutionIntent.NewRunFromContext, envelope, OrdinaryFixture.Grant(source, request.ExecutionId)));
        Assert.Equal(ContextAdmission.Supplied, result.Admission);
        Assert.Equal(AgentTerminationReason.Completed, result.Outcome!.Reason);
        Assert.Equal(1, result.Outcome.CompletedWorkUnits);
        Assert.Equal(1, provider.Effects);
        Assert.Equal(new[] { ProviderInputKind.HostInstruction, ProviderInputKind.ModelData, ProviderInputKind.InputData }, provider.LastRequest!.Inputs.Select(i => i.Kind));
        Assert.Equal("NEW_INSTRUCTIONS", provider.LastRequest.Inputs[0].Text);
        Assert.Equal("new-task", provider.LastRequest.Inputs[^1].Text);
        Assert.True(provider.LastRequest.Inputs[1].Model!.Accepted);
        Assert.True(provider.LastRequest.Continuation!.Matches(provider.LastRequest.Inputs[1].Model!.Continuation!));
        Assert.Equal(1, Assert.Single(result.Outcome.Usage!.Attempts).AttemptNumber);
    }
    [Theory]
    [InlineData(true, ContextExecutionIntent.NewRunFromContext)]
    [InlineData(false, ContextExecutionIntent.ContinueRun)]
    public async Task WrongCursorRejectsWithoutFallback(bool fail, ContextExecutionIntent intent)
    {
        var (source, envelope) = await OrdinaryFixture.Capture(fail);
        var provider = new PersistentProvider(); var hooks = new RuntimeHooks(); var sink = new RestrictedContextHost();
        var request = OrdinaryFixture.Request();
        var result = await OrdinaryFixture.Agent(provider, new SelectedAuthority(envelope, source.Checkpoint!), hooks).ExecuteWithContextAsync(
            new(request, intent, envelope, OrdinaryFixture.Grant(source, request.ExecutionId)), sink);
        Assert.Equal(ContextAdmission.Rejected, result.Admission); Assert.Null(result.Outcome);
        Assert.Equal(0, provider.Effects); Assert.Empty(hooks.Exposures); Assert.Equal(0, sink.CaptureCount);
    }
    [Theory]
    [InlineData("instructions")]
    [InlineData("data")]
    [InlineData("policy")]
    [InlineData("checkpoint")]
    [InlineData("provider")]
    public async Task IncompatibleCurrentAuthorityRejectsBeforeEffects(string change)
    {
        var (source, envelope) = await OrdinaryFixture.Capture();
        var provider = new PersistentProvider { RejectImport = change == "provider" };
        var authority = new SelectedAuthority(envelope, source.Checkpoint!);
        var request = OrdinaryFixture.Request(instructions: change == "instructions" ? "changed" : "CURRENT_HOST_INSTRUCTIONS",
            data: change == "data" ? [new(AgentInputSource.Repository, "changed")] : [], limits: change == "policy" ? new(retryPolicy: null) : null);
        var grant = OrdinaryFixture.Grant(source, request.ExecutionId);
        if (change == "checkpoint") grant = new(Guid.NewGuid(), source.Checkpoint! with { CheckpointId = Guid.NewGuid() }, request.ExecutionId, Guid.NewGuid());
        var result = await OrdinaryFixture.Agent(provider, authority).ExecuteWithContextAsync(new(request, ContextExecutionIntent.ContinueRun, envelope, grant));
        Assert.Equal(ContextAdmission.Rejected, result.Admission); Assert.Null(result.Outcome); Assert.Equal(0, provider.Effects);
        Assert.Equal(change == "provider" ? 1 : 0, authority.Claims);
    }
    [Theory]
    [InlineData(RuntimeHookStatus.Unknown)]
    [InlineData(RuntimeHookStatus.Failed)]
    public async Task UnknownClosureIsCapturedButCannotResume(RuntimeHookStatus status)
    {
        var hooks = new RuntimeHooks { After = (s, _) => ValueTask.FromResult<SettlementAcknowledgement?>(new(s.Exposure, status, accounting: s.Accounting)) };
        var (source, envelope) = await OrdinaryFixture.Capture(hooks: hooks);
        Assert.Equal(3, source.Outcome!.Usage!.InputTokens.ObservedTokens);
        Assert.Equal(ContextCaptureStatus.Delivered, source.CaptureStatus);
        var provider = new PersistentProvider(); var request = OrdinaryFixture.Request();
        var result = await OrdinaryFixture.Agent(provider, new SelectedAuthority(envelope, source.Checkpoint!)).ExecuteWithContextAsync(
            new(request, ContextExecutionIntent.ContinueRun, envelope, OrdinaryFixture.Grant(source, request.ExecutionId)));
        Assert.Equal(ContextAdmission.Rejected, result.Admission); Assert.Equal(0, provider.Effects);
    }
    [Fact]
    public async Task TrustedSelectionRejectsStructurallyValidTampering()
    {
        var (source, envelope) = await OrdinaryFixture.Capture();
        var document = JsonNode.Parse(envelope.CopyRestrictedPayload())!;
        document["Records"]![0]!["Text"] = "TAMPERED_INSTRUCTIONS";
        var modified = new AgentContextEnvelope(envelope.ImplementationId, 1, 1, JsonSerializer.SerializeToUtf8Bytes(document));
        Assert.NotEqual(OrdinaryFixture.Fingerprint(envelope), OrdinaryFixture.Fingerprint(modified));
        var request = OrdinaryFixture.Request(instructions: "TAMPERED_INSTRUCTIONS"); var provider = new PersistentProvider();
        var authority = new SelectedAuthority(envelope, source.Checkpoint!);
        var result = await OrdinaryFixture.Agent(provider, authority).ExecuteWithContextAsync(new(request, ContextExecutionIntent.ContinueRun,
            modified, OrdinaryFixture.Grant(source, request.ExecutionId)));
        Assert.Equal(ContextAdmission.Rejected, result.Admission); Assert.Equal(1, authority.Claims); Assert.Equal(0, provider.Effects);
        Assert.Equal(0, provider.Imports);
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("truncated")]
    [InlineData("null")]
    [InlineData("cursor")]
    [InlineData("attempt")]
    [InlineData("provider")]
    [InlineData("utf8")]
    public async Task StrictCodecRejectsBeforeClaim(string mutation)
    {
        var (source, envelope) = await OrdinaryFixture.Capture();
        var text = Encoding.UTF8.GetString(envelope.CopyRestrictedPayload()); var node = JsonNode.Parse(text)!;
        switch (mutation)
        {
            case "missing": node.AsObject().Remove("Elapsed"); break;
            case "null": node["Rounds"] = null; break;
            case "unknown": node["Extra"] = 1; break;
            case "cursor": node["Final"] = true; break;
            case "attempt": node["Rounds"]![0]!["Facts"]![0]!["Attempt"]!["PhysicalAttemptId"] = Guid.NewGuid(); break;
            case "provider": node["Provider"]!["Bytes"] = "AQID"; break;
        }
        var bytes = mutation switch { "duplicate" => Encoding.UTF8.GetBytes(text.Insert(1, "\"Elapsed\":\"00:00:00\",")),
            "truncated" => envelope.CopyRestrictedPayload()[..^1], "utf8" => new byte[] { 0xff, 0xfe }, _ => JsonSerializer.SerializeToUtf8Bytes(node) };
        var bad = new AgentContextEnvelope(envelope.ImplementationId, 1, 1, bytes);
        var provider = new PersistentProvider(); var authority = new SelectedAuthority(bad, source.Checkpoint!); var request = OrdinaryFixture.Request();
        var result = await OrdinaryFixture.Agent(provider, authority).ExecuteWithContextAsync(new(request, ContextExecutionIntent.ContinueRun, bad, OrdinaryFixture.Grant(source, request.ExecutionId)));
        Assert.Equal(ContextAdmission.Rejected, result.Admission); Assert.Null(result.Outcome);
        Assert.Equal(mutation == "provider" ? 1 : 0, authority.Claims); Assert.Equal(0, provider.Effects);
    }
    [Fact]
    public async Task CaptureThrowDoesNotRewriteCompletedOutcomeOrLeakContent()
    {
        var sink = new ThrowingSink(); var result = await OrdinaryFixture.Agent(new PersistentProvider()).ExecuteWithContextAsync(
            new(OrdinaryFixture.Request(), ContextExecutionIntent.Fresh), sink);
        Assert.Equal(AgentTerminationReason.Completed, result.Outcome!.Reason); Assert.Equal(ContextCaptureStatus.Failed, result.CaptureStatus);
        Assert.NotNull(sink.Value); Assert.Equal(3, result.Outcome.Usage!.InputTokens.ObservedTokens);
        var ordinary = JsonSerializer.Serialize(result) + result + result.Outcome;
        Assert.DoesNotContain("RESTRICTED_", ordinary); Assert.DoesNotContain("CREDENTIAL", ordinary);
        Assert.DoesNotContain("CREDENTIAL", Encoding.UTF8.GetString(sink.Value!.CopyRestrictedPayload()));
    }
    private sealed class ThrowingSink : IRestrictedContextSink
    {
        internal AgentContextEnvelope? Value;
        public void Capture(AgentContextEnvelope value) { Value = value; throw new InvalidOperationException("PRIVATE_EXCEPTION_CANARY"); }
    }
}

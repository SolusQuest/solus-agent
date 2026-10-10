using System.Text;
using System.Text.Json;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Context;
using Xunit;

namespace SolusAgent.ContractTests.Context;

public sealed class ContextEnvelopeTests
{
    [Fact]
    public void RestrictedBytesAreCopiedAndOrdinaryRepresentationsOnlyExposeMetadata()
    {
        var bytes = Encoding.UTF8.GetBytes(ContextCases.Canary);
        var envelope = new AgentContextEnvelope(ScriptedContextAgent.DefaultImplementationId, 1, 1, bytes);
        bytes[0] = 0;
        var first = envelope.CopyRestrictedPayload();
        Assert.Equal(ContextCases.Canary, Encoding.UTF8.GetString(first));
        first[0] = 0;
        Assert.Equal(ContextCases.Canary, Encoding.UTF8.GetString(envelope.CopyRestrictedPayload()));
        Assert.DoesNotContain(ContextCases.Canary, envelope.ToString());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(envelope));
        Assert.Equal(["ImplementationId", "FormatVersion", "CompatibilityVersion", "PayloadByteCount"], json.RootElement.EnumerateObject().Select(value => value.Name));
        Assert.Equal(Encoding.UTF8.GetByteCount(ContextCases.Canary), envelope.PayloadByteCount);
        Assert.All(typeof(AgentContextEnvelope).GetProperties(), value => Assert.Null(value.SetMethod));
        Assert.Equal([typeof(Guid), typeof(int), typeof(int), typeof(byte[])], typeof(AgentContextEnvelope).GetConstructors().Single().GetParameters().Select(value => value.ParameterType));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void InvalidMetadataRejectsBeforeAnyPayloadInterpretation(int format, int compatibility)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new AgentContextEnvelope(ScriptedContextAgent.DefaultImplementationId, format, compatibility, Encoding.UTF8.GetBytes(ContextCases.Canary)));
        Assert.DoesNotContain(ContextCases.Canary, error.ToString());
    }

    [Fact]
    public void IdentityAndNullRepresentationRejectButEmptyPayloadRemainsAdmissibleForExplicitRejection()
    {
        Assert.Throws<ArgumentException>(() => new AgentContextEnvelope(Guid.Empty, 1, 1, []));
        Assert.Throws<ArgumentNullException>(() => new AgentContextEnvelope(ScriptedContextAgent.DefaultImplementationId, 1, 1, null!));
        Assert.Empty(new AgentContextEnvelope(ScriptedContextAgent.DefaultImplementationId, 1, 1, []).CopyRestrictedPayload());
    }

    [Theory]
    [InlineData(ContextExecutionIntent.Fresh, true)]
    [InlineData(ContextExecutionIntent.NewRunFromContext, false)]
    [InlineData(ContextExecutionIntent.ContinueRun, false)]
    public void ContradictoryIntentShapeRejectsWithSafeErrors(ContextExecutionIntent intent, bool hasContext)
    {
        var error = Assert.Throws<ArgumentException>(() => new ContextExecutionRequest(ContextCases.Request(), intent, hasContext ? ContextCases.Envelope([]) : null));
        Assert.DoesNotContain(ContextCases.Canary, error.ToString());
    }

    [Fact]
    public void UndefinedIntentAndNullRequestCannotEnterAnImplementation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContextExecutionRequest(ContextCases.Request(), (ContextExecutionIntent)99));
        Assert.Throws<ArgumentNullException>(() => new ContextExecutionRequest(null!, ContextExecutionIntent.Fresh));
        Assert.DoesNotContain(ContextCases.Canary, new ContextExecutionRequest(ContextCases.Request(data: [new(AgentInputSource.Tool, ContextCases.Canary)]), ContextExecutionIntent.Fresh).ToString());
    }

    [Theory]
    [InlineData(ContextRejectionCode.UnsupportedContext)]
    [InlineData(ContextRejectionCode.ImplementationMismatch)]
    [InlineData(ContextRejectionCode.UnsupportedFormat)]
    [InlineData(ContextRejectionCode.IncompatibleContext)]
    [InlineData(ContextRejectionCode.InvalidContext)]
    [InlineData(ContextRejectionCode.InvalidRunTransition)]
    public void EveryContextRejectionHasNoFabricatedWorkOutcome(ContextRejectionCode code)
    {
        var result = new ContextExecutionResult(ContextCases.Id, ContextExecutionIntent.ContinueRun, ContextAdmission.Rejected, rejectionCode: code);
        Assert.Null(result.Outcome);
        Assert.Equal(code, result.RejectionCode);
        Assert.Throws<ArgumentException>(() => new ContextExecutionResult(ContextCases.Id, ContextExecutionIntent.ContinueRun, ContextAdmission.Rejected,
            new AgentOutcome(ContextCases.Id, AgentTerminationReason.Failed, 0, failureCode: AgentFailureCode.ExecutionFailed), code));
    }

    [Theory]
    [InlineData(ContextCaptureStatus.Delivered)]
    [InlineData(ContextCaptureStatus.Failed)]
    public void UnadmittedCallsCannotClaimRestrictedTransfer(ContextCaptureStatus capture)
    {
        Assert.Throws<ArgumentException>(() => new ContextExecutionResult(ContextCases.Id, ContextExecutionIntent.ContinueRun, ContextAdmission.Rejected,
            rejectionCode: ContextRejectionCode.InvalidContext, captureStatus: capture));
        Assert.Throws<ArgumentException>(() => new ContextExecutionResult(ContextCases.Id, ContextExecutionIntent.Fresh, ContextAdmission.NotAttempted,
            new AgentOutcome(ContextCases.Id, AgentTerminationReason.Cancelled, 0), captureStatus: capture));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void IncoherentOrdinaryResultShapesReject(int variant)
    {
        var completed = new AgentOutcome(ContextCases.Id, AgentTerminationReason.Completed, 1);
        Assert.ThrowsAny<ArgumentException>(() => variant switch
        {
            0 => new ContextExecutionResult(Guid.Empty, ContextExecutionIntent.Fresh, ContextAdmission.Fresh, completed),
            1 => new ContextExecutionResult(ContextCases.Id, (ContextExecutionIntent)99, ContextAdmission.Fresh, completed),
            2 => new ContextExecutionResult(ContextCases.Id, ContextExecutionIntent.Fresh, (ContextAdmission)99, completed),
            3 => new ContextExecutionResult(ContextCases.Id, ContextExecutionIntent.Fresh, ContextAdmission.Fresh, completed, (ContextRejectionCode)99),
            4 => new ContextExecutionResult(ContextCases.Id, ContextExecutionIntent.Fresh, ContextAdmission.Fresh, completed, captureStatus: (ContextCaptureStatus)99),
            5 => new ContextExecutionResult(ContextCases.Id, ContextExecutionIntent.Fresh, ContextAdmission.Supplied, completed),
            6 => new ContextExecutionResult(ContextCases.Id, ContextExecutionIntent.ContinueRun, ContextAdmission.Fresh, completed),
            _ => new ContextExecutionResult(ContextCases.Id, ContextExecutionIntent.Fresh, ContextAdmission.Fresh, new AgentOutcome(Guid.NewGuid(), AgentTerminationReason.Completed, 1)),
        });
    }

    [Fact]
    public void AdmissionAndNotAttemptedBranchesPreserveAcceptedExecutionAndUsageSemantics()
    {
        var cancelled = new AgentOutcome(ContextCases.Id, AgentTerminationReason.Cancelled, 0,
            usage: new AgentRunUsage(ContextCases.Id, UsageInventoryCoverage.Complete, []));
        Assert.Same(cancelled, new ContextExecutionResult(ContextCases.Id, ContextExecutionIntent.Fresh, ContextAdmission.NotAttempted, cancelled).Outcome);
        Assert.Throws<ArgumentException>(() => new ContextExecutionResult(ContextCases.Id, ContextExecutionIntent.Fresh, ContextAdmission.NotAttempted,
            new AgentOutcome(ContextCases.Id, AgentTerminationReason.Cancelled, 1)));
        Assert.Throws<ArgumentException>(() => new ContextExecutionResult(ContextCases.Id, ContextExecutionIntent.Fresh, ContextAdmission.NotAttempted,
            new AgentOutcome(ContextCases.Id, AgentTerminationReason.Completed, 0)));
        Assert.Throws<ArgumentException>(() => new ContextExecutionResult(ContextCases.Id, ContextExecutionIntent.Fresh, ContextAdmission.NotAttempted,
            new AgentOutcome(ContextCases.Id, AgentTerminationReason.Cancelled, 0, usage: new AgentRunUsage(ContextCases.Id, UsageInventoryCoverage.Unavailable, []))));
        Assert.Throws<ArgumentException>(() => new ContextExecutionResult(ContextCases.Id, ContextExecutionIntent.Fresh, ContextAdmission.Fresh,
            new AgentOutcome(ContextCases.Id, AgentTerminationReason.UnsupportedCapability, 0, AgentCapability.DurationLimit)));
        Assert.Throws<ArgumentException>(() => new ContextExecutionResult(ContextCases.Id, ContextExecutionIntent.ContinueRun, ContextAdmission.Supplied));
        Assert.Throws<ArgumentException>(() => new ContextExecutionResult(ContextCases.Id, ContextExecutionIntent.ContinueRun, ContextAdmission.Rejected));
        Assert.Throws<ArgumentException>(() => new ContextExecutionResult(ContextCases.Id, ContextExecutionIntent.Fresh, ContextAdmission.Rejected, rejectionCode: ContextRejectionCode.InvalidContext));
        Assert.Throws<ArgumentException>(() => new ContextExecutionResult(ContextCases.Id, ContextExecutionIntent.Fresh, ContextAdmission.Fresh, cancelled, ContextRejectionCode.InvalidContext));
    }
}

internal static class ContextCases
{
    internal static readonly Guid Id = Guid.Parse("f01d0d88-5854-4933-a5d1-ac96974e8008");
    internal const string Canary = "synthetic-restricted-context-canary";
    internal static AgentRequest Request(int bound = 3, Guid? id = null, IReadOnlyList<AgentInput>? data = null,
        AgentCapability required = AgentCapability.WorkUnitLimit | AgentCapability.Cancellation, string instructions = "current trusted Host task") =>
        new(id ?? Id, instructions, data ?? [], new AgentExecutionBounds(bound, TimeSpan.FromSeconds(10)), required);
    internal static AgentContextEnvelope Envelope(byte[] payload, Guid? implementation = null, int format = 1, int compatibility = 1) =>
        new(implementation ?? ScriptedContextAgent.DefaultImplementationId, format, compatibility, payload);
    internal static async Task<AgentContextEnvelope> CaptureAsync(int bound = 1, IReadOnlyList<AgentInput>? data = null)
    {
        var host = new RestrictedContextHost();
        await ContextConsumer.RunAsync(new ScriptedContextAgent(3), new ContextExecutionRequest(Request(bound, data: data), ContextExecutionIntent.Fresh), host);
        return host.CopyRestrictedContext();
    }
}

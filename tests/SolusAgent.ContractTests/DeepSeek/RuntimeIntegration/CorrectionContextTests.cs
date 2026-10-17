using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.ContractTests.Runtime.Tools;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Startup;
using SolusAgent.Runtime.Api.Providers;
using Xunit;

namespace SolusAgent.ContractTests.DeepSeek.RuntimeIntegration;

public sealed class CorrectionContextTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TrustedHostDenialMustBelongToTheOwningRegistrationBeforeClaim(bool missingClassifier)
    {
        var mode = missingClassifier ? "schema" : "path";
        using var first = new CorrectionContextFixture(mode, true, restrictHostErrors: true, missingClassifier: missingClassifier);
        var sink = new RestrictedContextHost();
        var source = await first.Execute(new(CorrectionContextFixture.Request(true), ContextExecutionIntent.Fresh), sink);
        Assert.Equal(AgentTerminationReason.Completed, source.Context.Outcome!.Reason);
        var original = sink.CopyRestrictedContext(); var node = JsonNode.Parse(original.CopyRestrictedPayload())!;
        var model = node["Records"]![1]!["CorrectionModel"]!;
        model["Origin"] = (int)ToolCorrectionOrigin.HostArguments;
        model["Members"]![0]!["SchemaError"] = (int)SolusAgent.Tools.Api.ToolError.None;
        model["Members"]![0]!["ErrorCode"] = missingClassifier ? "path_invalid" : "cursor_invalid";
        node["Rounds"]![0]!["Facts"]![0]!["ProducedFinish"] = (int)ProviderFinish.ToolCalls;
        var altered = new AgentContextEnvelope(original.ImplementationId, original.FormatVersion, original.CompatibilityVersion,
            JsonSerializer.SerializeToUtf8Bytes(node));
        var authority = new SelectedAuthority(altered, source.Context.Checkpoint!);
        using var second = new CorrectionContextFixture(mode, false, authority: authority,
            restrictHostErrors: true, missingClassifier: missingClassifier);
        var request = CorrectionContextFixture.Request(false);
        var result = await second.Execute(new(request, ContextExecutionIntent.NewRunFromContext, altered,
            new(Guid.NewGuid(), source.Context.Checkpoint!, request.ExecutionId, Guid.NewGuid())));
        Assert.Equal(ContextAdmission.Rejected, result.Context.Admission); Assert.Equal(0, authority.Claims);
        Assert.Equal(0, second.Provider.Imports); Assert.Equal(0, second.Handler.Sends);
        Assert.Equal(0, second.Host.Classifications); Assert.Equal(0, second.Host.Tool.DomainChecks); Assert.Equal(0, second.Host.Capability.Entries);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancelledCorrectionHistorySuppliesNoNewContinuationAuthority(bool candidates)
    {
        using var first = new CorrectionContextFixture("schema", true, candidates); var sink = new RestrictedContextHost();
        using var cancellation = new CancellationTokenSource();
        var source = await first.Execute(new(CorrectionContextFixture.Request(true), ContextExecutionIntent.Fresh), sink,
            new InlineProgress(_ => cancellation.Cancel()), cancellation.Token);
        Assert.Equal(AgentTerminationReason.Cancelled, source.Context.Outcome!.Reason);
        Assert.Equal(2, source.Context.Outcome.Usage!.ToolCallAdmissions!.Admitted); Assert.Equal(0, first.Host.Capability.Entries);
        Assert.Equal(1, first.Handler.Sends); Assert.Equal(ContextCaptureStatus.Delivered, source.Context.CaptureStatus);
        var envelope = sink.CopyRestrictedContext(); var payload = JsonNode.Parse(envelope.CopyRestrictedPayload())!;
        Assert.Empty(payload["Members"]!.AsArray()); Assert.Null(payload["ToolCursor"]);
        Assert.Single(payload["Records"]!.AsArray(), r => r!["CorrectionModel"] is not null);
        foreach (var intent in new[] { ContextExecutionIntent.ContinueRun, ContextExecutionIntent.NewRunFromContext })
        {
            var authority = new SelectedAuthority(envelope, source.Context.Checkpoint!);
            using var second = new CorrectionContextFixture("schema", false, candidates, authority);
            var request = CorrectionContextFixture.Request(false);
            var result = await second.Execute(new(request, intent, envelope,
                new(Guid.NewGuid(), source.Context.Checkpoint!, request.ExecutionId, Guid.NewGuid())));
            Assert.Equal(ContextAdmission.Rejected, result.Context.Admission); Assert.Equal(0, authority.Claims);
            Assert.Equal(0, second.Provider.Imports); Assert.Equal(0, second.Handler.Sends);
        }
    }

    [Fact]
    public async Task FatalPreflightCaptureRetainsProducedWorkWithoutRawModelOrAcceptedHistory()
    {
        using var first = new CorrectionContextFixture("unsafe", true); var sink = new RestrictedContextHost();
        var source = await first.Execute(new(CorrectionContextFixture.Request(true), ContextExecutionIntent.Fresh), sink);
        Assert.Equal(AgentTerminationReason.Failed, source.Context.Outcome!.Reason); Assert.Equal(1, source.Context.Outcome.CompletedWorkUnits);
        Assert.Equal(0, source.Context.Outcome.Usage!.ToolCallAdmissions!.Admitted); Assert.Equal(0, first.Host.Capability.Entries);
        Assert.Equal(ContextCaptureStatus.Delivered, source.Context.CaptureStatus);
        var node = JsonNode.Parse(sink.CopyRestrictedContext().CopyRestrictedPayload())!;
        Assert.Single(node["Records"]!.AsArray()); Assert.Empty(node["Members"]!.AsArray()); Assert.Null(node["ToolCursor"]);
        var fact = node["Rounds"]![0]!["Facts"]![0]!;
        Assert.False(fact["AcceptedTools"]!.GetValue<bool>()); Assert.False(fact["AcceptedCorrection"]!.GetValue<bool>());
        Assert.False(fact["AcceptedFinal"]!.GetValue<bool>()); Assert.NotNull(fact["ProducedFinish"]);
    }

    [Theory]
    [InlineData("schema", false)] [InlineData("path", false)] [InlineData("schema", true)] [InlineData("path", true)]
    public async Task ActualDeepSeekCompletedCorrectionAndToolHistoryRestoresWithoutHistoricalInvocation(string mode, bool candidates)
    {
        using var first = new CorrectionContextFixture(mode, true, candidates); var sink = new RestrictedContextHost();
        var produced = await first.Execute(new(CorrectionContextFixture.Request(true), ContextExecutionIntent.Fresh), sink);
        Assert.Equal(AgentTerminationReason.Completed, produced.Context.Outcome!.Reason); Assert.Equal(ContextCaptureStatus.Delivered, produced.Context.CaptureStatus);
        Assert.Equal(3, first.Handler.Sends); Assert.True(first.WireMatches); Assert.Equal(2, first.Host.Capability.Entries);
        var envelope = sink.CopyRestrictedContext(); Assert.Equal(2, envelope.FormatVersion);
        AssertSanitized(envelope, first.Host.BadArguments);
        var authority = new SelectedAuthority(envelope, produced.Context.Checkpoint!);
        using var second = new CorrectionContextFixture(mode, false, candidates, authority);
        var request = CorrectionContextFixture.Request(false);
        var consumed = await second.Execute(new(request, ContextExecutionIntent.NewRunFromContext, envelope,
            new(Guid.NewGuid(), produced.Context.Checkpoint!, request.ExecutionId, Guid.NewGuid())));
        Assert.Equal(ContextAdmission.Supplied, consumed.Context.Admission); Assert.Equal(AgentTerminationReason.Completed, consumed.Context.Outcome!.Reason);
        Assert.Equal(1, second.Handler.Sends); Assert.True(second.WireMatches); Assert.Equal(1, second.Provider.Imports); Assert.Equal(1, authority.Claims);
        Assert.Equal(0, second.Host.Capability.Entries); Assert.Equal(0, second.Host.Tool.DomainChecks);
        Assert.Equal(0, consumed.Context.Outcome.Usage!.ToolInvocations!.Invoked); Assert.Equal(0, consumed.Context.Outcome.Usage.ToolCallAdmissions!.Admitted);
        var historical = Assert.Single(consumed.Context.History); Assert.Equal(4, historical.Usage.ToolCallAdmissions!.Admitted);
        Assert.Equal(2, historical.Usage.ToolInvocations!.Invoked); Assert.Equal(3, historical.CompletedWorkUnits);
        var history = Assert.Single(second.Provider.Requests).Inputs;
        Assert.Equal(2, history.Count(i => i.CorrectionFeedback is not null));
        Assert.Equal(new[] { "corrected_a", "corrected_b" }, history.Where(i => i.ToolResult is not null).Select(i => i.ToolResult!.Call.CallId));
        Assert.All(history.Where(i => i.ToolResult is not null), i => Assert.True(i.ToolResult!.IsHistorical));
        if (candidates)
        { Assert.Equal(0, consumed.Candidates!.RepairsAdmitted); Assert.Equal(0, consumed.Candidates.ContinuationsAdmitted); Assert.Equal(1, second.Hosts); }
        Assert.DoesNotContain(ArgumentCorrectionFixture.Canary, JsonSerializer.Serialize(consumed));
    }

    [Theory]
    [InlineData("marker")] [InlineData("feedback")] [InlineData("missing_feedback")] [InlineData("reorder")]
    [InlineData("duplicate_id")] [InlineData("unknown_code")] [InlineData("origin")] [InlineData("fact")]
    [InlineData("debit")] [InlineData("source_size")] [InlineData("continuation")] [InlineData("old_format")]
    public async Task IndependentlyTrustedMalformedCorrectionRejectsBeforeClaimImportOrDispatch(string mutation)
    {
        using var first = new CorrectionContextFixture("schema", true); var sink = new RestrictedContextHost();
        var source = await first.Execute(new(CorrectionContextFixture.Request(true), ContextExecutionIntent.Fresh), sink);
        Assert.Equal(ContextCaptureStatus.Delivered, source.Context.CaptureStatus);
        var envelope = sink.CopyRestrictedContext(); var node = JsonNode.Parse(envelope.CopyRestrictedPayload())!;
        var model = node["Records"]![1]!["CorrectionModel"]!;
        switch (mutation)
        {
            case "marker": model["Members"]![0]!["Call"]!["ArgumentsJson"] = first.Host.BadArguments; break;
            case "feedback": node["Records"]![2]!["CorrectionFeedback"]!["Ordinal"] = 2; break;
            case "missing_feedback": node["Records"]!.AsArray().RemoveAt(2); break;
            case "reorder":
                var members = model["Members"]!.AsArray(); var a = members[0]!.DeepClone(); var b = members[1]!.DeepClone(); members[0] = b; members[1] = a; break;
            case "duplicate_id": model["Members"]![1]!["Call"]!["CallId"] = "bad"; break;
            case "unknown_code": model["Members"]![0]!["ErrorCode"] = "not_configured"; break;
            case "origin": model["Origin"] = 1; break;
            case "fact": node["Rounds"]![0]!["Facts"]![0]!["AcceptedTools"] = true; node["Rounds"]![0]!["Facts"]![0]!["AcceptedCorrection"] = false; break;
            case "debit": node["Rounds"]![0]!["Usage"]!["ToolCallAdmissions"]!["Admitted"] = 3; break;
            case "source_size": model["Members"]![0]!["OriginalArgumentBytes"] = 1; break;
            case "continuation": model["Continuation"]!["Origin"]!["PhysicalAttemptId"] = Guid.NewGuid(); break;
        }
        var altered = new AgentContextEnvelope(envelope.ImplementationId, mutation == "old_format" ? 1 : envelope.FormatVersion,
            envelope.CompatibilityVersion, JsonSerializer.SerializeToUtf8Bytes(node));
        var authority = new SelectedAuthority(altered, source.Context.Checkpoint!);
        using var second = new CorrectionContextFixture("schema", false, authority: authority);
        var request = CorrectionContextFixture.Request(false);
        var result = await second.Execute(new(request, ContextExecutionIntent.NewRunFromContext, altered,
            new(Guid.NewGuid(), source.Context.Checkpoint!, request.ExecutionId, Guid.NewGuid())));
        Assert.Equal(ContextAdmission.Rejected, result.Context.Admission); Assert.Equal(0, authority.Claims);
        Assert.Equal(0, second.Provider.Imports); Assert.Equal(0, second.Handler.Sends); Assert.Equal(0, second.Host.Capability.Entries);
        Assert.Null(result.Context.Outcome);
    }

    [Theory]
    [InlineData("exact", true)] [InlineData("one_under", false)]
    public async Task SelectedFullResponseAndEncodedDefinitionsMustFitBeforeNewRunClaim(string boundary, bool admitted)
    {
        using var first = new CorrectionContextFixture("schema", true); var sink = new RestrictedContextHost();
        var source = await first.Execute(new(CorrectionContextFixture.Request(true), ContextExecutionIntent.Fresh), sink);
        var envelope = sink.CopyRestrictedContext(); var request = CorrectionContextFixture.Request(false);
        var descriptor = first.Host.Tool.Descriptor;
        var definitions = new[] { descriptor.Name, descriptor.Description, descriptor.CapabilityId,
            descriptor.InputSchema.NormalizedJson, descriptor.ResultSchema.NormalizedJson }.Sum(Encoding.UTF8.GetByteCount) + first.Host.Policy.PayloadByteCount;
        var capacity = envelope.PayloadByteCount + definitions + Encoding.UTF8.GetByteCount(request.Instructions)
            + request.Data.Sum(d => Encoding.UTF8.GetByteCount(d.Text)) + 65536 - (boundary == "one_under" ? 1 : 0);
        var authority = new SelectedAuthority(envelope, source.Context.Checkpoint!);
        using var second = new CorrectionContextFixture("schema", false, authority: authority, options: new(maximumRetainedBytes: capacity, requireContinuation: true));
        var result = await second.Execute(new(request, ContextExecutionIntent.NewRunFromContext, envelope,
            new(Guid.NewGuid(), source.Context.Checkpoint!, request.ExecutionId, Guid.NewGuid())));
        Assert.Equal(admitted ? ContextAdmission.Supplied : ContextAdmission.Rejected, result.Context.Admission);
        Assert.Equal(admitted ? 1 : 0, authority.Claims); Assert.Equal(admitted ? 1 : 0, second.Provider.Imports);
        Assert.Equal(admitted ? 1 : 0, second.Handler.Sends); Assert.Equal(0, second.Host.Capability.Entries);
        if (admitted) Assert.Equal(65536, Assert.Single(second.Provider.Requests).Bounds.MaximumResponseBytes);
    }

    internal static void AssertSanitized(AgentContextEnvelope envelope, string raw)
    {
        var text = Encoding.UTF8.GetString(envelope.CopyRestrictedPayload()); var digest = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        Assert.DoesNotContain(ArgumentCorrectionFixture.Canary, text); Assert.DoesNotContain(Convert.ToHexString(digest), text);
        Assert.DoesNotContain(Convert.ToBase64String(digest), text);
        var node = JsonNode.Parse(text)!; Assert.Equal(2, node["Members"]!.AsArray().Count);
        Assert.Equal(new[] { "corrected_a", "corrected_b" }, node["Members"]!.AsArray().Select(m => m!["Call"]!["CallId"]!.GetValue<string>()));
    }
}

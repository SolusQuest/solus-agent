using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CustomTools;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Context;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Tools;

public sealed class RecoveryEscapingTests
{
    [Fact]
    public void DefaultQuoteAndHtmlEscapingHaveTheSameSixByteExpansion()
    {
        var quote = JsonSerializer.Serialize(new string('"', 1024));
        var html = JsonSerializer.Serialize(new string('<', 1024));
        Assert.Equal(6 * 1024 + 2, Encoding.UTF8.GetByteCount(quote));
        Assert.Equal(Encoding.UTF8.GetByteCount(quote), Encoding.UTF8.GetByteCount(html));
        foreach (var sample in new[] { "\"", "<", ">", "&", "'", "\\", "\0", "\n", "中", "😀" })
            Assert.True(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(sample)) <= 6 * Encoding.UTF8.GetByteCount(sample) + 2);
    }
    [Theory]
    [InlineData("<", 900)]
    [InlineData(">", 900)]
    [InlineData("&", 900)]
    [InlineData("'", 900)]
    [InlineData("\"", 450)]
    [InlineData("\\", 450)]
    [InlineData("中", 280)]
    [InlineData("😀", 70)]
    public async Task StringRecoveryAtExactCapacityRetainsExpandedAuditAndOneBelowDoesNotClaim(string token, int repetitions)
    {
        var value = string.Concat(Enumerable.Repeat(token, repetitions));
        var json = JsonSerializer.Serialize(new { text = value }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.InRange(Encoding.UTF8.GetByteCount(json), 1, 1024);
        var target = new RecoveryCounterCapability("string_target", ToolRecoveryGuarantee.QueryOutcome, fixedResultJson: json) { ThrowAfterEffect = true };
        var schema = ToolSchema.Parse(TransformTool.Schema);
        RuntimeToolRegistration[] tools = [new(new RecoverableCounterTool(maximumResultBytes: 1024, resultSchema: schema), target,
            new("keyed_recovery", target.TargetSnapshot, target.Guarantee))];
        var producer = new PersistentProvider(tools: true) { Respond = r => new(r.Scope, r.Attempt, ProviderFinish.ToolCalls, null,
            [new("string-result", "recover", "{\"amount\":1}")], new(r.Scope, r.Attempt, [1])) };
        var firstRequest = ToolContextFixture.Request(2, new(maximumToolInvocations: 1)); var sourceSink = new RestrictedContextHost();
        var source = await ToolContextFixture.Agent(producer, tools).ExecuteWithContextAsync(new(firstRequest, ContextExecutionIntent.Fresh), sourceSink);
        Assert.Equal(ContextCaptureStatus.Delivered, source.CaptureStatus); var envelope = sourceSink.CopyRestrictedContext();
        async Task<(ContextExecutionResult Result, RestrictedContextHost Sink, int Claims)> Run(int capacity, bool claim)
        {
            var request = ToolContextFixture.Request(2, new(maximumToolInvocations: 1));
            var authority = new Authority(envelope, source.Checkpoint!, claim); var sink = new RestrictedContextHost();
            var provider = new PersistentProvider(tools: true) { Respond = ToolContextFixture.Final };
            provider.Inspect = r => { var result = Assert.Single(r.Inputs, i => i.ToolResult is not null).ToolResult!;
                Assert.True(result.IsHistorical); Assert.True(result.InvocationStarted); Assert.Equal(json, result.Json); };
            var result = await ToolContextFixture.Agent(provider, tools, authority, new(maximumRetainedBytes: capacity, requireContinuation: true))
                .ExecuteWithContextAsync(new(request, ContextExecutionIntent.ContinueRun, envelope, OrdinaryFixture.Grant(source, request.ExecutionId)), sink);
            return (result, sink, authority.Claims);
        }
        var low = 1; var high = 196608;
        while (low < high) { var middle = low + (high - low) / 2; var probe = await Run(middle, false); if (probe.Claims == 1) high = middle; else low = middle + 1; }
        var below = await Run(low - 1, true); Assert.Equal(ContextAdmission.Rejected, below.Result.Admission); Assert.Equal(0, below.Claims);
        Assert.Equal(0, target.Queries); Assert.Equal(1, target.Invocations);
        var exact = await Run(low, true); Assert.Equal(ContextAdmission.Supplied, exact.Result.Admission);
        Assert.Equal(AgentTerminationReason.Completed, exact.Result.Outcome!.Reason); Assert.Equal(ContextCaptureStatus.Delivered, exact.Result.CaptureStatus);
        Assert.Equal(1, exact.Result.Outcome.Usage!.ToolInvocations!.Invoked); Assert.Equal(1, target.Queries); Assert.Equal(1, target.Effects);
        Assert.InRange(exact.Sink.CopyRestrictedContext().PayloadByteCount, 1, low);
    }
    private sealed class Authority(AgentContextEnvelope envelope, ContextCheckpointInfo source, bool claim) : IRuntimeContextAuthority
    {
        private readonly SelectedAuthority selected = new(envelope, source);
        internal int Claims;
        public bool TryClaim(AgentContextEnvelope context, ContextRoundGrant grant) { Claims++; return claim && selected.TryClaim(context, grant); }
    }
}

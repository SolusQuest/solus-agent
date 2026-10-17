using System.Text;
using System.Text.Json;
using CustomTools;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Capacity;

public sealed class CapacityExecutionTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("&")]
    [InlineData("\\")]
    [InlineData("\"")]
    [InlineData("\0")]
    [InlineData("汉")]
    [InlineData("😀")]
    public async Task FullArtifactSurvivesGuardedInvocationAndCompleteNextRequest(string token)
    {
        var artifact = CapacityFixture.Artifact(CapacityFixture.MiB, token);
        using var original = JsonDocument.Parse(artifact);
        var envelope = CapacityFixture.Envelope(artifact);
        Assert.InRange(CapacityFixture.Bytes(envelope), CapacityFixture.MiB + 1, 8 * CapacityFixture.MiB);
        var capability = new TransformCapability(); var tool = CapacityFixture.Tool(envelope);
        var call = CapacityFixture.Call(arguments: envelope);
        var replay = Enumerable.Range(0, 32768).Select(i => (byte)i).ToArray();
        ProviderResponse? model = null; var initialBytes = 0;
        var provider = new ScriptedProvider([
            (r, o, _) =>
            {
                initialBytes = r.PayloadByteCount; o.CaptureUsage(new(3, 2));
                model = new(r.Scope, r.Attempt, ProviderFinish.ToolCalls, null, [call], new(r.Scope, r.Attempt, replay));
                return ValueTask.FromResult(model);
            },
            (r, o, t) =>
            {
                var actual = Assert.Single(r.Inputs, i => i.ToolResult is not null).ToolResult!;
                Assert.True(actual.Call.Matches(call)); CapacityFixture.Same(envelope, actual.Json!);
                using var decoded = JsonDocument.Parse(actual.Json!);
                CapacityFixture.Same(artifact, decoded.RootElement.GetProperty("text").GetString()!);
                Assert.Equal(replay, r.Continuation!.CopyReplayBytes());
                var scopeBytes = CapacityFixture.Bytes(r.Scope.Provider) + CapacityFixture.Bytes(r.Scope.Model);
                Assert.Equal(initialBytes + model!.PayloadByteCount + CapacityFixture.CallBytes(call) + CapacityFixture.Bytes(envelope)
                    + replay.Length + scopeBytes, r.PayloadByteCount);
                var projection = JsonSerializer.SerializeToUtf8Bytes(new { call = call.CallId, argument = artifact, result = artifact, replay });
                Assert.True(projection.Length <= 32 * CapacityFixture.MiB);
                Assert.NotEqual(projection.Length, r.PayloadByteCount);
                var history = ToolResult.RestoreHistorical(tool.Descriptor, call, actual.Call, actual.Outcome, actual.Error, actual.InvocationStarted, actual.Json);
                Assert.True(history.IsHistorical); CapacityFixture.Same(envelope, history.Json!);
                return CapacityFixture.Final(r, o, t);
            }
        ]);
        var outcome = await CapacityFixture.Agent(provider, [new(tool, capability)], options: CapacityFixture.Options(continuation: true))
            .ExecuteAsync(CapacityFixture.Request());
        Assert.Equal(AgentTerminationReason.Completed, outcome.Reason); Assert.Equal(2, provider.Effects); Assert.Equal(1, capability.Effects);
        Assert.Equal(2, outcome.CompletedWorkUnits); Assert.Equal(2, outcome.Usage!.Attempts.Count);
        Assert.Equal(1, outcome.Usage.ToolInvocations!.Invoked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualLargeInputReachesProviderWithLogicalAndWireSizesMeasured(bool logicalRequest)
    {
        var input = logicalRequest ? CapacityFixture.LogicalRequest() : new string('x', 4 * CapacityFixture.MiB);
        var expected = logicalRequest ? 32 * CapacityFixture.MiB : 4 * CapacityFixture.MiB;
        Assert.Equal(expected, CapacityFixture.Bytes(input));
        var provider = new ScriptedProvider([(r, o, t) =>
        {
            var data = Assert.Single(r.Inputs, i => i.Kind == ProviderInputKind.InputData);
            CapacityFixture.Same(input, data.Text!); Assert.Equal(32 * CapacityFixture.MiB, r.Bounds.MaximumTextBytes);
            if (logicalRequest)
            {
                using var source = JsonDocument.Parse(data.Text!);
                var blocks = source.RootElement.GetProperty("blocks").EnumerateArray().Select(b => b.GetString()!).ToArray();
                Assert.Equal(8, blocks.Length); Assert.All(blocks, b => Assert.InRange(CapacityFixture.Bytes(b), 1, 4 * CapacityFixture.MiB));
                var wire = JsonSerializer.SerializeToUtf8Bytes(new { messages = blocks.Select(b => new { role = "user", content = b }) });
                Assert.InRange(wire.Length, 31 * CapacityFixture.MiB, 32 * CapacityFixture.MiB);
                Assert.NotEqual(wire.Length, r.PayloadByteCount);
            }
            return CapacityFixture.Final(r, o, t);
        }]);
        var outcome = await CapacityFixture.Agent(provider).ExecuteAsync(CapacityFixture.Request(1, [new(AgentInputSource.Repository, input)]));
        Assert.Equal(AgentTerminationReason.Completed, outcome.Reason); Assert.Equal(1, provider.Effects);
    }

    [Fact]
    public async Task NearNormalizedLimitContinuationReplaysExactlyAndChargesBothCopies()
    {
        var continuationContent = new string('r', CapacityFixture.MiB - 1024);
        var normalized = JsonSerializer.SerializeToUtf8Bytes(new { calls = new[] { new { id = "c", arguments = "{}" } }, continuationContent });
        Assert.InRange(normalized.Length, CapacityFixture.MiB - 1024, CapacityFixture.MiB);
        var replay = new byte[normalized.Length + 1]; replay[0] = 1; normalized.CopyTo(replay, 1);
        var call = CapacityFixture.Call(); var resultJson = CapacityFixture.Envelope("{}");
        var capability = new TransformCapability(); ProviderResponse? model = null; var initial = 0;
        var provider = new ScriptedProvider([
            (r, o, _) => { initial = r.PayloadByteCount; o.CaptureUsage(new(3, 2));
                model = new(r.Scope, r.Attempt, ProviderFinish.ToolCalls, null, [call], new(r.Scope, r.Attempt, replay)); return ValueTask.FromResult(model); },
            (r, o, t) =>
            {
                Assert.Equal(replay, r.Continuation!.CopyReplayBytes());
                Assert.Equal(initial + model!.PayloadByteCount + CapacityFixture.CallBytes(call) + CapacityFixture.Bytes(resultJson)
                    + replay.Length + CapacityFixture.Bytes(r.Scope.Provider + r.Scope.Model), r.PayloadByteCount);
                var altered = replay.ToArray(); altered[^1] ^= 1;
                var wrong = new ProviderContinuation(r.Scope, model.Attempt, altered);
                Assert.Equal(ProviderError.ContinuationMismatch, Assert.Throws<ProviderContractException>(() => new ProviderRequest(
                    r.Scope, r.Attempt, r.Inputs, r.Tools, wrong, r.RequiredCapabilities, r.Bounds, r.History)).Error);
                return CapacityFixture.Final(r, o, t);
            }
        ]);
        var outcome = await CapacityFixture.Agent(provider, [new(CapacityFixture.Tool(resultJson), capability)], options: CapacityFixture.Options(true))
            .ExecuteAsync(CapacityFixture.Request());
        Assert.Equal(AgentTerminationReason.Completed, outcome.Reason); Assert.Equal(1, capability.Effects); Assert.Equal(2, provider.Effects);
    }

    [Theory]
    [InlineData(128, 1090, 1089, true)]
    [InlineData(127, 1090, 1089, false)]
    [InlineData(128, 1089, 1089, false)]
    [InlineData(128, 1090, 1088, false)]
    public async Task RealDispatchAndToolInventoryHaveExactAdmissionNeighbors(int attempts, int records, int inputs, bool complete)
    {
        var resultJson = CapacityFixture.Envelope("{}"); var capability = new TransformCapability();
        var steps = new List<Func<ProviderRequest, ProviderObservation, CancellationToken, ValueTask<ProviderResponse>>>();
        for (var batch = 0; batch < 64; batch++)
        {
            var index = batch;
            if (batch < 63) steps.Add(ScriptedProvider.Failure(new(ProviderRetryKind.Transient)));
            steps.Add((r, o, _) => { o.CaptureUsage(new(3, 2)); return ValueTask.FromResult(new ProviderResponse(r.Scope, r.Attempt,
                ProviderFinish.ToolCalls, null, Enumerable.Range(0, 16).Select(i => CapacityFixture.Call($"b{index}_c{i}")).ToArray())); });
        }
        steps.Add((r, o, t) =>
        {
            Assert.Equal(1089, r.Inputs.Count); Assert.Equal(64, r.Inputs.Count(i => i.Model is not null));
            var results = r.Inputs.Where(i => i.ToolResult is not null).Select(i => i.ToolResult!).ToArray();
            Assert.Equal(1024, results.Length); Assert.Equal(1024, results.Select(i => i.Call.CallId).Distinct().Count());
            Assert.All(results, i => { Assert.Equal(ToolOutcome.Succeeded, i.Outcome); Assert.True(i.InvocationStarted); Assert.Equal(resultJson, i.Json); });
            return CapacityFixture.Final(r, o, t);
        });
        var provider = new ScriptedProvider(steps);
        var bounds = new ProviderExchangeBounds(maximumInputs: inputs, maximumRequestBytes: 128 * CapacityFixture.MiB,
            maximumResponseBytes: 8 * CapacityFixture.MiB, maximumTextBytes: 32 * CapacityFixture.MiB);
        var options = new RuntimeOptions(maximumAttempts: attempts, maximumRecords: records, maximumRetainedBytes: 256 * CapacityFixture.MiB);
        var usage = new AgentUsageLimits(maximumLogicalCalls: 65, maximumPhysicalDispatches: 128, maximumToolInvocations: 1024, retryPolicy: new(2));
        var outcome = await CapacityFixture.Agent(provider, [new(CapacityFixture.Tool(resultJson, resultLimit: 1024), capability)], bounds, options)
            .ExecuteAsync(CapacityFixture.Request(65, usage: usage));
        Assert.Equal(complete ? AgentTerminationReason.Completed : AgentTerminationReason.ResourceLimit, outcome.Reason);
        Assert.Equal(complete ? 128 : 127, provider.Effects); Assert.Equal(1024, capability.Effects);
        Assert.Equal(complete ? 65 : 64, outcome.CompletedWorkUnits); Assert.Equal(provider.Effects, outcome.Usage!.Attempts.Count);
        Assert.Equal(1024, outcome.Usage.ToolInvocations!.Invoked); Assert.Equal(63, outcome.Usage.Attempts.GroupBy(a => a.LogicalCallId).Count(g => g.Count() == 2));
        Assert.Equal(3L * provider.Effects, outcome.Usage.InputTokens.ObservedTokens); Assert.Equal(2L * provider.Effects, outcome.Usage.OutputTokens.ObservedTokens);
    }
}

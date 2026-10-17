using System.Text.Json;
using System.Text.Json.Nodes;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.ContractTests.Runtime.Execution;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using Xunit;

namespace SolusAgent.ContractTests.Runtime.Context;

public sealed class RetryCapacityContextTests
{
    private static AgentRequest Request(int policy, int physical) => OrdinaryFixture.Request(limits: RetryCapacityTests.Limits(policy, physical));
    private static async Task<(ContextExecutionResult Result, AgentContextEnvelope Envelope, PersistentProvider Provider)> Source(int count)
    {
        var provider = new PersistentProvider(true); var hooks = new RuntimeHooks();
        var (result, envelope) = await OrdinaryFixture.Capture(provider: provider, hooks: hooks,
            request: Request(128, count), options: new(maximumAttempts: 128, requireContinuation: true));
        Assert.Equal(AgentTerminationReason.ResourceLimit, result.Outcome!.Reason);
        Assert.Equal(ContextCaptureStatus.Delivered, result.CaptureStatus); Assert.Equal(count, provider.Effects);
        RetryCapacityTests.AssertAccounting(result.Outcome, hooks, count);
        return (result, envelope, provider);
    }

    [Theory]
    [InlineData(64, 64, 128, 64, true)]
    [InlineData(63, 65, 128, 65, true)]
    [InlineData(64, 63, 128, 64, false)]
    [InlineData(64, 64, 127, 64, false)]
    public async Task CurrentPolicyRenewsWhileGlobalLineageAndHistoricalInventoryRemain(int historicalCount, int policy, int inventory, int needed, bool completed)
    {
        var (source, envelope, original) = await Source(historicalCount);
        var predecessor = source.Outcome!.Usage!.Attempts[^1]; var seen = new List<ProviderRequest>();
        var provider = new PersistentProvider { Inspect = r => seen.Add(r), Respond = r =>
        {
            if (seen.Count < needed) throw new ProviderFailureException(new(ProviderRetryKind.Transient));
            return RuntimeFixture.Final(r, continuation: new(r.Scope, r.Attempt, [1]));
        } };
        var hooks = new RuntimeHooks(); var authority = new SelectedAuthority(envelope, source.Checkpoint!);
        var request = Request(policy, needed); var sink = new RestrictedContextHost();
        var result = await OrdinaryFixture.Agent(provider, authority, hooks, new(maximumAttempts: inventory, requireContinuation: true))
            .ExecuteWithContextAsync(new(request, ContextExecutionIntent.ContinueRun, envelope, OrdinaryFixture.Grant(source, request.ExecutionId)), sink);
        var count = completed ? needed : needed - 1;
        Assert.Equal(ContextAdmission.Supplied, result.Admission);
        Assert.Equal(completed ? AgentTerminationReason.Completed : policy < needed ? AgentTerminationReason.Failed : AgentTerminationReason.ResourceLimit,
            result.Outcome!.Reason);
        Assert.Equal(completed ? 1 : 0, result.Outcome.CompletedWorkUnits);
        Assert.Equal(1, authority.Claims); Assert.Equal(1, provider.Imports); Assert.Equal(count, provider.Effects);
        Assert.Equal(historicalCount, original.Effects);
        var history = Assert.Single(result.History);
        Assert.Equal(source.Outcome.Usage.Attempts.Select(a => a.PhysicalAttemptId), history.Usage.Attempts.Select(a => a.PhysicalAttemptId));
        Assert.Equal(3L * historicalCount, history.Usage.InputTokens.ObservedTokens);
        Assert.Equal(2L * historicalCount, history.Usage.OutputTokens.ObservedTokens);
        Assert.Equal(Enumerable.Range(historicalCount + 1, count), result.Outcome.Usage!.Attempts.Select(a => a.AttemptNumber));
        var all = history.Usage.Attempts.Concat(result.Outcome.Usage.Attempts).ToArray();
        Assert.Single(all.Select(a => a.LogicalCallId).Distinct()); Assert.Equal(all.Length, all.Select(a => a.PhysicalAttemptId).Distinct().Count());
        Assert.All(result.Outcome.Usage.Attempts, a => Assert.Equal(request.ExecutionId, a.ExecutionId));
        Assert.NotEqual(source.Outcome.ExecutionId, request.ExecutionId);
        var lineage = Assert.Single(result.Outcome.Usage.ContinuedCalls);
        Assert.Equal(predecessor.PhysicalAttemptId, lineage.PhysicalAttemptId); Assert.Equal(historicalCount, lineage.AttemptNumber);
        Assert.Equal(predecessor.ExecutionId, lineage.ExecutionId); Assert.Equal(predecessor.LogicalCallId, lineage.LogicalCallId);
        Assert.All(seen, r =>
        {
            Assert.True(r.History!.OperationOrigin!.Matches(original.LastRequest!.Attempt));
            Assert.Equal(original.LastRequest.Inputs.Select(i => (i.Kind, i.Text)), r.Inputs.Select(i => (i.Kind, i.Text)));
            Assert.All(r.Inputs, i => { Assert.Null(i.Model); Assert.Null(i.ToolResult); }); Assert.Empty(r.Tools);
        });
        Assert.True(Assert.Single(result.Outcome.Usage.Accounting!.ContinuedCalls).Matches(lineage));
        RetryCapacityTests.AssertAccounting(result.Outcome, hooks, count);
        Assert.Equal(3L * needed + 2 - 3L * count, result.Outcome.Usage.Accounting.Input.RemainingAllowance);
        Assert.Equal(0, hooks.Exposures[0].Accounting!.Input.MeasuredTokens);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExhaustedHistoricalInventoryOrInvalidPredecessorRejectsBeforeClaim(bool invalidLineage)
    {
        var (source, envelope, original) = await Source(64);
        if (invalidLineage)
        {
            var document = JsonNode.Parse(envelope.CopyRestrictedPayload())!;
            document["Pending"]!["AttemptNumber"] = 63;
            envelope = new(envelope.ImplementationId, envelope.FormatVersion, envelope.CompatibilityVersion, JsonSerializer.SerializeToUtf8Bytes(document));
        }
        var provider = new PersistentProvider(); var authority = new SelectedAuthority(envelope, source.Checkpoint!); var hooks = new RuntimeHooks();
        var request = Request(64, 64);
        var result = await OrdinaryFixture.Agent(provider, authority, hooks,
            new(maximumAttempts: invalidLineage ? 128 : 64, requireContinuation: true)).ExecuteWithContextAsync(
                new(request, ContextExecutionIntent.ContinueRun, envelope, OrdinaryFixture.Grant(source, request.ExecutionId)));
        Assert.Equal(ContextAdmission.Rejected, result.Admission); Assert.Null(result.Outcome);
        Assert.Equal(0, authority.Claims); Assert.Equal(0, provider.Imports); Assert.Equal(0, provider.Effects); Assert.Empty(hooks.Exposures);
        Assert.Equal(64, original.Effects);
    }
}

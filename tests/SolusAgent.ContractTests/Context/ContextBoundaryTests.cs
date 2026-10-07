using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer;
using SolusAgent.ApiOnlyConsumer.Context;
using SolusAgent.ContractTests.Architecture;
using Xunit;

namespace SolusAgent.ContractTests.Context;

public sealed class ContextBoundaryTests
{
    [Fact]
    public void RealContextProducerAndHostRemainIndependentlyCompiledApiOnlyConsumers()
    {
        var project = Path.Combine(RepositoryLayout.Root, "tests", "SolusAgent.ApiOnlyConsumer", "SolusAgent.ApiOnlyConsumer.csproj");
        var evaluation = MsbuildProjectEvaluation.Evaluate(project);
        ProjectBoundaryAssertions.AssertExactProjectReferences(evaluation, [RepositoryLayout.ProductionProjectPath("SolusAgent.Api")]);
        ProjectBoundaryAssertions.AssertNoPackages(evaluation);
        ProjectBoundaryAssertions.AssertManagedNet10(evaluation);
        ProjectBoundaryAssertions.AssertCompileSourcesWithinRoot(evaluation, RepositoryLayout.Root);
        Assert.Contains(evaluation.CompileItemPaths, value => value.EndsWith("ScriptedContextAgent.cs", StringComparison.Ordinal));
        Assert.Contains(evaluation.CompileItemPaths, value => value.EndsWith("ContextConsumer.cs", StringComparison.Ordinal));
        Assert.Equal(["SolusAgent.Api"], typeof(ScriptedContextAgent).Assembly.GetReferencedAssemblies()
            .Where(value => value.Name!.StartsWith("SolusAgent.", StringComparison.Ordinal)).Select(value => value.Name));
        Assert.Equal(nameof(IContextAgent.ExecuteWithContextAsync), Assert.Single(typeof(IContextAgent).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)).Name);
        Assert.DoesNotContain(typeof(ScriptedContextAgent).GetFields(BindingFlags.Instance | BindingFlags.NonPublic), value => value.FieldType == typeof(AgentContextEnvelope));
        Assert.DoesNotContain(typeof(ObservedContextExecution).GetProperties(), value => value.PropertyType == typeof(AgentContextEnvelope));
    }

    [Fact]
    public async Task ActualCaptureIsRestrictedAndItsClosedSchemaExcludesHostControlsAndLiveObjects()
    {
        var host = new RestrictedContextHost();
        const string control = "synthetic-Host-control-credential-exclusion";
        var request = new ContextExecutionRequest(ContextCases.Request(data: [new(AgentInputSource.Model, ContextCases.Canary)], instructions: control), ContextExecutionIntent.Fresh);
        var execution = await ContextConsumer.RunAsync(new ScriptedContextAgent(3), request, host);
        Assert.True(execution.Result.Outcome!.IsCompleted);
        Assert.Equal(ContextCaptureStatus.Delivered, execution.Result.CaptureStatus);
        Assert.Equal(1, host.CaptureCount);
        var envelope = host.CopyRestrictedContext();
        var payload = Encoding.UTF8.GetString(envelope.CopyRestrictedPayload());
        Assert.Contains(ContextCases.Canary, payload);
        Assert.DoesNotContain(control, payload);
        using var document = JsonDocument.Parse(payload);
        Assert.Equal(["OriginExecutionId", "CompletedWorkUnits", "TargetWorkUnits", "Finished", "Data"], document.RootElement.EnumerateObject().Select(value => value.Name));
        Assert.Equal(["Source", "Text"], document.RootElement.GetProperty("Data")[0].EnumerateObject().Select(value => value.Name));
        foreach (var ordinary in new[] { JsonSerializer.Serialize(execution), JsonSerializer.Serialize(execution.Result.Outcome), JsonSerializer.Serialize(execution.Progress),
            JsonSerializer.Serialize(envelope), request.ToString(), envelope.ToString(), execution.Result.ToString(), execution.ToString(), host.ToString() })
        {
            Assert.DoesNotContain(ContextCases.Canary, ordinary);
            Assert.DoesNotContain(control, ordinary);
        }
        AssertSafeGraph(execution.Result);
        Assert.All(execution.Progress, value => AssertSafeGraph(value));
        var copy = envelope.CopyRestrictedPayload(); Array.Fill(copy, (byte)0);
        Assert.Contains(ContextCases.Canary, Encoding.UTF8.GetString(host.CopyRestrictedContext().CopyRestrictedPayload()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureFailureIsSeparateFromWorkOutcomeAndDoesNotClaimAbsentHostRetention(bool storeFirst)
    {
        var sink = new ThrowingSink(storeFirst);
        var agent = new ScriptedContextAgent(1);
        var execution = await ContextConsumer.RunAsync(agent, new(ContextCases.Request(data: [new(AgentInputSource.Tool, ContextCases.Canary)]), ContextExecutionIntent.Fresh), sink);
        Assert.Equal(ContextCaptureStatus.Failed, execution.Result.CaptureStatus);
        Assert.Equal(AgentTerminationReason.Completed, execution.Result.Outcome!.Reason);
        Assert.Equal(AgentFailureCode.None, execution.Result.Outcome.FailureCode);
        Assert.Equal(1, execution.Result.Outcome.CompletedWorkUnits);
        Assert.Equal(1, agent.TotalWorkStarted);
        Assert.Equal(storeFirst ? 1 : 0, sink.Host.CaptureCount);
        if (storeFirst) Assert.Contains(ContextCases.Canary, Encoding.UTF8.GetString(sink.Host.CopyRestrictedContext().CopyRestrictedPayload()));
        Assert.DoesNotContain(ContextCases.Canary, JsonSerializer.Serialize(execution));
        AssertSafeGraph(execution.Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingCallsUseTheirOwnSinksEvenWithReusedCorrelation(bool sameCorrelation)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var agent = new ScriptedContextAgent(1, performWork: async (_, _, _, token) =>
        {
            if (Interlocked.Increment(ref calls) == 2) entered.SetResult();
            await release.Task.WaitAsync(token);
        });
        var left = new RestrictedContextHost(); var right = new RestrictedContextHost();
        var first = ContextConsumer.RunAsync(agent, new(ContextCases.Request(data: [new(AgentInputSource.Repository, "left-" + ContextCases.Canary)]), ContextExecutionIntent.Fresh), left).AsTask();
        var secondId = sameCorrelation ? ContextCases.Id : Guid.NewGuid();
        var second = ContextConsumer.RunAsync(agent, new(ContextCases.Request(id: secondId, data: [new(AgentInputSource.Tool, "right-" + ContextCases.Canary)]), ContextExecutionIntent.Fresh), right).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, left.CaptureCount); Assert.Equal(0, right.CaptureCount);
        release.SetResult();
        var executions = await Task.WhenAll(first, second);
        Assert.Equal(2, agent.TotalWorkStarted);
        Assert.All(executions, value => Assert.Equal(ContextCaptureStatus.Delivered, value.Result.CaptureStatus));
        Assert.Equal(1, left.CaptureCount); Assert.Equal(1, right.CaptureCount);
        var leftBytes = left.CopyRestrictedContext().CopyRestrictedPayload();
        var rightBytes = right.CopyRestrictedContext().CopyRestrictedPayload();
        Assert.Contains("left-" + ContextCases.Canary, Encoding.UTF8.GetString(leftBytes));
        Assert.DoesNotContain("right-" + ContextCases.Canary, Encoding.UTF8.GetString(leftBytes));
        Assert.Contains("right-" + ContextCases.Canary, Encoding.UTF8.GetString(rightBytes));
        Assert.DoesNotContain("left-" + ContextCases.Canary, Encoding.UTF8.GetString(rightBytes));
        Array.Fill(leftBytes, (byte)0);
        Assert.Contains("right-" + ContextCases.Canary, Encoding.UTF8.GetString(right.CopyRestrictedContext().CopyRestrictedPayload()));
        Assert.Equal(secondId, executions[1].Result.ExecutionId);
    }

    [Fact]
    public async Task SuppliedNewRunPreservesOnlyClassifiedDataAndCaptureUsesItsNewCorrelation()
    {
        var saved = await ContextCases.CaptureAsync(data: [new(AgentInputSource.Tool, ContextCases.Canary)]);
        var id = Guid.NewGuid(); var sink = new RestrictedContextHost();
        var execution = await ContextConsumer.RunAsync(new ScriptedContextAgent(1),
            new(ContextCases.Request(id: id, data: [new(AgentInputSource.Repository, "current input")]), ContextExecutionIntent.NewRunFromContext, saved), sink);
        Assert.Equal(1, execution.Result.Outcome!.CompletedWorkUnits);
        using var json = JsonDocument.Parse(sink.CopyRestrictedContext().CopyRestrictedPayload());
        Assert.Equal(id, json.RootElement.GetProperty("OriginExecutionId").GetGuid());
        Assert.Equal([AgentInputSource.Tool, AgentInputSource.Repository], json.RootElement.GetProperty("Data").EnumerateArray().Select(value => (AgentInputSource)value.GetProperty("Source").GetInt32()));
        Assert.Contains(ContextCases.Canary, json.RootElement.GetProperty("Data")[0].GetProperty("Text").GetString()!);
        Assert.DoesNotContain(ContextCases.Canary, JsonSerializer.Serialize(execution));
    }

    [Theory]
    [InlineData("work")]
    [InlineData("observer")]
    [InlineData("cancel")]
    public async Task SuppliedContinuationFailuresPreserveTruthfulCountsAndCapture(string variant)
    {
        var saved = await ContextCases.CaptureAsync(data: [new(AgentInputSource.Tool, ContextCases.Canary)]);
        using var cancellation = new CancellationTokenSource();
        var agent = new ScriptedContextAgent(3, performWork: (_, _, _, token) =>
        {
            if (variant == "work") throw new InvalidOperationException(ContextCases.Canary);
            if (variant == "cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            return ValueTask.CompletedTask;
        });
        var sink = new RestrictedContextHost();
        var result = await agent.ExecuteWithContextAsync(new(ContextCases.Request(), ContextExecutionIntent.ContinueRun, saved), sink,
            variant == "observer" ? new ThrowingObserver() : null, cancellation.Token);
        Assert.Equal(ContextAdmission.Supplied, result.Admission);
        Assert.Equal(variant == "cancel" ? AgentTerminationReason.Cancelled : AgentTerminationReason.Failed, result.Outcome!.Reason);
        Assert.Equal(variant == "observer" ? 2 : 1, result.Outcome.CompletedWorkUnits);
        Assert.Equal(variant == "observer" ? AgentFailureCode.ProgressObserverFailed : variant == "work" ? AgentFailureCode.ExecutionFailed : AgentFailureCode.None, result.Outcome.FailureCode);
        Assert.Equal(1, agent.TotalWorkStarted);
        Assert.Equal(ContextCaptureStatus.Delivered, result.CaptureStatus);
        Assert.Contains(ContextCases.Canary, Encoding.UTF8.GetString(sink.CopyRestrictedContext().CopyRestrictedPayload()));
        Assert.DoesNotContain(ContextCases.Canary, JsonSerializer.Serialize(result));
    }

    [Fact]
    public void OrdinaryContextResultsKeepAcceptedUsageMetadataAndClosedCapabilities()
    {
        var attempt = new UsageAttemptObservation(ContextCases.Id, Guid.NewGuid(), Guid.NewGuid(), 1, DispatchExposure.Dispatched,
            new UsageObservation(2, null, [new ProviderTokenCounter(ProviderTokenCounterKind.Reasoning, 1, TokenCounterRelationship.Independent)]),
            new UsageAccounting(estimatedCost: new UsageCostEstimate(0.01m, "USD")));
        var usage = new AgentRunUsage(ContextCases.Id, UsageInventoryCoverage.Complete, [attempt]);
        var outcome = new AgentOutcome(ContextCases.Id, AgentTerminationReason.Completed, 1, usage: usage);
        var result = new ContextExecutionResult(ContextCases.Id, ContextExecutionIntent.Fresh, ContextAdmission.Fresh, outcome);
        Assert.Same(usage, result.Outcome!.Usage);
        AssertSafeGraph(result);
        var unsupported = new ContextExecutionResult(ContextCases.Id, ContextExecutionIntent.Fresh, ContextAdmission.NotAttempted,
            new AgentOutcome(ContextCases.Id, AgentTerminationReason.UnsupportedCapability, 0, AgentCapability.DurationLimit));
        AssertSafeGraph(unsupported);
        var cancelledWithAttempt = new AgentOutcome(ContextCases.Id, AgentTerminationReason.Cancelled, 0, usage: usage);
        Assert.Throws<ArgumentException>(() => new ContextExecutionResult(ContextCases.Id, ContextExecutionIntent.Fresh, ContextAdmission.NotAttempted, cancelledWithAttempt));
        Assert.Same(cancelledWithAttempt, new ContextExecutionResult(ContextCases.Id, ContextExecutionIntent.Fresh, ContextAdmission.Fresh, cancelledWithAttempt).Outcome);
    }

    [Fact]
    public async Task ExistingOrdinaryInterfacePathRemainsExecutableWithoutImplicitCapture()
    {
        var agent = new ScriptedContextAgent(1);
        var ordinary = await AgentConsumer.RunAsync(agent, ContextCases.Request());
        Assert.True(ordinary.Outcome.IsCompleted);
        Assert.Equal(1, agent.OrdinaryWorkStarted);
        Assert.Equal(0, agent.TotalWorkStarted);
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await agent.ExecuteWithContextAsync(null!));
        Assert.Throws<InvalidOperationException>(() => new RestrictedContextHost().CopyRestrictedContext());
    }

    private static void AssertSafeGraph(object? value)
    {
        if (value is null) return;
        var type = value.GetType();
        if (type.IsEnum || type.IsPrimitive || value is Guid or decimal or TimeSpan) return;
        Assert.False(value is byte[] or Exception or Delegate or AgentRequest or AgentContextEnvelope or IRestrictedContextSink);
        if (value is IEnumerable values) { foreach (var item in values) AssertSafeGraph(item); return; }
        Assert.Contains(type.Namespace, new[] { "SolusAgent.Api.Context", "SolusAgent.Api.Execution", "SolusAgent.Api.Usage" });
        foreach (var property in type.GetProperties())
        {
            Assert.Null(property.SetMethod);
            var item = property.GetValue(value);
            if (item is string text)
            {
                Assert.Equal(typeof(UsageCostEstimate), type);
                Assert.Equal(nameof(UsageCostEstimate.Currency), property.Name);
                Assert.Matches("^[A-Z]{3}$", text);
            }
            else AssertSafeGraph(item);
        }
    }

    private sealed class ThrowingObserver : IProgress<AgentProgress>
    {
        public void Report(AgentProgress value) => throw new InvalidOperationException(ContextCases.Canary);
    }
    private sealed class ThrowingSink(bool storeFirst) : IRestrictedContextSink
    {
        internal RestrictedContextHost Host { get; } = new();
        public void Capture(AgentContextEnvelope context)
        {
            if (storeFirst) Host.Capture(context);
            throw new InvalidOperationException(ContextCases.Canary);
        }
    }
}

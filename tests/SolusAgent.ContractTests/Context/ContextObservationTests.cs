using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ApiOnlyConsumer.Context;
using Xunit;

namespace SolusAgent.ContractTests.Context;

public sealed class ContextObservationTests
{
    [Theory]
    [InlineData("checkpoint")]
    [InlineData("work")]
    [InlineData("round")]
    [InlineData("execution")]
    [InlineData("other-execution")]
    public async Task AlternateProducerCannotPublishMalformedCheckpoint(string variant)
    {
        var id = Guid.NewGuid();
        var checkpoint = new ContextCheckpointInfo(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), id);
        checkpoint = variant switch
        {
            "checkpoint" => checkpoint with { CheckpointId = Guid.Empty },
            "work" => checkpoint with { LogicalWorkId = Guid.Empty },
            "round" => checkpoint with { RoundId = Guid.Empty },
            "execution" => checkpoint with { ExecutionId = Guid.Empty },
            _ => checkpoint with { ExecutionId = Guid.NewGuid() },
        };
        var agent = new ObservationAgent(() => new(id, ContextExecutionIntent.Fresh, ContextAdmission.Fresh,
            new(id, AgentTerminationReason.Completed, 1), captureStatus: ContextCaptureStatus.Delivered, checkpoint: checkpoint));
        await Assert.ThrowsAsync<ArgumentException>(async () => await ContextConsumer.RunAsync(agent,
            new(ContextCases.Request(id: id), ContextExecutionIntent.Fresh)));
    }

    [Theory]
    [InlineData("work")]
    [InlineData("round")]
    [InlineData("reason")]
    [InlineData("completed")]
    [InlineData("usage")]
    [InlineData("null-entry")]
    public async Task AlternateProducerCannotPublishMalformedHistory(string variant)
    {
        var id = Guid.NewGuid();
        var history = new ContextRoundObservation(Guid.NewGuid(), Guid.NewGuid(), new(id, UsageInventoryCoverage.Complete, []),
            AgentTerminationReason.Completed, 1);
        history = variant switch
        {
            "work" => history with { LogicalWorkId = Guid.Empty },
            "round" => history with { RoundId = Guid.Empty },
            "reason" => history with { Reason = (AgentTerminationReason)99 },
            "completed" => history with { CompletedWorkUnits = -1 },
            "usage" => history with { Usage = null! },
            _ => null!,
        };
        var agent = new ObservationAgent(() => new(id, ContextExecutionIntent.NewRunFromContext, ContextAdmission.Supplied,
            new(id, AgentTerminationReason.Completed, 1), history: [history]));
        await Assert.ThrowsAsync<ArgumentException>(async () => await ContextConsumer.RunAsync(agent,
            new(ContextCases.Request(id: id), ContextExecutionIntent.NewRunFromContext, ContextCases.Envelope([1]))));
    }

    [Theory]
    [InlineData(0, AgentTerminationReason.Cancelled)]
    [InlineData(1, AgentTerminationReason.Completed)]
    public async Task ValidHistoricalFactsRemainImmutableWithoutImposingHostCorrelationUniqueness(int completed, AgentTerminationReason reason)
    {
        var id = Guid.NewGuid(); var work = Guid.NewGuid(); var round = Guid.NewGuid();
        var usage = new AgentRunUsage(id, UsageInventoryCoverage.Complete, []);
        var historical = new ContextRoundObservation(work, round, usage, reason, completed);
        var supplied = new[] { historical };
        var checkpoint = new ContextCheckpointInfo(Guid.NewGuid(), work, Guid.NewGuid(), id);
        var result = new ContextExecutionResult(id, ContextExecutionIntent.NewRunFromContext, ContextAdmission.Supplied,
            new(id, AgentTerminationReason.Completed, 1), captureStatus: ContextCaptureStatus.Delivered,
            checkpoint: checkpoint, history: supplied);
        supplied[0] = null!;
        var observed = await ContextConsumer.RunAsync(new ObservationAgent(() => result), new(ContextCases.Request(id: id), ContextExecutionIntent.NewRunFromContext, ContextCases.Envelope([1])));
        Assert.Same(checkpoint, observed.Result.Checkpoint);
        Assert.Same(historical, Assert.Single(observed.Result.History));
        Assert.Same(usage, observed.Result.History[0].Usage);
        Assert.Throws<NotSupportedException>(() => ((IList<ContextRoundObservation>)observed.Result.History).Clear());
    }

    private sealed class ObservationAgent(Func<ContextExecutionResult> create) : IContextAgent
    {
        public AgentCapability SupportedCapabilities => AgentCapability.None;
        public ValueTask<AgentOutcome> ExecuteAsync(AgentRequest request, IProgress<AgentProgress>? progress = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(create().Outcome!);
        public ValueTask<ContextExecutionResult> ExecuteWithContextAsync(ContextExecutionRequest request, IRestrictedContextSink? contextSink = null,
            IProgress<AgentProgress>? progress = null, CancellationToken cancellationToken = default) => ValueTask.FromResult(create());
    }
}

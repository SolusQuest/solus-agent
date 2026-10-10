using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;

namespace SolusAgent.Api.Context;

/// <summary>Closed checkpoint correlation, without restricted state or storage authority.</summary>
public sealed record ContextCheckpointInfo(Guid CheckpointId, Guid LogicalWorkId, Guid RoundId, Guid ExecutionId);

/// <summary>Explicit Host authorization for one source-bound new execution round; not provenance or a durable claim.</summary>
public sealed class ContextRoundGrant
{
    /// <summary>Creates a nonempty source and distinct target binding. The Host must exclusively claim its current recovery source.</summary>
    public ContextRoundGrant(Guid grantId, ContextCheckpointInfo source, Guid executionId, Guid roundId)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (grantId == Guid.Empty || source.CheckpointId == Guid.Empty || source.LogicalWorkId == Guid.Empty
            || source.RoundId == Guid.Empty || source.ExecutionId == Guid.Empty || executionId == Guid.Empty || roundId == Guid.Empty
            || executionId == source.ExecutionId || roundId == source.RoundId) throw new ArgumentException("Invalid round binding.");
        GrantId = grantId; Source = source; ExecutionId = executionId; RoundId = roundId;
    }
    /// <summary>Gets the Host grant identity.</summary>
    public Guid GrantId { get; }
    /// <summary>Gets the exact selected source.</summary>
    public ContextCheckpointInfo Source { get; }
    /// <summary>Gets the new current execution.</summary>
    public Guid ExecutionId { get; }
    /// <summary>Gets the new execution round.</summary>
    public Guid RoundId { get; }
    /// <summary>Returns no stored state.</summary>
    public override string ToString() => nameof(ContextRoundGrant);
}

/// <summary>Immutable historical round facts, never current-round consumption or permission.</summary>
public sealed record ContextRoundObservation(Guid LogicalWorkId, Guid RoundId, AgentRunUsage Usage,
    AgentTerminationReason Reason, int CompletedWorkUnits);

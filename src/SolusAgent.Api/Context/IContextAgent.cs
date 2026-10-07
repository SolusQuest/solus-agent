using SolusAgent.Api.Execution;

namespace SolusAgent.Api.Context;

/// <summary>A deliberately restricted Host channel supplied for one context-aware invocation.</summary>
/// <remarks>The Host may keep, copy or discard state under its own integrity, storage, protection and retention policy. This is separate from ordinary progress and outcomes; callback completion is not a durability or effect guarantee.</remarks>
public interface IRestrictedContextSink
{
    /// <summary>Receives an immutable restricted envelope within the associated call, never through an ID-addressed agent store.</summary>
    void Capture(AgentContextEnvelope context);
}

/// <summary>Optional outer context boundary independent of a runtime transcript or storage service.</summary>
/// <remarks>Implementations validate their scoped compatibility/grammar/current authority before supplied state affects work, explicitly reject without fresh fallback, and document capture safe points. Context metadata is not authentication. Full provider/model/record validation and restoration remain implementation obligations beyond this M1 draft.</remarks>
public interface IContextAgent : IAgent
{
    /// <summary>Executes an explicit intent with ordinary observations and an optional separate restricted capture channel.</summary>
    /// <remarks>Required guarantees reject before work/progress/capture. Rejected context has no work outcome. Capture failures are separately observed, without raw exceptions or changing truthful work outcomes. No agent retention, execution-ID uniqueness, forced callback liveness or crash-recovery guarantee is selected.</remarks>
    /// <exception cref="ArgumentNullException">The request is null before invocation.</exception>
    ValueTask<ContextExecutionResult> ExecuteWithContextAsync(
        ContextExecutionRequest request,
        IRestrictedContextSink? contextSink = null,
        IProgress<AgentProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

using SolusAgent.Api.Execution;

namespace SolusAgent.Api.Context;

/// <summary>Explicit Host intent, without prescribing a universal execution-ID or restoration lifecycle.</summary>
public enum ContextExecutionIntent
{
    /// <summary>Start fresh without supplied state.</summary>
    Fresh,
    /// <summary>Start a new Host-authorized run using supplied implementation-owned state.</summary>
    NewRunFromContext,
    /// <summary>Continue unfinished work using supplied implementation-owned state.</summary>
    ContinueRun,
}

/// <summary>Current Host controls plus an explicit choice to supply restricted context.</summary>
/// <remarks>The request is input, not diagnostics. Supplied content cannot redefine trusted instructions, bounds, required guarantees or injected capabilities.</remarks>
public sealed class ContextExecutionRequest
{
    /// <summary>Validates explicit intent and context presence before execution.</summary>
    /// <exception cref="ArgumentNullException">The current Host request is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Intent is undefined.</exception>
    /// <exception cref="ArgumentException">Fresh has context, or a supplied intent has none.</exception>
    public ContextExecutionRequest(AgentRequest request, ContextExecutionIntent intent, AgentContextEnvelope? context = null,
        ContextRoundGrant? roundGrant = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(intent)) throw new ArgumentOutOfRangeException(nameof(intent));
        if ((intent == ContextExecutionIntent.Fresh) != (context is null))
            throw new ArgumentException("Context presence must agree with explicit intent.", nameof(context));
        Request = request;
        Intent = intent;
        Context = context;
        if (intent == ContextExecutionIntent.Fresh && roundGrant is not null) throw new ArgumentException("Fresh has no source grant.");
        RoundGrant = roundGrant;
    }

    /// <summary>Gets the current trusted Host control inputs and separately classified data.</summary>
    public AgentRequest Request { get; }
    /// <summary>Gets the explicit Host lifecycle choice.</summary>
    public ContextExecutionIntent Intent { get; }
    /// <summary>Gets restricted supplied state only for a supplied intent; never an ordinary outcome member.</summary>
    public AgentContextEnvelope? Context { get; }
    /// <summary>Gets explicit new-round authorization; implementations still require trusted provenance and exclusive Host claim.</summary>
    public ContextRoundGrant? RoundGrant { get; }
    /// <summary>Returns the intent without input text or restricted payload.</summary>
    public override string ToString() => $"ContextExecutionRequest {{ Intent = {Intent} }}";
}

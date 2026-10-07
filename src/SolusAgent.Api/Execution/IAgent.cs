using SolusAgent.Api.Capabilities;

namespace SolusAgent.Api.Execution;

/// <summary>An application-facing asynchronous agent independent of a particular runtime, model provider or tool registry.</summary>
public interface IAgent
{
    /// <summary>Gets the guarantees this implementation can honor for its execution path.</summary>
    AgentCapability SupportedCapabilities { get; }

    /// <summary>Executes a validated request, explicitly rejecting unsupported required guarantees before work or progress.</summary>
    /// <param name="request">Host-owned control inputs with separately classified untrusted data.</param>
    /// <param name="progress">Optional ordinary observations. Report calls are ordered per execution; observer delivery may be scheduled.</param>
    /// <param name="cancellationToken">Caller cancellation, observed cooperatively when advertised.</param>
    /// <returns>A correlated truthful terminal outcome; completion does not imply product acceptance or external effects.</returns>
    /// <remarks>Progress and controllable failure outcomes must omit raw content, candidates, restricted state, credentials and exception text. Implementations document their work-unit meaning and guarantees; this interface alone enforces none. Abrupt process death cannot promise a final result.</remarks>
    /// <exception cref="ArgumentNullException">The request is null, before execution begins.</exception>
    ValueTask<AgentOutcome> ExecuteAsync(
        AgentRequest request,
        IProgress<AgentProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

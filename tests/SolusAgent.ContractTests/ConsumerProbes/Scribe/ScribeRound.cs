using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.ConsumerProbes.ScribeHost;
using SolusAgent.ApiOnlyConsumer;
using SolusAgent.ApiOnlyConsumer.Candidates;

namespace SolusAgent.ContractTests.ConsumerProbes.Scribe;

/// <summary>One scripted production step: the member its candidate covers and the fact text routed through the real tool.</summary>
internal sealed record ScribeProductionStep(string Member, string Fact);

/// <summary>
/// One composed fresh invocation: the Host's explicit Fresh choice, the startup adapter consuming that exact request,
/// the derived candidate request, the real runtime configuration startup and the existing scripted candidate producer.
/// </summary>
internal sealed class ScribeRound
{
    private readonly ScribeBusinessHost host;
    private readonly List<string> producedMembers = [];

    internal ScribeRound(ScribeBusinessHost host, IReadOnlyList<ScribeProductionStep> productions, bool captureUsage = true)
    {
        this.host = host;
        ContextRequest = host.CreateFreshRequest();
        Startup = new ScribeCandidateStartup(host.Control.CandidateBounds);
        CandidateRequest = Startup.Adopt(ContextRequest);
        Runtime = new ScribeRuntimeStartup(CandidateRequest.Execution, captureUsage);
        Agent = new ScriptedCandidateAgent(productions.Select(Produce).ToArray());
    }

    internal ContextExecutionRequest ContextRequest { get; }
    internal ScribeCandidateStartup Startup { get; }
    internal CandidateExecutionRequest CandidateRequest { get; }
    internal ScribeRuntimeStartup Runtime { get; }
    internal ScriptedCandidateAgent Agent { get; }

    /// <summary>Gets the members actually produced, proving which work the producer regenerated.</summary>
    internal IReadOnlyList<string> ProducedMembers => producedMembers.ToArray();

    internal async ValueTask<ObservedCandidateExecution> RunAsync(CancellationToken cancellationToken = default) =>
        await CandidateConsumer.RunAsync(Agent, CandidateRequest, host, cancellationToken);

    private Func<CandidateFeedback?, CancellationToken, ValueTask<string>> Produce(ScribeProductionStep step) =>
        async (_, cancellationToken) =>
        {
            producedMembers.Add(step.Member);
            return await Runtime.ProduceCandidateAsync(step.Member, step.Fact, cancellationToken);
        };
}

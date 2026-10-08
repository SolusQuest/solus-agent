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

    /// <summary>Gets the members whose candidates were actually produced successfully, proving which work the producer regenerated.</summary>
    internal IReadOnlyList<string> ProducedMembers => producedMembers.ToArray();

    internal async ValueTask<ObservedCandidateExecution> RunAsync(CancellationToken cancellationToken = default) =>
        await CandidateConsumer.RunAsync(Agent, CandidateRequest, host, cancellationToken);

    private Func<CandidateFeedback?, CancellationToken, ValueTask<string>> Produce(ScribeProductionStep step) =>
        async (previous, cancellationToken) =>
        {
            // A production following a rejection is a repair: its content must come from the Host correction
            // consumed as untrusted data, so the correction causally drives the repair. Missing or malformed
            // correction data never authorizes fabricating repair content.
            var fact = step.Fact;
            if (previous?.Decision == CandidateDecision.Reject)
            {
                if (!ScribeCandidateCorrection.TryParseRequestedFact(previous.CorrectionText, out var requestedFact))
                {
                    throw new InvalidOperationException("Repair production requires parseable Host correction data.");
                }

                fact = requestedFact;
            }

            var payload = await Runtime.ProduceCandidateAsync(step.Member, fact, cancellationToken);
            producedMembers.Add(step.Member);
            return payload;
        };
}

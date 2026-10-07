using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;

namespace SolusAgent.ConsumerProbes.ScribeHost;

/// <summary>Observed delivery classification for one Host exchange, independent of the domain decision.</summary>
public enum ScribeDelivery
{
    /// <summary>The correlated decision reached the agent.</summary>
    Delivered,

    /// <summary>The Host committed its exchange and returned no feedback.</summary>
    Missing,

    /// <summary>The delivery outcome is unknown to the agent; Host work may already have happened.</summary>
    Unknown,

    /// <summary>The feedback exchange failed without establishing a decision.</summary>
    Failed,

    /// <summary>Delivery is held until explicitly released, so cancelled observation sees unknown feedback.</summary>
    Held,
}

/// <summary>One scripted Host exchange entry; synthetic domain validation still precedes its decision.</summary>
public sealed class ScribeExchangePlan
{
    /// <summary>Creates one coherent scripted exchange entry.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An enumeration value is undefined.</exception>
    public ScribeExchangePlan(bool accepted, ScribeDelivery delivery = ScribeDelivery.Delivered, CandidateContinuation continuation = CandidateContinuation.Continue)
    {
        if (!Enum.IsDefined(delivery))
        {
            throw new ArgumentOutOfRangeException(nameof(delivery));
        }

        if (!Enum.IsDefined(continuation))
        {
            throw new ArgumentOutOfRangeException(nameof(continuation));
        }

        Accepted = accepted;
        Delivery = delivery;
        Continuation = continuation;
    }

    /// <summary>Gets the scripted domain decision applied only when validation passes.</summary>
    public bool Accepted { get; }

    /// <summary>Gets the scripted delivery classification.</summary>
    public ScribeDelivery Delivery { get; }

    /// <summary>Gets the Host execution instruction delivered only with an acknowledged decision.</summary>
    public CandidateContinuation Continuation { get; }
}

/// <summary>Closed exchange correlation record retaining neither candidate payload nor correction text.</summary>
public sealed record ScribeExchangeRecord(Guid ExecutionId, Guid SubmissionId, Guid? RepairsSubmissionId,
    ScribePayloadValidation Validation, bool Accepted, ScribeDelivery Delivery);

/// <summary>Current Host control inputs, separate from reconstructed business data and never derived from it.</summary>
public sealed class ScribeHostControl
{
    private const AgentCapability Supported =
        AgentCapability.WorkUnitLimit | AgentCapability.DurationLimit | AgentCapability.Cancellation
        | AgentCapability.UsageReporting | AgentCapability.DispatchLimits | AgentCapability.UsageThresholds;

    /// <summary>Creates current control with trusted instructions, requested bounds and required capabilities.</summary>
    /// <exception cref="ArgumentNullException">A bounds argument is null.</exception>
    /// <exception cref="ArgumentException">The instructions are blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Required capabilities contain undefined flags.</exception>
    public ScribeHostControl(string instructions, AgentExecutionBounds executionBounds, CandidateExecutionBounds candidateBounds,
        AgentCapability requiredCapabilities = AgentCapability.Cancellation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instructions);
        ArgumentNullException.ThrowIfNull(executionBounds);
        ArgumentNullException.ThrowIfNull(candidateBounds);
        if ((requiredCapabilities & ~Supported) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredCapabilities));
        }

        Instructions = instructions;
        ExecutionBounds = executionBounds;
        CandidateBounds = candidateBounds;
        RequiredCapabilities = requiredCapabilities;
    }

    /// <summary>Gets instructions from the Host's trusted source, never inferred from business data.</summary>
    public string Instructions { get; }

    /// <summary>Gets the Host-requested finite execution bounds.</summary>
    public AgentExecutionBounds ExecutionBounds { get; }

    /// <summary>Gets the finite candidate submission/repair/continuation limits.</summary>
    public CandidateExecutionBounds CandidateBounds { get; }

    /// <summary>Gets the guarantees the composed producers must support or reject before work.</summary>
    public AgentCapability RequiredCapabilities { get; }
}

/// <summary>Api-only synthetic business Host supplying current control, explicit fresh reconstruction and domain candidate acceptance.</summary>
/// <remarks>Business history retains only minimal validated facts and closed exchange metadata. Candidate payloads, correction text, contexts and provider material never enter business history or ordinary diagnostics. This probe has no checkpoint, campaign, storage, patch, publication or migration behavior and proves no product or M7 acceptance.</remarks>
public sealed class ScribeBusinessHost : ICandidateHost
{
    /// <summary>Canary embedded in Host correction text to prove correction confinement.</summary>
    public const string CorrectionCanary = "CORRECTION_CANARY restricted Host channel only";

    private readonly object gate = new();
    private readonly IReadOnlyList<ScribeExchangePlan> exchangePlan;
    private readonly List<ScribeExchangeRecord> records = [];
    private readonly List<string> externalEffectRequests = [];
    private readonly TaskCompletionSource heldDeliveryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource releaseHeldDelivery = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ScribeProgress progress;
    private int served;
    private Guid currentExecutionId;

    /// <summary>Creates the business Host over one manifest selection, initial validated progress and a scripted exchange plan.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The initial progress belongs to another manifest.</exception>
    public ScribeBusinessHost(ScribeManifest manifest, ScribeProgress initialProgress, ScribeHostControl control, IReadOnlyList<ScribeExchangePlan> exchangePlan)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(initialProgress);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(exchangePlan);
        if (!ReferenceEquals(initialProgress.Manifest, manifest))
        {
            throw new ArgumentException("Initial progress must belong to the supplied manifest.", nameof(initialProgress));
        }

        if (exchangePlan.Any(entry => entry is null))
        {
            throw new ArgumentException("The scripted exchange plan must be non-null.", nameof(exchangePlan));
        }

        Manifest = manifest;
        progress = initialProgress;
        Control = control;
        this.exchangePlan = Array.AsReadOnly(exchangePlan.ToArray());
    }

    /// <summary>Gets the manifest selection bounding this Host task.</summary>
    public ScribeManifest Manifest { get; }

    /// <summary>Gets current Host control; business data never modifies it.</summary>
    public ScribeHostControl Control { get; }

    /// <summary>Gets the current validated business progress, including facts committed before uncertain delivery.</summary>
    public ScribeProgress Progress { get { lock (gate) { return progress; } } }

    /// <summary>Gets the copied closed exchange records, retaining no payload or correction text.</summary>
    public IReadOnlyList<ScribeExchangeRecord> Exchanges { get { lock (gate) { return records.ToArray(); } } }

    /// <summary>Gets the number of requested external effects such as patches, storage or publication; no probe path requests any.</summary>
    public int ExternalEffects => externalEffectRequests.Count;

    /// <summary>Gets the execution identity of the latest fresh reconstruction.</summary>
    public Guid CurrentExecutionId { get { lock (gate) { return currentExecutionId; } } }

    /// <summary>Gets a task completed when a held exchange has committed and awaits release.</summary>
    public Task DeliveryHeld => heldDeliveryStarted.Task;

    /// <summary>Releases the held exchange so its late decision completes after cancelled observation.</summary>
    public void ReleaseHeldDelivery() => releaseHeldDelivery.TrySetResult();

    /// <summary>Explicitly reconstructs fresh input from the manifest and validated progress under current control.</summary>
    /// <remarks>The returned request is always the Fresh choice with a null envelope and a new execution identity. Accepted facts are reconstructed as untrusted data, never as regeneration assignments or instructions.</remarks>
    /// <returns>The explicit fresh context request consumed by the test startup.</returns>
    public ContextExecutionRequest CreateFreshRequest()
    {
        ScribeProgress snapshot;
        Guid executionId;
        lock (gate)
        {
            snapshot = progress;
            executionId = Guid.NewGuid();
            currentExecutionId = executionId;
        }

        var data = new List<AgentInput> { new(AgentInputSource.Repository, "selected members: " + string.Join(", ", Manifest.SelectedMembers)) };
        foreach (var fact in snapshot.AcceptedFacts)
        {
            data.Add(new AgentInput(AgentInputSource.Repository, $"accepted fact: {fact.Member} = {fact.Text}"));
        }

        foreach (var member in snapshot.UnresolvedMembers)
        {
            data.Add(new AgentInput(AgentInputSource.Repository, $"unresolved member: {member}"));
        }

        var request = new AgentRequest(executionId, Control.Instructions, data, Control.ExecutionBounds, Control.RequiredCapabilities);
        return new ContextExecutionRequest(request, ContextExecutionIntent.Fresh);
    }

    /// <summary>Validates and commits one submission before any delivery, then performs its scripted exchange.</summary>
    /// <remarks>Commit precedes delivery: missing, unknown, failed and held exchanges can leave accepted facts in Host progress even when the agent observed no acceptance. The hold ignores caller cancellation because Host work already happened, and late delivery is never replayed into the agent.</remarks>
    /// <param name="submission">Untrusted candidate data for Host-owned synthetic validation.</param>
    /// <param name="cancellationToken">Observation cancellation; this synthetic Host documents that its already-committed work does not stop.</param>
    /// <returns>Feedback to be checked for association, or null for missing feedback.</returns>
    /// <exception cref="ArgumentNullException">The submission is null.</exception>
    /// <exception cref="InvalidOperationException">The scripted exchange plan is exhausted, or the scripted delivery failed.</exception>
    public async ValueTask<CandidateFeedback?> SubmitAsync(CandidateSubmission submission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ScribeExchangePlan plan;
        lock (gate)
        {
            if (served == exchangePlan.Count)
            {
                throw new InvalidOperationException("The scripted exchange plan is exhausted.");
            }

            plan = exchangePlan[served++];
        }

        var validation = ScribeCandidatePayload.TryParse(submission.Payload, out var member, out var fact);
        bool accepted;
        lock (gate)
        {
            if (validation != ScribePayloadValidation.Valid)
            {
                accepted = false;
            }
            else
            {
                validation = !Manifest.Selects(member) ? ScribePayloadValidation.UnselectedMember
                    : progress.AcceptedFacts.Any(existing => existing.Member == member) ? ScribePayloadValidation.DuplicateMember
                    : ScribePayloadValidation.Valid;
                ScribeFact? acceptedFact = null;
                if (validation == ScribePayloadValidation.Valid)
                {
                    try
                    {
                        acceptedFact = new ScribeFact(member, fact);
                    }
                    catch (ArgumentException)
                    {
                        validation = ScribePayloadValidation.InvalidFact;
                    }
                }

                // Domain acceptance commits validated facts before any delivery, while rejection retains nothing.
                if (acceptedFact is not null && plan.Accepted)
                {
                    progress = progress.Accept(acceptedFact);
                    accepted = true;
                }
                else
                {
                    accepted = false;
                }
            }

            records.Add(new ScribeExchangeRecord(submission.ExecutionId, submission.SubmissionId, submission.RepairsSubmissionId,
                validation, accepted, plan.Delivery));
        }

        var correction = accepted ? null
            : $"synthetic rejection ({(validation == ScribePayloadValidation.Valid ? "domain-policy" : validation)}): {CorrectionCanary}";
        return plan.Delivery switch
        {
            ScribeDelivery.Missing => null,
            ScribeDelivery.Unknown => new CandidateFeedback(submission.ExecutionId, submission.SubmissionId, CandidateAcknowledgement.Unknown),
            ScribeDelivery.Failed => throw new InvalidOperationException("Synthetic Host delivery failure."),
            ScribeDelivery.Held => await HoldAsync(submission, accepted, plan, correction).ConfigureAwait(false),
            _ => Deliver(submission, accepted, plan, correction),
        };
    }

    /// <summary>Returns the type name without business data.</summary>
    public override string ToString() => nameof(ScribeBusinessHost);

    private async ValueTask<CandidateFeedback?> HoldAsync(CandidateSubmission submission, bool accepted, ScribeExchangePlan plan, string? correction)
    {
        heldDeliveryStarted.TrySetResult();
        await releaseHeldDelivery.Task.ConfigureAwait(false);
        return Deliver(submission, accepted, plan, correction);
    }

    private static CandidateFeedback Deliver(CandidateSubmission submission, bool accepted, ScribeExchangePlan plan, string? correction) =>
        new(submission.ExecutionId, submission.SubmissionId, CandidateAcknowledgement.Acknowledged,
            accepted ? CandidateDecision.Accept : CandidateDecision.Reject, plan.Continuation, correction);
}

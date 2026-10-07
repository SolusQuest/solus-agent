using AprHost;
using CustomTools;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.ConsumerProbes.CustomProvider;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;

namespace SolusAgent.ContractTests.ConsumerProbes.Apr;

/// <summary>
/// Finite test-only APR scenario implementation. One work unit is one completed synthetic review
/// production: a guarded provider exchange with a completed real tool round and its closing
/// exchange. Candidate execution delivers one minimized synthetic item per production. This class
/// is a deterministic in-memory probe, not a production runtime loop, budget engine, retry engine,
/// restoration codec or durable acknowledgement store.
/// </summary>
internal sealed class AprScenarioAgent : ICandidateAgent, IContextAgent
{
    /// <summary>Gets the fixed synthetic implementation discriminator of this scenario grammar.</summary>
    public static readonly Guid ImplementationId = new("2f4a7c9b-1d3e-4f6a-8b5c-7e9d0a2b4c6d");

    /// <summary>Gets the synthetic grammar format discriminator.</summary>
    public const int FormatVersion = 1;

    /// <summary>Gets the synthetic grammar compatibility discriminator.</summary>
    public const int CompatibilityVersion = 1;

    /// <summary>Gets the correction-data prefix consumed as untrusted data by finite production.</summary>
    public const string CorrectionPrefix = "apr-correction: expected-value=";

    private readonly SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration configuration;
    private readonly AprStartupOptions options;
    private readonly IToolCapability capability;
    private readonly object gate = new();
    private readonly Dictionary<Guid, ConfigurationConsumer> consumers = [];
    private readonly Dictionary<Guid, List<UsageAttemptObservation>> runAttempts = [];
    private readonly List<ConfigurationAttempt> recordedAttempts = [];
    private long retainedBase;
    private int completedUnits;
    private int nextItemIndex = 1;
    private IReadOnlyList<string> retainedNotes = [];

    public AprScenarioAgent(SolusAgent.Runtime.Api.Configuration.RuntimeConfiguration configuration, AprStartupOptions options)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        if (configuration.Tools.Count != 1)
        {
            throw new ArgumentException("The scenario startup must register exactly one tool binding.", nameof(configuration));
        }

        capability = configuration.Tools[0].Capability;
    }

    /// <summary>Gets the guarantees this finite scenario honors on every seam.</summary>
    public AgentCapability SupportedCapabilities =>
        AgentCapability.WorkUnitLimit | AgentCapability.Cancellation | AgentCapability.UsageReporting | AgentCapability.DispatchLimits;

    /// <summary>Gets every configuration attempt observed through the composed Runtime.Api seam.</summary>
    public IReadOnlyList<ConfigurationAttempt> RecordedAttempts
    {
        get
        {
            lock (gate)
            {
                return recordedAttempts.ToArray();
            }
        }
    }

    /// <summary>
    /// Runs one actual composed configuration attempt through the existing Runtime.Api consumer,
    /// guarded provider and Host hooks. Production uses this same seam; it exposes ordered exposure
    /// and same-attempt closure observations for their own focused checks.
    /// </summary>
    public ValueTask<ConfigurationAttempt> RunConfiguredAttemptAsync(AgentRequest request, ProviderAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(attempt);
        return RunAttemptAsync(ConsumerFor(request), ConsumerFor(request).CreateRequest(attempt), cancellationToken);
    }

    /// <summary>Executes finite ordinary production without candidate delivery or context behavior.</summary>
    public async ValueTask<AgentOutcome> ExecuteAsync(AgentRequest request, IProgress<AgentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var unsupported = request.RequiredCapabilities & ~SupportedCapabilities;
        if (unsupported != AgentCapability.None)
        {
            return new AgentOutcome(request.ExecutionId, AgentTerminationReason.UnsupportedCapability, 0, unsupported);
        }

        var completed = 0;
        while (true)
        {
            if (completed == options.TargetWorkUnits)
            {
                return Outcome(request.ExecutionId, AgentTerminationReason.Completed, completed);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Outcome(request.ExecutionId, AgentTerminationReason.Cancelled, completed);
            }

            if (completed >= request.Bounds.MaximumWorkUnits)
            {
                return Outcome(request.ExecutionId, AgentTerminationReason.ResourceLimit, completed);
            }

            try
            {
                await ProduceAsync(request, null, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Outcome(request.ExecutionId, AgentTerminationReason.Cancelled, completed);
            }
            catch (Exception)
            {
                return Outcome(request.ExecutionId, AgentTerminationReason.Failed, completed, AgentFailureCode.ExecutionFailed);
            }

            completed++;
            try
            {
                progress?.Report(new AgentProgress(request.ExecutionId, completed, UsageSnapshot(request.ExecutionId)));
            }
            catch (Exception)
            {
                return Outcome(request.ExecutionId, AgentTerminationReason.Failed, completed, AgentFailureCode.ProgressObserverFailed);
            }
        }
    }

    /// <summary>
    /// Executes the separate context seam with the implementation-local synthetic prior-context
    /// grammar. Admission, grammar and transition checks run before any provider, tool, capture or
    /// progress effect; rejected supplied state returns no work outcome and never falls back fresh.
    /// Admitted runs perform actual provider/tool work and transfer one safe-point restricted state
    /// snapshot to the call-scoped sink when one was supplied.
    /// </summary>
    public async ValueTask<ContextExecutionResult> ExecuteWithContextAsync(ContextExecutionRequest contextRequest,
        IRestrictedContextSink? contextSink = null, IProgress<AgentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contextRequest);
        var request = contextRequest.Request;
        var intent = contextRequest.Intent;
        var unavailable = contextSink is null ? ContextCaptureStatus.NotRequested : ContextCaptureStatus.Unavailable;
        var unsupported = request.RequiredCapabilities & ~SupportedCapabilities;
        if (unsupported != AgentCapability.None)
        {
            return new ContextExecutionResult(request.ExecutionId, intent, ContextAdmission.NotAttempted,
                new AgentOutcome(request.ExecutionId, AgentTerminationReason.UnsupportedCapability, 0, unsupported), captureStatus: unavailable);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new ContextExecutionResult(request.ExecutionId, intent, ContextAdmission.NotAttempted,
                new AgentOutcome(request.ExecutionId, AgentTerminationReason.Cancelled, 0), captureStatus: unavailable);
        }

        AprContextState? supplied = null;
        if (contextRequest.Context is { } envelope)
        {
            if (envelope.ImplementationId != ImplementationId)
            {
                return Reject(ContextRejectionCode.ImplementationMismatch);
            }

            if (envelope.FormatVersion != FormatVersion)
            {
                return Reject(ContextRejectionCode.UnsupportedFormat);
            }

            if (envelope.CompatibilityVersion != CompatibilityVersion)
            {
                return Reject(ContextRejectionCode.IncompatibleContext);
            }

            supplied = AprContextState.TryParse(envelope.CopyRestrictedPayload());
            if (supplied is null)
            {
                return Reject(ContextRejectionCode.InvalidContext);
            }

            if (!AdmitsTransition(intent, supplied, request))
            {
                return Reject(ContextRejectionCode.InvalidRunTransition);
            }
        }

        lock (gate)
        {
            retainedBase = supplied?.Total ?? 0;
            retainedNotes = supplied?.Notes ?? [];
            completedUnits = intent == ContextExecutionIntent.ContinueRun ? supplied!.Units : 0;
            nextItemIndex = completedUnits + 1;
        }

        while (true)
        {
            if (completedUnits == options.TargetWorkUnits)
            {
                return await FinishAsync(AgentTerminationReason.Completed, completedUnits).ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return await FinishAsync(AgentTerminationReason.Cancelled, completedUnits).ConfigureAwait(false);
            }

            if (completedUnits >= request.Bounds.MaximumWorkUnits)
            {
                return await FinishAsync(AgentTerminationReason.ResourceLimit, completedUnits).ConfigureAwait(false);
            }

            if (options.PartialAfterWorkUnits is int partial && completedUnits >= partial)
            {
                return await FinishAsync(AgentTerminationReason.Partial, completedUnits).ConfigureAwait(false);
            }

            try
            {
                await ProduceAsync(request, null, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return await FinishAsync(AgentTerminationReason.Cancelled, completedUnits).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return await FinishAsync(AgentTerminationReason.Failed, completedUnits, AgentFailureCode.ExecutionFailed).ConfigureAwait(false);
            }

            completedUnits++;
            try
            {
                progress?.Report(new AgentProgress(request.ExecutionId, completedUnits, UsageSnapshot(request.ExecutionId)));
            }
            catch (Exception)
            {
                return await FinishAsync(AgentTerminationReason.Failed, completedUnits, AgentFailureCode.ProgressObserverFailed).ConfigureAwait(false);
            }
        }

        ContextExecutionResult Reject(ContextRejectionCode code) =>
            new(request.ExecutionId, intent, ContextAdmission.Rejected, rejectionCode: code, captureStatus: unavailable);

        async ValueTask<ContextExecutionResult> FinishAsync(AgentTerminationReason reason, int workUnits,
            AgentFailureCode failure = AgentFailureCode.None)
        {
            var outcome = new AgentOutcome(request.ExecutionId, reason, workUnits, failureCode: failure,
                usage: UsageSnapshot(request.ExecutionId));
            var capture = ContextCaptureStatus.NotRequested;
            if (contextSink is not null)
            {
                try
                {
                    contextSink.Capture(CurrentState(request.ExecutionId).ToEnvelope(ImplementationId));
                    capture = ContextCaptureStatus.Delivered;
                }
                catch (Exception)
                {
                    capture = ContextCaptureStatus.Failed;
                }
            }

            return new ContextExecutionResult(request.ExecutionId, intent,
                intent == ContextExecutionIntent.Fresh ? ContextAdmission.Fresh : ContextAdmission.Supplied,
                outcome, captureStatus: capture);
        }

        bool AdmitsTransition(ContextExecutionIntent selected, AprContextState state, AgentRequest current) => selected switch
        {
            // A supplied new run needs a distinct toy correlation and keeps prior state as data.
            ContextExecutionIntent.NewRunFromContext => state.OriginExecutionId != current.ExecutionId,
            // An unfinished continuation needs the same origin, an unfinished snapshot and the current goal.
            ContextExecutionIntent.ContinueRun => state.OriginExecutionId == current.ExecutionId
                && state.Units != state.Goal && state.Goal == options.TargetWorkUnits,
            _ => false,
        };
    }

    /// <summary>
    /// Executes bounded candidate exchange over the supplied Host channel. Each production is real
    /// provider/tool work; each correction is consumed as untrusted data; repair association uses
    /// the actual <see cref="CandidateSubmission.RepairsSubmissionId"/> member. Acknowledged
    /// receipts survive later stops and no uncertain exchange is replayed.
    /// </summary>
    public async ValueTask<CandidateExecutionResult> ExecuteCandidatesAsync(CandidateExecutionRequest request,
        ICandidateHost host, IProgress<AgentProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);
        var execution = request.Execution;
        var unsupported = execution.RequiredCapabilities & ~SupportedCapabilities;
        var receipts = new List<CandidateReceipt>();
        var completed = 0;
        var repairs = 0;
        var continuations = 0;
        CandidateFeedback? previous = null;
        if (unsupported != AgentCapability.None)
        {
            return Stop(CandidateStopReason.UnsupportedCapability, unsupported);
        }

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Stop(CandidateStopReason.Cancelled);
            }

            if (receipts.Count == request.Bounds.MaximumSubmissions)
            {
                return Stop(CandidateStopReason.SubmissionLimit);
            }

            if (completed == execution.Bounds.MaximumWorkUnits)
            {
                return Stop(CandidateStopReason.WorkUnitLimit);
            }

            if (previous is not null)
            {
                if (previous.Decision == CandidateDecision.Reject)
                {
                    if (repairs == request.Bounds.MaximumRepairs)
                    {
                        return Stop(CandidateStopReason.RepairLimit);
                    }

                    repairs++;
                }
                else
                {
                    if (continuations == request.Bounds.MaximumContinuations)
                    {
                        return Stop(CandidateStopReason.ContinuationLimit);
                    }

                    continuations++;
                }
            }

            AprItem item;
            try
            {
                item = (await ProduceAsync(execution, previous?.CorrectionText, cancellationToken).ConfigureAwait(false)).Item;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Stop(CandidateStopReason.Cancelled);
            }
            catch (Exception)
            {
                return Stop(CandidateStopReason.ProductionFailed);
            }

            completed++;
            try
            {
                progress?.Report(new AgentProgress(execution.ExecutionId, completed, UsageSnapshot(execution.ExecutionId)));
            }
            catch (Exception)
            {
                return Stop(CandidateStopReason.ProgressObserverFailed);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Stop(CandidateStopReason.Cancelled);
            }

            var submission = new CandidateSubmission(execution.ExecutionId, Guid.NewGuid(), item.ToPayload(),
                previous?.Decision == CandidateDecision.Reject ? previous.SubmissionId : null);

            CandidateFeedback? feedback;
            try
            {
                // Cancel observation even if a Host ignores the token; its possible effects are never replayed.
                feedback = await host.SubmitAsync(submission, cancellationToken).AsTask().WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Observe(CandidateAcknowledgement.Unknown);
                return Stop(CandidateStopReason.Cancelled);
            }
            catch (Exception)
            {
                Observe(CandidateAcknowledgement.Failed);
                return Stop(CandidateStopReason.FailedAcknowledgement);
            }

            if (feedback is null)
            {
                Observe(CandidateAcknowledgement.Missing);
                return Stop(CandidateStopReason.MissingAcknowledgement);
            }

            if (feedback.ExecutionId != execution.ExecutionId)
            {
                Observe(CandidateAcknowledgement.Mismatched);
                return Stop(CandidateStopReason.MismatchedFeedback);
            }

            if (feedback.SubmissionId != submission.SubmissionId)
            {
                var duplicate = receipts.Any(receipt => receipt.SubmissionId == feedback.SubmissionId);
                Observe(duplicate ? CandidateAcknowledgement.Duplicate : CandidateAcknowledgement.Mismatched);
                return Stop(duplicate ? CandidateStopReason.DuplicateFeedback : CandidateStopReason.MismatchedFeedback);
            }

            if (feedback.Acknowledgement != CandidateAcknowledgement.Acknowledged)
            {
                Observe(feedback.Acknowledgement);
                return Stop(feedback.Acknowledgement == CandidateAcknowledgement.Failed
                    ? CandidateStopReason.FailedAcknowledgement : CandidateStopReason.UnknownAcknowledgement);
            }

            Observe(CandidateAcknowledgement.Acknowledged, feedback);
            // The explicit finite goal completes independently of Host acceptance or product effects.
            if (completed == options.TargetProductions && feedback.Decision == CandidateDecision.Accept)
            {
                return Stop(CandidateStopReason.Completed);
            }

            if (feedback.Continuation == CandidateContinuation.End)
            {
                return Stop(CandidateStopReason.HostEnded);
            }

            if (completed == options.TargetProductions)
            {
                return Stop(CandidateStopReason.ProductionExhausted);
            }

            previous = feedback;

            void Observe(CandidateAcknowledgement acknowledgement, CandidateFeedback? acknowledged = null) =>
                receipts.Add(new CandidateReceipt(execution.ExecutionId, submission.SubmissionId, acknowledgement,
                    acknowledged?.Decision, acknowledged?.Continuation));
        }

        CandidateExecutionResult Stop(CandidateStopReason stop, AgentCapability rejected = AgentCapability.None)
        {
            var reason = stop switch
            {
                CandidateStopReason.Completed => AgentTerminationReason.Completed,
                CandidateStopReason.HostEnded or CandidateStopReason.ProductionExhausted
                    or CandidateStopReason.MissingAcknowledgement or CandidateStopReason.UnknownAcknowledgement => AgentTerminationReason.Partial,
                CandidateStopReason.SubmissionLimit or CandidateStopReason.WorkUnitLimit
                    or CandidateStopReason.RepairLimit or CandidateStopReason.ContinuationLimit => AgentTerminationReason.ResourceLimit,
                CandidateStopReason.Cancelled => AgentTerminationReason.Cancelled,
                CandidateStopReason.UnsupportedCapability => AgentTerminationReason.UnsupportedCapability,
                _ => AgentTerminationReason.Failed,
            };
            var failure = reason == AgentTerminationReason.Failed
                ? stop == CandidateStopReason.ProgressObserverFailed ? AgentFailureCode.ProgressObserverFailed : AgentFailureCode.ExecutionFailed
                : AgentFailureCode.None;
            var usage = reason == AgentTerminationReason.UnsupportedCapability && HasAttempts(execution.ExecutionId)
                ? null : UsageSnapshot(execution.ExecutionId);
            var outcome = new AgentOutcome(execution.ExecutionId, reason, completed,
                reason == AgentTerminationReason.UnsupportedCapability ? rejected : AgentCapability.None, failure, usage);
            return new CandidateExecutionResult(outcome, stop, receipts, repairs, continuations);
        }
    }

    /// <summary>
    /// One finite production: a guarded provider exchange carrying Host instruction and classified
    /// data, complete real tool-batch preparation and concrete capability admission before any
    /// invocation, one invocation per accepted call, and the closing exchange with the accepted
    /// model turn and guarded tool results. The produced synthetic item value derives from actual
    /// guarded tool output and admitted prior state.
    /// </summary>
    private async ValueTask<AprProduction> ProduceAsync(AgentRequest request, string? correctionText, CancellationToken cancellationToken)
    {
        var consumer = ConsumerFor(request);
        var stepAmount = ResolveStepAmount(correctionText);
        var attempt = new ProviderAttempt(request.ExecutionId, Guid.NewGuid(), Guid.NewGuid());
        var exchange = await RunAttemptAsync(consumer, configuration.CreateRequest(attempt, BuildInputs(request, correctionText, stepAmount)),
            cancellationToken).ConfigureAwait(false);
        if (exchange.AdmissionStop != RuntimeStop.None)
        {
            throw new AprProductionException();
        }

        if (exchange.Provider?.Outcome != ProviderOutcome.Succeeded || exchange.Provider.Response is not { } first)
        {
            throw new AprProductionException();
        }

        // Every requested batch member is prepared and admitted with its concrete narrow capability
        // before any invocation; a rejected member leaves the whole batch uninvoked.
        var prepared = AdmitBatch(first.Calls);
        var results = new List<ToolResult>();
        long toolTotal = 0;
        foreach (var member in prepared)
        {
            var result = await member.Tool.InvokeAsync(member.Prepared, member.Call, member.Capability, cancellationToken).ConfigureAwait(false);
            if (result.Outcome != ToolOutcome.Succeeded || result.Json is null || !result.Call.Matches(member.Call))
            {
                throw new AprProductionException();
            }

            results.Add(result);
            toolTotal = ReadTotal(result.Json);
        }

        var closing = new ProviderAttempt(request.ExecutionId, Guid.NewGuid(), Guid.NewGuid());
        var closingInputs = BuildInputs(request, correctionText, stepAmount);
        closingInputs.Add(ProviderInput.FromModel(first));
        foreach (var result in results)
        {
            closingInputs.Add(ProviderInput.FromTool(result));
        }

        var closed = await RunAttemptAsync(consumer, configuration.CreateRequest(closing, closingInputs), cancellationToken).ConfigureAwait(false);
        if (closed.AdmissionStop != RuntimeStop.None || closed.Provider?.Outcome != ProviderOutcome.Succeeded
            || closed.Provider.Response is not { Finish: ProviderFinish.Final })
        {
            throw new AprProductionException();
        }

        return new AprProduction(NextItem(retainedBase + toolTotal), toolTotal);
    }

    /// <summary>Runs one configuration attempt through the existing consumer and records its honest observations.</summary>
    private async ValueTask<ConfigurationAttempt> RunAttemptAsync(ConfigurationConsumer consumer, ProviderRequest request,
        CancellationToken cancellationToken)
    {
        var attempt = await consumer.RunAsync(request, cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            recordedAttempts.Add(attempt);
            if (!runAttempts.TryGetValue(attempt.Observation.ExecutionId, out var attempts))
            {
                runAttempts[attempt.Observation.ExecutionId] = attempts = [];
            }

            attempts.Add(attempt.Observation);
        }

        return attempt;
    }

    /// <summary>Admits a complete requested tool batch before any effect is possible.</summary>
    private IReadOnlyList<PreparedBatchMember> AdmitBatch(IReadOnlyList<ToolCall> calls)
    {
        var prepared = new List<PreparedBatchMember>();
        foreach (var call in calls)
        {
            var binding = configuration.Tools.SingleOrDefault(candidate => candidate.Descriptor.Name == call.ToolName);
            if (binding is null || binding.Tool is not CounterTool tool || binding.Capability is not CounterCapability admitted
                || admitted.CapabilityId != binding.Descriptor.CapabilityId)
            {
                throw new AprProductionException();
            }

            var preparation = tool.Prepare(call);
            if (!preparation.Accepted || preparation.Prepared is null)
            {
                throw new AprProductionException();
            }

            prepared.Add(new PreparedBatchMember(tool, call, preparation.Prepared, admitted));
        }

        return prepared;
    }

    private List<ProviderInput> BuildInputs(AgentRequest request, string? correctionText, int stepAmount)
    {
        var inputs = new List<ProviderInput> { ProviderInput.Instruction(request.Instructions) };
        foreach (var data in request.Data)
        {
            inputs.Add(ProviderInput.Data(data.Text));
        }

        lock (gate)
        {
            inputs.Add(ProviderInput.Data($"apr-state: total={retainedBase + CounterTotal}"));
        }

        inputs.Add(ProviderInput.Data($"{AprProviderScript.StepPrefix}{stepAmount}"));
        if (correctionText is not null)
        {
            inputs.Add(ProviderInput.Data(correctionText));
        }

        return inputs;
    }

    /// <summary>Consumes a bounded Host correction as untrusted data to compute the next synthetic step.</summary>
    private int ResolveStepAmount(string? correctionText)
    {
        long current;
        lock (gate)
        {
            current = retainedBase + CounterTotal;
        }

        if (correctionText is not null && correctionText.StartsWith(CorrectionPrefix, StringComparison.Ordinal)
            && long.TryParse(correctionText[CorrectionPrefix.Length..], out var expected))
        {
            var delta = expected - current;
            if (delta is >= 0 and <= 100)
            {
                return (int)delta;
            }
        }

        return options.StepAmount;
    }

    private AprItem NextItem(long value)
    {
        lock (gate)
        {
            return new AprItem($"item-{nextItemIndex++}", value);
        }
    }

    private AprContextState CurrentState(Guid executionId)
    {
        lock (gate)
        {
            return new AprContextState(executionId, completedUnits, options.TargetWorkUnits, retainedBase + CounterTotal, retainedNotes);
        }
    }

    private long CounterTotal => capability is CounterCapability counter ? counter.Total : 0;

    private AgentOutcome Outcome(Guid executionId, AgentTerminationReason reason, int workUnits,
        AgentFailureCode failure = AgentFailureCode.None) =>
        new(executionId, reason, workUnits, failureCode: failure, usage: UsageSnapshot(executionId));

    private ConfigurationConsumer ConsumerFor(AgentRequest request)
    {
        lock (gate)
        {
            if (!consumers.TryGetValue(request.ExecutionId, out var consumer))
            {
                consumers[request.ExecutionId] = consumer = new ConfigurationConsumer(configuration, request);
            }

            return consumer;
        }
    }

    private AgentRunUsage UsageSnapshot(Guid executionId)
    {
        lock (gate)
        {
            return new AgentRunUsage(executionId, UsageInventoryCoverage.Complete,
                runAttempts.TryGetValue(executionId, out var attempts) ? attempts.ToArray() : []);
        }
    }

    private bool HasAttempts(Guid executionId)
    {
        lock (gate)
        {
            return runAttempts.TryGetValue(executionId, out var attempts) && attempts.Count != 0;
        }
    }

    private static long ReadTotal(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.GetProperty("total").GetInt64();
    }

    private sealed record AprProduction(AprItem Item, long ToolTotal);

    private sealed record PreparedBatchMember(CounterTool Tool, ToolCall Call, PreparedToolInvocation Prepared, CounterCapability Capability);

    /// <summary>Fixed content-free production failure; it carries no provider, tool or payload detail.</summary>
    private sealed class AprProductionException : Exception;
}

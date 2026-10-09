using SolusAgent.Api.Usage;
using SolusAgent.Tools.Api;

namespace SolusAgent.Runtime.Api.Providers;

/// <summary>Accepted or content-free unsuccessful exchange outcomes.</summary>
public enum ProviderOutcome
{
    /// <summary>Associated bounded response accepted.</summary>
    Succeeded,
    /// <summary>Admission or candidate response rejected.</summary>
    Rejected,
    /// <summary>Requested cancellation observed before acceptance.</summary>
    Cancelled,
    /// <summary>Provider failed.</summary>
    Failed,
}

/// <summary>Ordinary diagnostics containing only correlations and closed numeric/classification metadata.</summary>
public sealed record ProviderDiagnostic(Guid ExecutionId, Guid LogicalCallId, Guid PhysicalAttemptId, int AttemptNumber,
    ProviderOutcome Outcome, ProviderError Error, DispatchExposure Exposure, UsageCompleteness UsageCompleteness, int AcceptedToolCalls,
    ProviderRetryKind? RetryKind = null, TimeSpan? RetryAfter = null);

/// <summary>Exchange result retaining usage independently of payload acceptance.</summary>
public sealed class ProviderExchangeResult
{
    internal ProviderExchangeResult(ProviderOutcome outcome, ProviderError error, UsageAttemptObservation observation, ProviderResponse? response = null,
        ProviderRetry? retry = null)
    {
        ProviderBoundary.Require(Enum.IsDefined(outcome) && Enum.IsDefined(error)
            && (outcome == ProviderOutcome.Succeeded ? error == ProviderError.None && response is { Accepted: true }
                : error != ProviderError.None && response is null)
            && (outcome != ProviderOutcome.Failed || error == ProviderError.ProviderFailed)
            && (outcome != ProviderOutcome.Cancelled || error == ProviderError.Cancelled)
            && (retry is null || outcome == ProviderOutcome.Failed && error == ProviderError.ProviderFailed), ProviderError.InvalidResponse);
        Outcome = outcome; Error = error; Observation = observation; Response = response; Retry = retry;
        Diagnostic = new(observation.ExecutionId, observation.LogicalCallId, observation.PhysicalAttemptId, observation.AttemptNumber,
            outcome, error, observation.Exposure, observation.Usage.Completeness, response?.Calls.Count ?? 0, retry?.Kind, retry?.RetryAfter);
    }
    /// <summary>Gets the normalized outcome.</summary>
    public ProviderOutcome Outcome { get; }
    /// <summary>Gets content-free error classification.</summary>
    public ProviderError Error { get; }
    /// <summary>Gets captured usage/exposure even after later failure.</summary>
    public UsageAttemptObservation Observation { get; }
    /// <summary>Gets restricted accepted data only on success.</summary>
    public ProviderResponse? Response { get; }
    /// <summary>Gets positive classified failure evidence, never automatic retry or replay authority.</summary>
    public ProviderRetry? Retry { get; }
    /// <summary>Gets the explicitly ordinary diagnostic surface.</summary>
    public ProviderDiagnostic Diagnostic { get; }
    /// <summary>Validates forwarded observation association and accepted payload against the actual consumer request.</summary>
    public void ValidateFor(ProviderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var attempt = request.Attempt;
        ProviderBoundary.Require(Observation.ExecutionId == attempt.ExecutionId && Observation.LogicalCallId == attempt.LogicalCallId
            && Observation.PhysicalAttemptId == attempt.PhysicalAttemptId && Observation.AttemptNumber == attempt.AttemptNumber,
            ProviderError.InvalidAssociation);
        Response?.ValidateFor(request);
    }
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(ProviderExchangeResult);
}

/// <summary>Per-exchange capture closed on every return/throw; never carries text or replay material.</summary>
public sealed class ProviderObservation
{
    private readonly object gate = new();
    private DispatchExposure exposure = DispatchExposure.Unknown;
    private UsageObservation usage = new();
    private UsageAccounting? accounting;
    private bool captured;
    private bool closed;
    private bool started;
    private UsageAttemptObservation? sealedObservation;
    internal ProviderObservation(ProviderAttempt attempt) => Attempt = attempt;
    /// <summary>Gets immutable request-owned correlation.</summary>
    public ProviderAttempt Attempt { get; }
    /// <summary>Gets whether a valid usage/accounting capture was made, including an explicitly unavailable capture.</summary>
    public bool HasCapturedUsage { get { lock (gate) return captured; } }
    /// <summary>Reports dispatch knowledge without regressing known dispatch or altering captured evidence.</summary>
    public void ObserveDispatch(DispatchExposure value)
    {
        lock (gate)
        {
            Open();
            ProviderBoundary.Require(Enum.IsDefined(value) && (!captured || value == exposure)
                && (exposure != DispatchExposure.Dispatched || value == exposure)
                && (exposure != DispatchExposure.NotDispatched || value != DispatchExposure.Unknown), ProviderError.ObservationConflict);
            exposure = value;
        }
    }
    /// <summary>Captures normalized usage once before payload acceptance; contradictions retain prior evidence.</summary>
    public void CaptureUsage(UsageObservation value, UsageAccounting? claims = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (gate)
        {
            Open();
            ProviderBoundary.Require(!captured, ProviderError.ObservationConflict);
            try { _ = Attempt.Observe(exposure, value, claims); }
            catch (ArgumentException) { throw new ProviderContractException(ProviderError.ObservationConflict); }
            usage = value; accounting = claims; captured = true;
        }
    }
    /// <summary>Returns a point-in-time immutable snapshot without closing the channel.</summary>
    public UsageAttemptObservation Snapshot()
    { lock (gate) return sealedObservation ?? Attempt.Observe(exposure, usage, accounting); }
    /// <summary>Atomically freezes all preceding facts and rejects later writers. Repeated sealing returns the same snapshot.</summary>
    public UsageAttemptObservation Seal()
    {
        lock (gate)
        {
            closed = true;
            return sealedObservation ??= Attempt.Observe(exposure, usage, accounting);
        }
    }
    internal bool TryBegin()
    { lock (gate) { if (started || closed) return false; started = true; return true; } }
    private void Open() => ProviderBoundary.Require(!closed, ProviderError.ObservationClosed);
    /// <summary>Returns only the type name.</summary>
    public override string ToString() => nameof(ProviderObservation);
}

/// <summary>Self-owned provider seam; implementers must preserve classified inputs and observation semantics.</summary>
public interface IModelProvider
{
    /// <summary>Gets host-installed scope without selecting transport.</summary>
    ProviderScope Scope { get; }
    /// <summary>Gets complete supported draft semantics.</summary>
    ProviderCapabilities Capabilities { get; }
    /// <summary>Exchanges one bounded attempt; cancellation never promises remote stop.</summary>
    ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Guarded provider extension, not a transport, scheduler, loop or hostile-code sandbox.</summary>
public abstract class ModelProvider : IModelProvider
{
    /// <summary>Installs an exact host scope and supported capabilities.</summary>
    protected ModelProvider(ProviderScope scope, ProviderCapabilities capabilities)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        ProviderRequest.ValidateCapabilities(capabilities); Capabilities = capabilities;
    }
    /// <inheritdoc />
    public ProviderScope Scope { get; }
    /// <inheritdoc />
    public ProviderCapabilities Capabilities { get; }
    /// <inheritdoc />
    public async ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var capture = request.Observation;
        if (!capture.TryBegin()) return new(ProviderOutcome.Rejected, ProviderError.ObservationClosed, capture.Seal());
        if (!Scope.Matches(request.Scope)) return BeforeCore(ProviderOutcome.Rejected, ProviderError.InvalidAssociation);
        if ((request.RequiredCapabilities & ~Capabilities) != 0) return BeforeCore(ProviderOutcome.Rejected, ProviderError.UnsupportedCapability);
        if (cancellationToken.IsCancellationRequested) return BeforeCore(ProviderOutcome.Cancelled, ProviderError.Cancelled);
        ProviderResponse? candidate = null;
        UsageAttemptObservation observation;
        var outcome = ProviderOutcome.Succeeded;
        var error = ProviderError.None;
        ProviderRetry? retry = null;
        try { candidate = await ExchangeCoreAsync(request, capture, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested && exception.CancellationToken == cancellationToken)
        { outcome = ProviderOutcome.Cancelled; error = ProviderError.Cancelled; }
        catch (ProviderContractException exception) { outcome = ProviderOutcome.Rejected; error = exception.Error; }
        catch (ToolContractException) { outcome = ProviderOutcome.Rejected; error = ProviderError.InvalidResponse; }
        catch (ProviderFailureException exception)
        { outcome = ProviderOutcome.Failed; error = ProviderError.ProviderFailed; retry = exception.Retry; }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        { outcome = ProviderOutcome.Failed; error = ProviderError.ProviderFailed; }
        finally { observation = capture.Seal(); }
        if (error != ProviderError.None) return new(outcome, error, observation, retry: cancellationToken.IsCancellationRequested ? null : retry);
        if (cancellationToken.IsCancellationRequested) return new(ProviderOutcome.Cancelled, ProviderError.Cancelled, observation);
        try
        {
            ProviderBoundary.Require(candidate is not null, ProviderError.InvalidResponse);
            candidate!.ValidateFor(request);
        }
        catch (ProviderContractException exception) { return new(ProviderOutcome.Rejected, exception.Error, observation); }
        // This final check is the acceptance cut; later cancellation cannot rewrite this immutable result.
        if (cancellationToken.IsCancellationRequested) return new(ProviderOutcome.Cancelled, ProviderError.Cancelled, observation);
        return new(ProviderOutcome.Succeeded, ProviderError.None, observation, candidate.Accept());

        ProviderExchangeResult BeforeCore(ProviderOutcome result, ProviderError failure)
        {
            try { capture.ObserveDispatch(DispatchExposure.NotDispatched); }
            // A forwarding extension may already have supplied valid facts. Pre-core rejection cannot erase them.
            catch (ProviderContractException) { }
            return new(result, failure, capture.Seal());
        }
    }
    /// <summary>Report known dispatch and normalized usage before constructing/validating output; bound own allocations and pass cancellation to effects.</summary>
    protected abstract ValueTask<ProviderResponse> ExchangeCoreAsync(ProviderRequest request, ProviderObservation observation, CancellationToken cancellationToken);
}

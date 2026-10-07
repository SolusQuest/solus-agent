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
    ProviderOutcome Outcome, ProviderError Error, DispatchExposure Exposure, UsageCompleteness UsageCompleteness, int AcceptedToolCalls);

/// <summary>Exchange result retaining usage independently of payload acceptance.</summary>
public sealed class ProviderExchangeResult
{
    internal ProviderExchangeResult(ProviderOutcome outcome, ProviderError error, UsageAttemptObservation observation, ProviderResponse? response = null)
    {
        Outcome = outcome; Error = error; Observation = observation; Response = response;
        Diagnostic = new(observation.ExecutionId, observation.LogicalCallId, observation.PhysicalAttemptId, observation.AttemptNumber,
            outcome, error, observation.Exposure, observation.Usage.Completeness, response?.Calls.Count ?? 0);
    }
    /// <summary>Gets the normalized outcome.</summary>
    public ProviderOutcome Outcome { get; }
    /// <summary>Gets content-free error classification.</summary>
    public ProviderError Error { get; }
    /// <summary>Gets captured usage/exposure even after later failure.</summary>
    public UsageAttemptObservation Observation { get; }
    /// <summary>Gets restricted accepted data only on success.</summary>
    public ProviderResponse? Response { get; }
    /// <summary>Gets the explicitly ordinary diagnostic surface.</summary>
    public ProviderDiagnostic Diagnostic { get; }
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
    internal ProviderObservation(ProviderAttempt attempt) => Attempt = attempt;
    /// <summary>Gets immutable request-owned correlation.</summary>
    public ProviderAttempt Attempt { get; }
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
    internal UsageAttemptObservation Close()
    { lock (gate) { closed = true; return Attempt.Observe(exposure, usage, accounting); } }
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
        if (!Scope.Matches(request.Scope)) return BeforeCore(ProviderOutcome.Rejected, ProviderError.InvalidAssociation);
        if ((request.RequiredCapabilities & ~Capabilities) != 0) return BeforeCore(ProviderOutcome.Rejected, ProviderError.UnsupportedCapability);
        if (cancellationToken.IsCancellationRequested) return BeforeCore(ProviderOutcome.Cancelled, ProviderError.Cancelled);
        var capture = new ProviderObservation(request.Attempt);
        ProviderResponse? candidate = null;
        UsageAttemptObservation observation;
        var outcome = ProviderOutcome.Succeeded;
        var error = ProviderError.None;
        try { candidate = await ExchangeCoreAsync(request, capture, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested && exception.CancellationToken == cancellationToken)
        { outcome = ProviderOutcome.Cancelled; error = ProviderError.Cancelled; }
        catch (ProviderContractException exception) { outcome = ProviderOutcome.Rejected; error = exception.Error; }
        catch (ToolContractException) { outcome = ProviderOutcome.Rejected; error = ProviderError.InvalidResponse; }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        { outcome = ProviderOutcome.Failed; error = ProviderError.ProviderFailed; }
        finally { observation = capture.Close(); }
        if (error != ProviderError.None) return new(outcome, error, observation);
        if (cancellationToken.IsCancellationRequested) return new(ProviderOutcome.Cancelled, ProviderError.Cancelled, observation);
        try
        {
            ProviderBoundary.Require(candidate is not null, ProviderError.InvalidResponse);
            candidate!.Validate(request);
        }
        catch (ProviderContractException exception) { return new(ProviderOutcome.Rejected, exception.Error, observation); }
        // This final check is the acceptance cut; later cancellation cannot rewrite this immutable result.
        if (cancellationToken.IsCancellationRequested) return new(ProviderOutcome.Cancelled, ProviderError.Cancelled, observation);
        return new(ProviderOutcome.Succeeded, ProviderError.None, observation, candidate.Accept());

        ProviderExchangeResult BeforeCore(ProviderOutcome result, ProviderError failure) =>
            new(result, failure, request.Attempt.Observe(DispatchExposure.NotDispatched, new()));
    }
    /// <summary>Report known dispatch and normalized usage before constructing/validating output; bound own allocations and pass cancellation to effects.</summary>
    protected abstract ValueTask<ProviderResponse> ExchangeCoreAsync(ProviderRequest request, ProviderObservation observation, CancellationToken cancellationToken);
}

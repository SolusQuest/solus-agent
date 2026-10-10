using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;

namespace SolusAgent.Runtime.Execution;

internal sealed record AttemptExecution(ProviderResponse? Response, RuntimeStop Stop, RuntimeStop SettlementStop,
    ProviderOutcome? ProviderOutcome, ProviderError? ProviderError, ProviderRetry? Retry);

// The reusable turn boundary. Tool/candidate handlers consume accepted data only after this operation has closed.
internal static class ProviderAttemptOperation
{
    public static async ValueTask<AttemptExecution> ExecuteAsync(RunState state, ProviderRequest request)
    {
        var configuration = state.Configuration;
        var cut = state.Cut;
        var exposure = new RuntimeExposure(configuration.Scope, request.Attempt, configuration.RequiredAcknowledgement, state.Accounting());
        var stop = cut.Check();
        var enteredExposure = false;
        var invoked = false;
        ProviderOutcome? outcome = null;
        ProviderError? error = null;
        ProviderExchangeResult? result = null;
        ProviderResponse? accepted = null;
        UsageAttemptObservation observation = request.Observation.Snapshot();

        if (stop == RuntimeStop.None && configuration.Hooks is { } hooks)
        {
            try
            {
                if (cut.TryStart(() => { enteredExposure = true; return hooks.BeforeDispatchAsync(exposure, cut.Token).AsTask(); }, out var pending))
                {
                    var receipt = await cut.WaitAsync(pending!).ConfigureAwait(false);
                    stop = receipt.Obtained ? receipt.Value?.Assess(exposure) ?? RuntimeStop.ExposureMissing : cut.Check();
                }
                else stop = cut.Check();
            }
            catch (Exception exception) when (Recoverable(exception)) { stop = RuntimeStop.ExposureFailed; }
        }
        if (cut.Check() != RuntimeStop.None) stop = cut.Check();
        if (stop == RuntimeStop.None)
        {
            try
            {
                if (cut.TryStart(() => { invoked = true; return configuration.Provider.ExchangeAsync(request, cut.Token).AsTask(); },
                    out var pending, request.Observation))
                {
                    var completion = await cut.WaitAsync(pending!).ConfigureAwait(false);
                    if (completion.Obtained)
                    {
                        result = completion.Value;
                        outcome = result?.Outcome ?? ProvidersOutcomeFailed;
                        error = result?.Error ?? ProviderError.ProviderFailed;
                    }
                    else stop = cut.Check();
                }
                else stop = cut.Check();
            }
            catch (Exception exception) when (Recoverable(exception))
            { outcome = ProvidersOutcomeFailed; error = ProviderError.ProviderFailed; }
        }
        if (cut.Check() != RuntimeStop.None) stop = cut.Check();

        if (!invoked)
        {
            request.Observation.Seal();
            observation = new(request.Attempt.ExecutionId, request.Attempt.LogicalCallId, request.Attempt.PhysicalAttemptId,
                request.Attempt.AttemptNumber, DispatchExposure.NotDispatched, new());
        }
        else
        {
            var captured = request.Observation.Seal();
            observation = captured;
            if (result is not null && stop == RuntimeStop.None)
            {
                // Association gates evidence import; payload validation stays after retention so valid usage survives rejection.
                if (!result.Scope.Matches(request.Scope) || !RunState.Matches(request.Attempt, result.Observation))
                { outcome = ProviderOutcome.Rejected; error = ProviderError.InvalidAssociation; result = null; }
                else if (HasConflictingFacts(request.Observation, captured, result.Observation))
                { outcome = ProviderOutcome.Rejected; error = ProviderError.ObservationConflict; result = null; }
                else
                {
                    // An associated returned observation can supply facts an interface extension did not stream.
                    // Once the local cut wins, only the already sealed channel is eligible instead.
                    observation = result.Observation;
                }
            }
        }
        // Finalize the reserved send slot from these sealed, association-checked facts before Host closure.
        state.Retain(request.Attempt, observation);

        if (stop == RuntimeStop.None && result?.Outcome == ProviderOutcome.Succeeded)
        {
            try
            {
                if (result.Response is null) throw new ProviderContractException(ProviderError.InvalidResponse);
                if (cut.TryCommit(() => state.Accept(request, result.Response))) accepted = result.Response;
                else stop = cut.Check();
            }
            catch (ProviderContractException exception)
            { outcome = ProviderOutcome.Rejected; error = exception.Error; }
        }

        // Outcome/error describe this request's normalized exchange, including invalid interface returns or actual faults.
        // A still-pending operation has neither, and is represented only with its explicit local cut.
        var settlement = new RuntimeSettlement(exposure, observation, stop, invoked, outcome, error, state.Accounting());
        var settlementStop = RuntimeStop.None;
        if (configuration.Hooks is { } closure && (enteredExposure || invoked))
            settlementStop = await DeliverSettlementAsync(state, closure, settlement).ConfigureAwait(false);

        if (cut.Check() != RuntimeStop.None) stop = cut.Check();
        if (stop != RuntimeStop.None) state.Close(stop);
        else if (settlementStop != RuntimeStop.None) state.Close(settlementStop);
        var retry = stop == RuntimeStop.None && settlementStop == RuntimeStop.None
            && outcome == ProviderOutcome.Failed && error == ProviderError.ProviderFailed ? result?.Retry : null;
        state.RecordAttempt(request, new(accepted, stop, settlementStop, outcome, error, retry));
        return new(accepted, stop, settlementStop, outcome, error, retry);
    }

    private const ProviderOutcome ProvidersOutcomeFailed = ProviderOutcome.Failed;
    private static bool Recoverable(Exception exception) => exception is not (OutOfMemoryException or StackOverflowException);

    private static bool HasConflictingFacts(ProviderObservation channel, UsageAttemptObservation captured, UsageAttemptObservation returned)
    {
        if (captured.Exposure == DispatchExposure.Dispatched && returned.Exposure != DispatchExposure.Dispatched
            || captured.Exposure == DispatchExposure.NotDispatched && returned.Exposure == DispatchExposure.Unknown
            || channel.HasCapturedUsage && captured.Exposure != returned.Exposure) return true;
        if (!channel.HasCapturedUsage) return false;
        var a = captured.Usage; var b = returned.Usage;
        return a.InputTokens != b.InputTokens || a.OutputTokens != b.OutputTokens
            || !a.ProviderCounters.Select(value => (value.Kind, value.Value, value.Relationship)).SequenceEqual(
                b.ProviderCounters.Select(value => (value.Kind, value.Value, value.Relationship)))
            || !SameAccounting(captured.Accounting, returned.Accounting);
    }
    internal static bool SameAccounting(UsageAccounting? a, UsageAccounting? b) => a == b || (a is not null && b is not null
        && a.Settlement == b.Settlement && Amounts(a.InFlightReservation, b.InFlightReservation)
        && Amounts(a.ConservativeUnobservedCharge, b.ConservativeUnobservedCharge)
        && a.EstimatedCost?.Amount == b.EstimatedCost?.Amount && a.EstimatedCost?.Currency == b.EstimatedCost?.Currency);
    private static bool Amounts(UsageTokenAmounts? a, UsageTokenAmounts? b) => a?.InputTokens == b?.InputTokens && a?.OutputTokens == b?.OutputTokens;

    private static async ValueTask<RuntimeStop> DeliverSettlementAsync(RunState state, IRuntimeExposureHooks hooks, RuntimeSettlement settlement)
    {
        var grace = state.Options.SettlementGrace;
        var remaining = state.Cut.Remaining;
        var allowance = state.Cut.Check() == RuntimeStop.None && remaining < grace ? remaining : grace;
        if (allowance <= TimeSpan.Zero) allowance = grace;
        using var closureCut = new RunCut(state.Options.TimeProvider, allowance, CancellationToken.None);
        Task<SettlementAcknowledgement?>? pending = null;
        try
        {
            pending = hooks.AfterAttemptAsync(settlement, closureCut.Token).AsTask();
            var receipt = await closureCut.WaitAsync(pending).ConfigureAwait(false);
            return receipt.Obtained ? receipt.Value?.Assess(settlement) ?? RuntimeStop.SettlementMissing : RuntimeStop.SettlementUnknown;
        }
        catch (Exception exception) when (Recoverable(exception)) { return RuntimeStop.SettlementFailed; }
    }
}

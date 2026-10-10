using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;

namespace SolusAgent.Runtime.Execution;

internal sealed record CallExecution(ProviderRequest Request, AttemptExecution Attempt);

// One logical call, one immutable request history and run ledger; no tools, progress or candidate effects in this loop.
internal static class ProviderCallOperation
{
    public static async ValueTask<CallExecution?> ExecuteAsync(RunState state, ProviderRequest request)
    {
        var policy = state.Request.UsageLimits?.RetryPolicy;
        while (true)
        {
            var attempt = await ProviderAttemptOperation.ExecuteAsync(state, request).ConfigureAwait(false);
            if (attempt.Retry is not { } retry || policy is null || !state.CanContinue
                || state.RoundAttemptNumber(request.Attempt) >= policy.MaximumAttemptsPerLogicalCall)
                return Finish();

            var delay = policy.Backoff;
            if (policy.HonorRetryAfter && retry.RetryAfter is { } hint)
            {
                if (hint > policy.MaximumDelay) return Finish();
                if (hint > delay) delay = hint;
            }
            // Check before waiting and again on admission. Only the logical-call debit is omitted on retry.
            if (!state.PreflightRetry()) return null;
            if (!state.Cut.TryStart(() => DelayAsync(delay, state.Cut), out var pending)) return null;
            var waited = await state.Cut.WaitAsync(pending!).ConfigureAwait(false);
            if (!waited.Obtained) return null;
            var next = state.AdmitRetry(request);
            if (next is null) return null;
            request = next;
            continue;

            CallExecution Finish()
            {
                if (attempt.ProviderOutcome != ProviderOutcome.Succeeded)
                    state.Close(attempt.ProviderError == ProviderError.LimitExceeded ? RuntimeStop.ResourceLimit : RuntimeStop.InvalidAssociation);
                return new(request, attempt);
            }
        }
    }
    private static async Task<bool> DelayAsync(TimeSpan delay, RunCut cut)
    {
        await Task.Delay(delay, cut.Clock, cut.Token).ConfigureAwait(false);
        return true;
    }
}

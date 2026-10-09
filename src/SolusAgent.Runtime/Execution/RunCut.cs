using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;

namespace SolusAgent.Runtime.Execution;

// One linearization gate for starts, acceptance and the local cut. Extension callbacks cannot keep an async wait alive.
internal sealed class RunCut : IDisposable
{
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly long started;
    private readonly TimeSpan duration;
    private readonly CancellationToken caller;
    private readonly CancellationTokenSource operationCancellation = new();
    private readonly TaskCompletionSource interrupted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ITimer timer;
    private readonly CancellationTokenRegistration registration;
    private ProviderObservation? observation;
    private Task? cancellationCompletion;
    private RuntimeStop stop;
    private bool disposed;
    private static readonly TimeSpan TimerSlice = TimeSpan.FromDays(20);

    public RunCut(TimeProvider clock, TimeSpan duration, CancellationToken caller)
    {
        this.clock = clock; this.duration = duration; this.caller = caller; started = clock.GetTimestamp();
        timer = clock.CreateTimer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        registration = caller.Register(() => Check());
        lock (gate) if (stop == RuntimeStop.None) Schedule();
    }
    public CancellationToken Token => operationCancellation.Token;
    public TimeProvider Clock => clock;
    public TimeSpan Remaining => duration - clock.GetElapsedTime(started);

    public RuntimeStop Check() { lock (gate) return CheckLocked(); }
    private RuntimeStop CheckLocked()
    {
        if (stop != RuntimeStop.None) return stop;
        var next = caller.IsCancellationRequested ? RuntimeStop.Cancelled
            : Remaining <= TimeSpan.Zero ? RuntimeStop.DurationLimit : RuntimeStop.None;
        if (next != RuntimeStop.None)
        {
            stop = next;
            observation?.Seal();
            interrupted.TrySetResult();
            // CancelAsync does not synchronously wait for uncooperative extension registrations.
            BeginCancellation();
        }
        return stop;
    }
    public bool TryCommit(Action action)
    {
        lock (gate)
        {
            if (CheckLocked() != RuntimeStop.None) return false;
            action();
            return true;
        }
    }
    public bool TryStart<T>(Func<Task<T>> start, out Task<T>? pending, ProviderObservation? activeObservation = null)
    {
        lock (gate)
        {
            pending = null;
            if (CheckLocked() != RuntimeStop.None) return false;
            observation = activeObservation;
            pending = start();
            return true;
        }
    }
    public async ValueTask<(bool Obtained, T? Value)> WaitAsync<T>(Task<T> pending, bool observeCompletedAtCut = false)
    {
        await Task.WhenAny(pending, interrupted.Task).ConfigureAwait(false);
        // Candidate delivery observes an already completed callback at this selection boundary.
        // This retains facts only; Check/TryStart still forbid every subsequent effect after a cut.
        if (observeCompletedAtCut && pending.IsCompleted)
            return (true, await pending.ConfigureAwait(false));
        if (Check() != RuntimeStop.None)
        {
            ObserveLate(pending);
            return (false, default);
        }
        return (true, await pending.ConfigureAwait(false));
    }
    private void Tick()
    {
        lock (gate)
        {
            if (!disposed && CheckLocked() == RuntimeStop.None) Schedule();
        }
    }
    private void Schedule()
    {
        var remaining = Remaining;
        timer.Change(remaining > TimerSlice ? TimerSlice : remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }
    internal static void ObserveLate(Task pending) => _ = pending.ContinueWith(task => { _ = task.Exception; },
        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    // Called under gate. A repeated CancelAsync can finish before the original callback dispatch.
    private Task BeginCancellation()
    {
        if (cancellationCompletion is null)
        {
            cancellationCompletion = operationCancellation.CancelAsync();
            ObserveLate(cancellationCompletion);
        }
        return cancellationCompletion;
    }
    public void Dispose()
    {
        Task cancelled;
        lock (gate)
        {
            if (disposed) return;
            disposed = true; observation?.Seal(); timer.Dispose();
            cancelled = BeginCancellation();
        }
        registration.Dispose();
        // Tokens may still be held by abandoned extension work. Dispose after cancellation callbacks, without waiting here.
        _ = cancelled.ContinueWith(_ => operationCancellation.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}

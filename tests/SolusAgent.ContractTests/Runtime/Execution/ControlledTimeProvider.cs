namespace SolusAgent.ContractTests.Runtime.Execution;

internal sealed class ControlledTimeProvider : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ControlledTimer> timers = [];
    private long ticks;
    public Action<TimeSpan>? TimerCreated { get; set; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (gate) return ticks; }
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ControlledTimer(this, callback, state);
        lock (gate) { timers.Add(timer); timer.Change(dueTime, period); }
        TimerCreated?.Invoke(dueTime);
        return timer;
    }
    public void Advance(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        List<ControlledTimer> due;
        lock (gate)
        {
            ticks += duration.Ticks;
            due = timers.Where(timer => timer.Due <= ticks).ToList();
            foreach (var timer in due) timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? long.MaxValue : ticks + timer.Period.Ticks;
        }
        foreach (var timer in due) timer.Fire();
    }
    private sealed class ControlledTimer(ControlledTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        private bool disposed;
        public long Due { get; set; } = long.MaxValue;
        public TimeSpan Period { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.gate)
            {
                if (disposed) return false;
                Period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + dueTime.Ticks;
                return true;
            }
        }
        public void Fire() { if (!disposed) callback(state); }
        public void Dispose() { lock (clock.gate) { disposed = true; clock.timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

namespace Ansight.Cli.Tests.TestSupport;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = [];
    private long timestamp;
    private DateTimeOffset utcNow = DateTimeOffset.UtcNow;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (gate) return timestamp; }
    public override DateTimeOffset GetUtcNow() { lock (gate) return utcNow; }
    public void SetUtcNow(DateTimeOffset value) { lock (gate) utcNow = value; }

    public bool HasTimerDueIn(TimeSpan remaining)
    {
        lock (gate) return timers.Any(timer => timer.DueAt == timestamp + remaining.Ticks);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (gate) timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan elapsed)
    {
        List<ManualTimer> due;
        lock (gate)
        {
            timestamp += elapsed.Ticks;
            utcNow += elapsed;
            due = timers.Where(timer => timer.DueAt <= timestamp).ToList();
            foreach (var timer in due) timer.DueAt = long.MaxValue;
        }
        foreach (var timer in due) timer.Fire();
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private bool disposed;
        public long DueAt { get; set; } = long.MaxValue;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Tests use one-shot timers only.");
            lock (owner.gate)
            {
                if (disposed) return false;
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.timestamp + dueTime.Ticks;
                return true;
            }
        }

        public void Fire() { if (!disposed) callback(state); }
        public void Dispose()
        {
            lock (owner.gate)
            {
                disposed = true;
                DueAt = long.MaxValue;
                owner.timers.Remove(this);
            }
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

namespace MoiCalendar.Tests;

// Small deterministic one-shot timer clock: scheduling tests never wait for real debounce/retry intervals.
internal sealed class ManualSyncTimeProvider : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.Parse("2026-10-06T00:00:00Z");
    private readonly List<ManualTimer> timers = [];
    public override DateTimeOffset GetUtcNow() => now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(int milliseconds)
    {
        now += TimeSpan.FromMilliseconds(milliseconds);
        foreach (var timer in timers.ToArray()) timer.FireIfDue();
    }

    private sealed class ManualTimer(ManualSyncTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset? due;
        private bool disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (disposed) return false;
            due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.now + dueTime;
            return true;
        }
        public void FireIfDue()
        {
            if (disposed || due is null || due > clock.now) return;
            due = null;
            callback(state);
        }
        public void Dispose() { disposed = true; due = null; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

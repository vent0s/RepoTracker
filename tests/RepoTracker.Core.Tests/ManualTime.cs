namespace RepoTracker.Core.Tests;

internal sealed class ManualTime : TimeProvider
{
    private readonly object sync = new();
    private DateTimeOffset now = DateTimeOffset.UnixEpoch;
    private readonly List<Timer> timers = [];
    public override DateTimeOffset GetUtcNow() { lock (sync) return now; }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (sync)
        {
            var timer = new Timer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer;
        }
    }
    public void Advance(TimeSpan amount)
    {
        List<Timer> ready;
        lock (sync)
        {
            now += amount; ready = timers.Where(timer => timer.Due <= now).ToList();
            foreach (var timer in ready) timer.Due = timer.Period > TimeSpan.Zero ? now + timer.Period : DateTimeOffset.MaxValue;
        }
        foreach (var timer in ready) timer.Callback(timer.State);
    }
    private sealed class Timer(ManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback = callback;
        public object? State = state;
        public DateTimeOffset Due;
        public TimeSpan Period;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.sync) { Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.now + dueTime; Period = period; return true; }
        }
        public void Dispose() { lock (owner.sync) owner.timers.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

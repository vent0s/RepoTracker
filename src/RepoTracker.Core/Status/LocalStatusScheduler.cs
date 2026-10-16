using System.Collections.Immutable;
using System.Threading.Channels;
using RepoTracker.Core.Tracking;

namespace RepoTracker.Core.Status;

public sealed record StatusScheduleOptions
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(400);
    public TimeSpan MaxDebounce { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan StartSpacing { get; init; } = TimeSpan.FromMilliseconds(100);
    public int Concurrency { get; init; } = 2;
}

/// <summary>One wake-driven queue; no per-repository timers and no UI-thread Git work.</summary>
public sealed class LocalStatusScheduler : IAsyncDisposable
{
    private sealed class Entry(TrackedRepository repository, DateTimeOffset due)
    {
        public TrackedRepository Repository = repository;
        public DateTimeOffset Due = due;
        public DateTimeOffset? FirstEvent;
        public bool FollowUp;
        public StatusSnapshot Snapshot = new();
        public CancellationTokenSource? Cancellation;
        public IRepositoryWatch? Watch;
        public Task? Work;
    }
    private readonly object sync = new();
    private readonly Dictionary<Guid, Entry> entries = [];
    private readonly HashSet<Entry> running = [];
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource lifetime = new();
    private readonly ILocalStatusChecker checker;
    private readonly IRepositoryWatchFactory watchers;
    private readonly TimeProvider clock;
    private StatusScheduleOptions options;
    private readonly Task loop;
    private DateTimeOffset nextStart;
    private bool suspended, stopped;
    private Task? shutdown;
    public event EventHandler? Changed;

    public LocalStatusScheduler(ILocalStatusChecker checker, IRepositoryWatchFactory watchers,
        TimeProvider? clock = null, StatusScheduleOptions? options = null)
    {
        this.checker = checker; this.watchers = watchers;
        this.clock = clock ?? TimeProvider.System; this.options = options ?? new();
        if (this.options.Concurrency < 1 || this.options.Interval <= TimeSpan.Zero || this.options.Debounce < TimeSpan.Zero ||
            this.options.MaxDebounce < this.options.Debounce || this.options.StartSpacing < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        loop = Task.Run(RunAsync);
    }

    public ImmutableDictionary<Guid, StatusSnapshot> Snapshot
    {
        get { lock (sync) return entries.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.Snapshot); }
    }

    public void SetRepositories(IEnumerable<TrackedRepository> repositories)
    {
        lock (sync)
        {
            if (stopped) return;
            var wanted = repositories.ToDictionary(repo => repo.Id);
            foreach (var pair in entries.ToArray())
                if (!wanted.TryGetValue(pair.Key, out var repo) || repo.WorktreeRoot != pair.Value.Repository.WorktreeRoot ||
                    repo.GitDir != pair.Value.Repository.GitDir || repo.CommonDir != pair.Value.Repository.CommonDir)
                {
                    entries.Remove(pair.Key);
                    pair.Value.Cancellation?.Cancel();
                    pair.Value.Watch?.Dispose(); pair.Value.Watch = null;
                }
            var order = 0;
            foreach (var repo in wanted.Values)
            {
                if (!entries.ContainsKey(repo.Id)) entries.Add(repo.Id, new(repo, clock.GetUtcNow() + options.StartSpacing * order++));
            }
        }
        Signal(); Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Refresh()
    {
        lock (sync)
        {
            if (stopped) return;
            var now = clock.GetUtcNow();
            foreach (var entry in entries.Values)
            {
                entry.FirstEvent = null;
                if (running.Contains(entry)) entry.FollowUp = true;
                else entry.Due = now;
            }
        }
        Signal();
    }

    public void SetSchedule(TimeSpan interval, int concurrency)
    {
        if (interval <= TimeSpan.Zero || concurrency < 1) throw new ArgumentOutOfRangeException(nameof(interval));
        lock (sync)
        {
            if (stopped || options.Interval == interval && options.Concurrency == concurrency) return;
            options = options with { Interval = interval, Concurrency = concurrency };
            foreach (var entry in entries.Values)
                if (!running.Contains(entry))
                {
                    var periodic = entry.Snapshot.CheckedAt is { } last ? last + interval : clock.GetUtcNow();
                    // An already queued filesystem event / explicit refresh must not be postponed by a settings change.
                    entry.Due = entry.FirstEvent is not null || entry.Due <= clock.GetUtcNow() ? Min(entry.Due, periodic) : periodic;
                }
        }
        Signal();
    }

    public void NotifyChanged(Guid repositoryId)
    {
        lock (sync)
        {
            if (stopped || !entries.TryGetValue(repositoryId, out var entry)) return;
            var now = clock.GetUtcNow();
            entry.FirstEvent ??= now;
            var due = Min(now + options.Debounce, entry.FirstEvent.Value + options.MaxDebounce);
            if (running.Contains(entry)) { entry.FollowUp = true; entry.Due = due; }
            else entry.Due = entry.FirstEvent == now ? Min(entry.Due, due) : Min(due, entry.FirstEvent.Value + options.MaxDebounce);
        }
        Signal();
    }

    public void Suspend()
    {
        lock (sync)
        {
            if (stopped) return;
            suspended = true;
            foreach (var entry in running) entry.Cancellation?.Cancel();
        }
        Signal();
    }

    public void Resume()
    {
        lock (sync)
        {
            if (stopped) return;
            suspended = false; nextStart = clock.GetUtcNow();
            foreach (var entry in entries.Values) { entry.Due = nextStart; entry.FollowUp = running.Contains(entry); }
        }
        Signal();
    }

    private async Task RunAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var started = false;
                var delay = Timeout.InfiniteTimeSpan;
                lock (sync)
                {
                    if (!suspended && !stopped && running.Count < options.Concurrency)
                    {
                        var candidate = entries.Values.Where(entry => !running.Any(active => active.Repository.Id == entry.Repository.Id ||
                            string.Equals(active.Repository.WorktreeRoot, entry.Repository.WorktreeRoot, StringComparison.OrdinalIgnoreCase))).OrderBy(entry => entry.Due).FirstOrDefault();
                        if (candidate is not null)
                        {
                            var due = candidate.Due > nextStart ? candidate.Due : nextStart;
                            var now = clock.GetUtcNow();
                            if (due <= now)
                            {
                                candidate.FirstEvent = null; candidate.FollowUp = false;
                                candidate.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                                candidate.Snapshot = candidate.Snapshot with { IsChecking = true };
                                running.Add(candidate);
                                nextStart = now + options.StartSpacing;
                                candidate.Work = Task.Run(() => CheckAsync(candidate));
                                started = true;
                            }
                            else delay = due - now;
                        }
                    }
                }
                if (started) { Changed?.Invoke(this, EventArgs.Empty); continue; }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                var signal = wake.Reader.WaitToReadAsync(wait.Token).AsTask();
                var timeout = Task.Delay(delay, clock, wait.Token);
                await Task.WhenAny(signal, timeout).ConfigureAwait(false);
                await wait.CancelAsync().ConfigureAwait(false);
                while (wake.Reader.TryRead(out _)) { }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task CheckAsync(Entry entry)
    {
        StatusResult? result = null;
        var token = entry.Cancellation!.Token;
        try
        {
            // Watch setup and directory access also stay off the dispatcher thread.
            var watch = watchers.Watch(entry.Repository, () => NotifyChanged(entry.Repository.Id));
            lock (sync)
            {
                if (stopped || token.IsCancellationRequested || !IsCurrent(entry)) watch.Dispose();
                else { entry.Watch?.Dispose(); entry.Watch = watch; }
            }
            token.ThrowIfCancellationRequested();
            result = await checker.CheckAsync(entry.Repository, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { result = new(null, StatusError.Failed, "本地检查未完成，将在下一轮重试。"); }
        lock (sync)
        {
            running.Remove(entry);
            entry.Cancellation.Dispose(); entry.Cancellation = null;
            if (!stopped && IsCurrent(entry))
            {
                var now = clock.GetUtcNow();
                if (result is not null && !token.IsCancellationRequested)
                    entry.Snapshot = new(result.Data ?? entry.Snapshot.Data, result.Error, result.Message, now,
                        result.Data is not null ? now : entry.Snapshot.SucceededAt, false, entry.Watch?.IsActive == true, result.Remote ?? entry.Snapshot.Remote);
                else entry.Snapshot = entry.Snapshot with { IsChecking = false };
                entry.Due = entry.FollowUp ? Min(entry.Due, now + options.Debounce) : now + options.Interval;
            }
        }
        Signal(); Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool IsCurrent(Entry entry) => entries.TryGetValue(entry.Repository.Id, out var current) && ReferenceEquals(current, entry);
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private void Signal() => wake.Writer.TryWrite(true);

    public ValueTask DisposeAsync()
    {
        lock (sync) return new(shutdown ??= StopAsync());
    }

    private async Task StopAsync()
    {
        Task[] tasks;
        lock (sync)
        {
            if (stopped) return;
            stopped = true;
            foreach (var entry in entries.Values) { entry.Watch?.Dispose(); entry.Watch = null; }
            tasks = running.Select(entry => entry.Work!).ToArray();
        }
        await lifetime.CancelAsync().ConfigureAwait(false);
        Signal();
        await loop.ConfigureAwait(false);
        await Task.WhenAll(tasks).ConfigureAwait(false);
        lifetime.Dispose();
    }
}

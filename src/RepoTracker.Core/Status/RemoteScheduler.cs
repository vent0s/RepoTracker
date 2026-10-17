using RepoTracker.Core.Localization;
using System.Collections.Immutable;
using System.Threading.Channels;
using RepoTracker.Core.Tracking;

namespace RepoTracker.Core.Status;

public enum RemoteOutcome { Never, Success, Skipped, Authentication, Offline, Timeout, Failed }
public sealed record RemoteResult(RemoteOutcome Outcome, string Message);
public sealed record RemoteSnapshot(RemoteOutcome Outcome = RemoteOutcome.Never, string Message = "",
    DateTimeOffset? AttemptedAt = null, DateTimeOffset? SucceededAt = null, bool IsChecking = false);
public interface IRemoteChecker
{
    Task<RemoteResult> CheckAsync(TrackedRepository repository, CancellationToken cancellationToken);
}

/// <summary>A single staggered remote queue also serializes every shared commonDir. Local checks have their own queue.</summary>
public sealed class RemoteScheduler : IAsyncDisposable
{
    private sealed class Entry(TrackedRepository repo, DateTimeOffset due)
    {
        public TrackedRepository Repository = repo;
        public DateTimeOffset Due = due;
        public RemoteSnapshot Snapshot = new();
    }
    private readonly object sync = new();
    private readonly Dictionary<Guid, Entry> entries = [];
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? active;
    private Entry? current;
    private readonly IRemoteChecker checker;
    private readonly Action succeeded;
    private readonly TimeProvider clock;
    private TimeSpan interval;
    private readonly TimeSpan spacing;
    private readonly Task loop;
    private Task? shutdown;
    private DateTimeOffset nextStart;
    private long generation;
    private bool enabled, suspended, stopped;
    public event EventHandler? Changed;

    public RemoteScheduler(IRemoteChecker checker, Action succeeded, TimeProvider? clock = null, TimeSpan? interval = null, TimeSpan? spacing = null)
    {
        this.checker = checker; this.succeeded = succeeded; this.clock = clock ?? TimeProvider.System;
        this.interval = interval ?? TimeSpan.FromMinutes(10); this.spacing = spacing ?? TimeSpan.FromSeconds(1);
        if (this.interval <= TimeSpan.Zero || this.spacing < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        loop = Task.Run(RunAsync);
    }

    public ImmutableDictionary<Guid, RemoteSnapshot> Snapshot
    { get { lock (sync) return entries.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.Snapshot); } }

    public void Configure(IEnumerable<TrackedRepository> repositories, bool remoteEnabled)
    {
        lock (sync)
        {
            if (stopped) return;
            var wanted = repositories.ToDictionary(repo => repo.Id);
            foreach (var pair in entries.ToArray())
                if (!wanted.TryGetValue(pair.Key, out var repo) || repo.GitDir != pair.Value.Repository.GitDir ||
                    repo.CommonDir != pair.Value.Repository.CommonDir || repo.WorktreeRoot != pair.Value.Repository.WorktreeRoot)
                { entries.Remove(pair.Key); if (current == pair.Value) active?.Cancel(); }
            foreach (var repo in wanted.Values)
                if (!entries.ContainsKey(repo.Id)) entries.Add(repo.Id, new(repo, clock.GetUtcNow()));
            if (enabled != remoteEnabled)
            {
                enabled = remoteEnabled; generation++; active?.Cancel();
                if (enabled) foreach (var entry in entries.Values) entry.Due = clock.GetUtcNow();
            }
        }
        Signal(); Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Suspend()
    { lock (sync) { suspended = true; generation++; active?.Cancel(); } Signal(); }
    public void Resume()
    {
        lock (sync)
        {
            suspended = false;
            foreach (var entry in entries.Values) entry.Due = clock.GetUtcNow();
        }
        Signal();
    }
    public void Refresh()
    {
        lock (sync) foreach (var entry in entries.Values) if (entry != current) entry.Due = clock.GetUtcNow();
        Signal();
    }

    public void SetInterval(TimeSpan value)
    {
        if (value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(value));
        lock (sync)
        {
            if (stopped || value == interval) return;
            interval = value;
            foreach (var entry in entries.Values)
                if (entry != current) entry.Due = entry.Snapshot.AttemptedAt is { } last ? last + value : clock.GetUtcNow();
        }
        Signal();
    }

    private async Task RunAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                Entry? candidate = null;
                long epoch = 0;
                var delay = Timeout.InfiniteTimeSpan;
                lock (sync)
                {
                    if (enabled && !suspended && !stopped)
                    {
                        var first = entries.Values.OrderBy(entry => entry.Due).FirstOrDefault();
                        if (first is not null)
                        {
                            var due = first.Due > nextStart ? first.Due : nextStart;
                            if (due <= clock.GetUtcNow())
                            {
                                candidate = current = first; epoch = generation;
                                active = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                                first.Snapshot = first.Snapshot with { IsChecking = true };
                            }
                            else delay = due - clock.GetUtcNow();
                        }
                    }
                }
                if (candidate is not null)
                {
                    Changed?.Invoke(this, EventArgs.Empty);
                    var token = active!.Token;
                    RemoteResult? result = null;
                    try { result = await checker.CheckAsync(candidate.Repository, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    catch (Exception) { result = new(RemoteOutcome.Failed, Text.Get("Remote.RetryLater")); }
                    var success = false;
                    lock (sync)
                    {
                        if (entries.TryGetValue(candidate.Repository.Id, out var saved) && saved == candidate)
                        {
                            var now = clock.GetUtcNow();
                            if (epoch == generation && !token.IsCancellationRequested && result is not null)
                            {
                                success = result.Outcome == RemoteOutcome.Success;
                                candidate.Snapshot = new(result.Outcome, result.Message, now, success ? now : candidate.Snapshot.SucceededAt);
                                candidate.Due = now + interval;
                            }
                            else candidate.Snapshot = candidate.Snapshot with { IsChecking = false };
                        }
                        active.Dispose(); active = null; current = null;
                        nextStart = clock.GetUtcNow() + spacing;
                    }
                    if (success) succeeded();
                    Changed?.Invoke(this, EventArgs.Empty);
                    continue;
                }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                await Task.WhenAny(wake.Reader.WaitToReadAsync(wait.Token).AsTask(), Task.Delay(delay < TimeSpan.Zero && delay != Timeout.InfiniteTimeSpan ? TimeSpan.Zero : delay, clock, wait.Token)).ConfigureAwait(false);
                await wait.CancelAsync().ConfigureAwait(false);
                while (wake.Reader.TryRead(out _)) { }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private void Signal() => wake.Writer.TryWrite(true);
    public ValueTask DisposeAsync() { lock (sync) return new(shutdown ??= StopAsync()); }
    private async Task StopAsync()
    {
        lock (sync) { stopped = true; generation++; }
        await lifetime.CancelAsync().ConfigureAwait(false);
        Signal();
        await loop.ConfigureAwait(false);
        lifetime.Dispose();
    }
}

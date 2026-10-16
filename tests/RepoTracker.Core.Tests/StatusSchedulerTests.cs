using System.Collections.Concurrent;
using RepoTracker.Core.Status;
using RepoTracker.Core.Tracking;

namespace RepoTracker.Core.Tests;

public sealed class StatusSchedulerTests
{
    private static readonly GitStatus Clean = new(HeadKind.Detached, null, new string('a', 40), null, null, null, 0, 0, 0, 0);
    private static TrackedRepository Repo(int i) => new(Guid.NewGuid(), $"E:\\fixture\\{i}", $"E:\\fixture\\{i}\\.git", $"E:\\fixture\\{i}\\.git", "r" + i, null, DateTimeOffset.UnixEpoch);
    private static StatusScheduleOptions Options => new() { StartSpacing = TimeSpan.Zero };

    [Fact]
    public async Task TwentyRepositoriesRespectConcurrencyAndNeverReenter()
    {
        var checker = new BlockingChecker(); var watches = new Watches();
        await using var scheduler = new LocalStatusScheduler(checker, watches, options: Options);
        var repos = Enumerable.Range(0, 20).Select(Repo).ToArray(); scheduler.SetRepositories(repos);
        await Until(() => checker.Calls.Count == 2); Assert.Equal(2, checker.Active);
        scheduler.Refresh(); scheduler.Refresh();
        for (var i = 0; i < 22; i++)
        {
            await Until(() => checker.Calls.Count > i);
            checker.Calls.ToArray()[i].Result.TrySetResult(new(Clean));
        }
        await Until(() => checker.Active == 0);
        Assert.Equal(2, checker.MaxActive); Assert.False(checker.Reentered);
        Assert.All(scheduler.Snapshot.Values, status => Assert.NotNull(status.Data));
    }

    [Fact]
    public async Task RemovedAndReaddedIdentityRejectsOldResultsEvenIfCheckerIgnoresCancellation()
    {
        var checker = new BlockingChecker(); var watches = new Watches();
        await using var scheduler = new LocalStatusScheduler(checker, watches, options: Options);
        var repo = Repo(1); scheduler.SetRepositories([repo]); await Until(() => checker.Calls.Count == 1);
        var old = checker.Calls.First(); scheduler.SetRepositories([]); Assert.True(old.Token.IsCancellationRequested);
        Assert.Empty(scheduler.Snapshot); scheduler.SetRepositories([repo]);
        await Task.Delay(30); Assert.Single(checker.Calls);
        old.Result.SetResult(new(Clean with { Staged = 9 }));
        await Until(() => checker.Calls.Count == 2);
        checker.Calls.Last().Result.SetResult(new(Clean));
        await Until(() => scheduler.Snapshot[repo.Id].CheckedAt is not null);
        Assert.Equal(0, scheduler.Snapshot[repo.Id].Data!.Staged);
        Assert.False(checker.Reentered);
    }

    [Fact]
    public async Task EventsDebounceAndPeriodicPollingUsesControlledTime()
    {
        var clock = new ManualTime(); var checker = new ImmediateChecker();
        await using var scheduler = new LocalStatusScheduler(checker, new Watches(), clock, Options);
        var repo = Repo(1); scheduler.SetRepositories([repo]); await Until(() => scheduler.Snapshot[repo.Id].CheckedAt is not null);
        for (var i = 0; i < 100; i++) scheduler.NotifyChanged(repo.Id);
        await Task.Delay(50); Assert.Equal(1, checker.Count);
        clock.Advance(TimeSpan.FromMilliseconds(399)); await Task.Delay(30); Assert.Equal(1, checker.Count);
        clock.Advance(TimeSpan.FromMilliseconds(1)); await Until(() => checker.Count == 2);
        await Until(() => !scheduler.Snapshot[repo.Id].IsChecking);
        clock.Advance(TimeSpan.FromSeconds(15)); await Until(() => checker.Count == 3);
    }

    [Fact]
    public async Task SuspendedChecksCancelAndResumeStaggersWithoutBurst()
    {
        var clock = new ManualTime(); var checker = new BlockingChecker();
        var scheduler = new LocalStatusScheduler(checker, new Watches(), clock, new() { StartSpacing = TimeSpan.FromSeconds(1) });
        var repos = new[] { Repo(1), Repo(2), Repo(3) }; scheduler.SetRepositories(repos);
        await Until(() => checker.Calls.Count == 1);
        scheduler.Suspend(); var call = checker.Calls.First(); Assert.True(call.Token.IsCancellationRequested);
        call.Result.SetResult(new(Clean)); await Until(() => checker.Active == 0);
        clock.Advance(TimeSpan.FromMinutes(5)); await Task.Delay(30); Assert.Single(checker.Calls);
        scheduler.Resume(); await Until(() => checker.Calls.Count == 2);
        await Task.Delay(30); Assert.Equal(2, checker.Calls.Count);
        clock.Advance(TimeSpan.FromSeconds(1)); await Until(() => checker.Calls.Count == 3);
        var stop = scheduler.DisposeAsync().AsTask(); Assert.False(stop.IsCompleted);
        await Until(() => checker.Calls.All(pending => pending.Token.IsCancellationRequested));
        foreach (var pending in checker.Calls) { Assert.True(pending.Token.IsCancellationRequested); pending.Result.TrySetResult(new(Clean)); }
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromHours(1)); scheduler.Refresh(); Assert.Equal(3, checker.Calls.Count);
    }

    [Fact]
    public async Task FailurePreservesSuccessfulSnapshotAndDisposeReleasesWatches()
    {
        var checker = new ImmediateChecker(); var watches = new Watches(); var repo = Repo(1);
        var scheduler = new LocalStatusScheduler(checker, watches, options: Options);
        scheduler.SetRepositories([repo]); await Until(() => scheduler.Snapshot[repo.Id].Data is not null);
        var success = scheduler.Snapshot[repo.Id].SucceededAt;
        checker.Next = new(null, StatusError.Timeout, "timeout"); scheduler.Refresh();
        await Until(() => scheduler.Snapshot[repo.Id].Error == StatusError.Timeout);
        Assert.Equal(Clean, scheduler.Snapshot[repo.Id].Data); Assert.Equal(success, scheduler.Snapshot[repo.Id].SucceededAt);
        await scheduler.DisposeAsync(); Assert.Equal(0, watches.Active);
    }

    [Fact]
    public async Task ChangedIntervalReschedulesWithoutPostponingPendingFilesystemEvent()
    {
        var clock = new ManualTime(); var checker = new ImmediateChecker(); var repo = Repo(1);
        await using var scheduler = new LocalStatusScheduler(checker, new Watches(), clock, Options);
        scheduler.SetRepositories([repo]); await Until(() => scheduler.Snapshot[repo.Id].CheckedAt is not null);
        scheduler.SetSchedule(TimeSpan.FromSeconds(30), 1);
        clock.Advance(TimeSpan.FromSeconds(15)); await Task.Delay(50); Assert.Equal(1, checker.Count);
        clock.Advance(TimeSpan.FromSeconds(15)); await Until(() => checker.Count == 2 && !scheduler.Snapshot[repo.Id].IsChecking);
        scheduler.NotifyChanged(repo.Id); scheduler.SetSchedule(TimeSpan.FromSeconds(60), 2);
        clock.Advance(TimeSpan.FromMilliseconds(400)); await Until(() => checker.Count == 3);
    }
    [Fact]
    public async Task LowerConcurrencyDrainsExistingChecksAndHigherConcurrencyStartsWaitingWork()
    {
        var checker = new BlockingChecker(); var scheduler = new LocalStatusScheduler(checker, new Watches(), options: Options);
        try
        {
            scheduler.SetRepositories([Repo(1), Repo(2), Repo(3), Repo(4), Repo(5)]);
            await Until(() => checker.Calls.Count == 2);
            scheduler.SetSchedule(TimeSpan.FromMinutes(1), 1);
            checker.Calls.First().Result.SetResult(new(Clean));
            await Until(() => checker.Active == 1); await Task.Delay(50); Assert.Equal(2, checker.Calls.Count);
            Assert.False(checker.Calls.Last().Token.IsCancellationRequested);
            scheduler.SetSchedule(TimeSpan.FromMinutes(1), 4);
            await Until(() => checker.Calls.Count == 5); Assert.Equal(4, checker.Active); Assert.False(checker.Reentered);
        }
        finally
        {
            var stop = scheduler.DisposeAsync().AsTask();
            foreach (var call in checker.Calls) call.Result.TrySetResult(new(Clean));
            await stop;
        }
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
    private sealed record Call(Guid Id, CancellationToken Token, TaskCompletionSource<StatusResult> Result);
    private sealed class BlockingChecker : ILocalStatusChecker
    {
        public readonly ConcurrentQueue<Call> Calls = new();
        private readonly ConcurrentDictionary<Guid, byte> activeIds = new();
        public int Active, MaxActive; public bool Reentered;
        public async Task<StatusResult> CheckAsync(TrackedRepository repo, CancellationToken token)
        {
            if (!activeIds.TryAdd(repo.Id, 0)) Reentered = true;
            var count = Interlocked.Increment(ref Active); MaxActive = Math.Max(MaxActive, count);
            var result = new TaskCompletionSource<StatusResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Enqueue(new(repo.Id, token, result));
            try { return await result.Task; }
            finally { activeIds.TryRemove(repo.Id, out _); Interlocked.Decrement(ref Active); }
        }
    }
    private sealed class ImmediateChecker : ILocalStatusChecker
    {
        public int Count;
        public StatusResult Next = new(Clean);
        public Task<StatusResult> CheckAsync(TrackedRepository repo, CancellationToken token) { Interlocked.Increment(ref Count); return Task.FromResult(Next); }
    }
    private sealed class Watches : IRepositoryWatchFactory
    {
        public int Active;
        public IRepositoryWatch Watch(TrackedRepository repo, Action changed) { Interlocked.Increment(ref Active); return new Lease(this); }
        private sealed class Lease(Watches owner) : IRepositoryWatch
        {
            public bool IsActive => true;
            public void Dispose() => Interlocked.Decrement(ref owner.Active);
        }
    }
}

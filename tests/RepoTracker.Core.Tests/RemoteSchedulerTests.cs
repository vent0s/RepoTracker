using System.Collections.Concurrent;
using RepoTracker.Core.Status;
using RepoTracker.Core.Tracking;

namespace RepoTracker.Core.Tests;

public sealed class RemoteSchedulerTests
{
    private static TrackedRepository Repo(int i) => new(Guid.NewGuid(), $"E:/fixture/{i}", $"E:/fixture/{i}/.git", "E:/fixture/shared/.git", "repo", null, DateTimeOffset.UnixEpoch);
    [Fact]
    public async Task OffNeverFetchesAndRapidToggleRejectsCancelledResults()
    {
        var checker = new Checker(); var success = 0;
        await using var queue = new RemoteScheduler(checker, () => success++, spacing: TimeSpan.Zero);
        var repo = Repo(1);
        queue.Configure([repo], false); queue.Refresh(); await Task.Delay(60); Assert.Empty(checker.Calls);
        queue.Configure([repo], true); await Until(() => checker.Calls.Count == 1);
        var first = checker.Calls.First(); queue.Configure([repo], false); queue.Configure([repo], true);
        Assert.True(first.Token.IsCancellationRequested);
        first.Result.SetResult(new(RemoteOutcome.Success, "late success"));
        await Until(() => checker.Calls.Count == 2); Assert.Equal(0, success); Assert.Null(queue.Snapshot[repo.Id].SucceededAt);
        checker.Calls.Last().Result.SetResult(new(RemoteOutcome.Success, "ok"));
        await Until(() => queue.Snapshot[repo.Id].SucceededAt is not null);
        queue.Configure([repo], false); var count = checker.Calls.Count; queue.Refresh(); await Task.Delay(60);
        Assert.Equal(count, checker.Calls.Count); Assert.NotNull(queue.Snapshot[repo.Id].SucceededAt);
    }
    [Fact]
    public async Task SharedCommonDirectoryIsSerialAndRemovedResultsAreIgnored()
    {
        var checker = new Checker();
        await using var queue = new RemoteScheduler(checker, () => { }, spacing: TimeSpan.Zero);
        var first = Repo(1); var second = Repo(2);
        queue.Configure([first, second], true); await Until(() => checker.Calls.Count == 1);
        queue.Configure([second], true); Assert.True(checker.Calls.First().Token.IsCancellationRequested);
        await Task.Delay(50); Assert.Single(checker.Calls);
        checker.Calls.First().Result.SetResult(new(RemoteOutcome.Success, "removed"));
        await Until(() => checker.Calls.Count == 2); Assert.False(queue.Snapshot.ContainsKey(first.Id));
        checker.Calls.Last().Result.SetResult(new(RemoteOutcome.Success, "ok"));
        await Until(() => queue.Snapshot[second.Id].SucceededAt is not null);
        Assert.Equal(1, checker.MaxActive);
    }
    [Fact]
    public async Task CadenceSpacingAndFailedRecoveryPreserveLastSuccess()
    {
        var clock = new ManualTime(); var checker = new Checker();
        await using var queue = new RemoteScheduler(checker, () => { }, clock);
        var repo = Repo(1); queue.Configure([repo, Repo(2)], true);
        await Until(() => checker.Calls.Count == 1); checker.Calls.First().Result.SetResult(new(RemoteOutcome.Success, "ok"));
        await Until(() => queue.Snapshot[repo.Id].SucceededAt is not null);
        var success = queue.Snapshot[repo.Id].SucceededAt;
        await Task.Delay(20); Assert.Single(checker.Calls);
        clock.Advance(TimeSpan.FromSeconds(1)); await Until(() => checker.Calls.Count == 2);
        checker.Calls.Last().Result.SetResult(new(RemoteOutcome.Skipped, "local"));
        await Until(() => checker.Active == 0); await Task.Delay(20);
        clock.Advance(TimeSpan.FromMinutes(10)); await Until(() => checker.Calls.Count == 3);
        checker.Calls.Last().Result.SetResult(new(RemoteOutcome.Offline, "offline"));
        await Until(() => queue.Snapshot[repo.Id].Outcome == RemoteOutcome.Offline);
        Assert.Equal(success, queue.Snapshot[repo.Id].SucceededAt);
        queue.Configure([repo], true); queue.Refresh(); clock.Advance(TimeSpan.FromSeconds(1));
        await Until(() => checker.Calls.Count == 4); checker.Calls.Last().Result.SetResult(new(RemoteOutcome.Success, "recovered"));
        await Until(() => queue.Snapshot[repo.Id].Outcome == RemoteOutcome.Success);
        Assert.True(queue.Snapshot[repo.Id].SucceededAt > success);
    }
    [Fact]
    public async Task SuspendAndDisposeCancelAndDrainActiveWork()
    {
        var checker = new Checker(); var queue = new RemoteScheduler(checker, () => { }, spacing: TimeSpan.Zero);
        var repo = Repo(1); queue.Configure([repo], true); await Until(() => checker.Calls.Count == 1);
        queue.Suspend(); Assert.True(checker.Calls.First().Token.IsCancellationRequested);
        checker.Calls.First().Result.SetResult(new(RemoteOutcome.Success, "late")); await Until(() => checker.Active == 0);
        await Task.Delay(30); Assert.Single(checker.Calls); queue.Resume(); await Until(() => checker.Calls.Count == 2);
        var stop = queue.DisposeAsync().AsTask(); Assert.False(stop.IsCompleted);
        await Until(() => checker.Calls.Last().Token.IsCancellationRequested);
        Assert.True(checker.Calls.Last().Token.IsCancellationRequested);
        checker.Calls.Last().Result.SetResult(new(RemoteOutcome.Success, "late")); await stop.WaitAsync(TimeSpan.FromSeconds(3));
        await queue.DisposeAsync(); Assert.Null(queue.Snapshot[repo.Id].SucceededAt);
    }
    [Fact]
    public async Task IntervalChangesApplyWithoutEnablingRemoteOrCancellingActiveFetch()
    {
        var clock = new ManualTime(); var checker = new Checker(); var repo = Repo(1);
        var queue = new RemoteScheduler(checker, () => { }, clock, spacing: TimeSpan.Zero);
        try
        {
            queue.Configure([repo], false); queue.SetInterval(TimeSpan.FromMinutes(1));
            clock.Advance(TimeSpan.FromHours(1)); await Task.Delay(50); Assert.Empty(checker.Calls);
            queue.Configure([repo], true); await Until(() => checker.Calls.Count == 1);
            queue.SetInterval(TimeSpan.FromMinutes(2)); Assert.False(checker.Calls.First().Token.IsCancellationRequested);
            checker.Calls.First().Result.SetResult(new(RemoteOutcome.Success, "ok"));
            await Until(() => queue.Snapshot[repo.Id].SucceededAt is not null);
            clock.Advance(TimeSpan.FromMinutes(1)); await Task.Delay(50); Assert.Single(checker.Calls);
            queue.SetInterval(TimeSpan.FromMinutes(1)); await Until(() => checker.Calls.Count == 2);
        }
        finally
        {
            var stop = queue.DisposeAsync().AsTask(); foreach (var call in checker.Calls) call.Result.TrySetResult(new(RemoteOutcome.Success, "done")); await stop;
        }
    }
    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(10, timeout.Token); }
    private sealed record Call(CancellationToken Token, TaskCompletionSource<RemoteResult> Result);
    private sealed class Checker : IRemoteChecker
    {
        public ConcurrentQueue<Call> Calls = []; public int Active; public int MaxActive;
        public async Task<RemoteResult> CheckAsync(TrackedRepository repo, CancellationToken token)
        {
            Interlocked.Increment(ref Active); MaxActive = Math.Max(Active, MaxActive);
            var call = new Call(token, new(TaskCreationOptions.RunContinuationsAsynchronously)); Calls.Enqueue(call);
            try { return await call.Result.Task; } finally { Interlocked.Decrement(ref Active); }
        }
    }
}

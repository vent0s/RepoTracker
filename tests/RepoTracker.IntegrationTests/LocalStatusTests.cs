using System.Security.Cryptography;
using RepoTracker.Core.Status;
using RepoTracker.Core.Tracking;
using RepoTracker.Infrastructure.Git;
using RepoTracker.Tests.Shared;

namespace RepoTracker.IntegrationTests;

public sealed class LocalStatusTests
{
    [Fact]
    public async Task ActualGitReadsCleanDirtyStagedUnstagedRenamedAndConflictedWithoutWriting()
    {
        var area = new TestWorkspace(); var root = await area.CreateDetachedRepositoryAsync("真实状态 中文");
        await TestWorkspace.CommitFixtureAsync(root);
        var repo = await Record(root); var checker = new GitStatusChecker(new GitProcess());
        async Task<GitStatus> Read()
        {
            var before = Snapshot(root); var result = await checker.CheckAsync(repo, default);
            Assert.Equal(StatusError.None, result.Error); Assert.NotNull(result.Data);
            Assert.Equal(before, Snapshot(root)); return result.Data;
        }
        var clean = await Read(); Assert.False(clean.IsDirty); Assert.Equal(HeadKind.Detached, clean.HeadKind); Assert.Null(clean.Ahead);
        await File.AppendAllTextAsync(Path.Combine(root, "keep.txt"), "unstaged\n");
        Assert.Equal(1, (await Read()).Unstaged);
        await TestWorkspace.GitAsync(root, "add", "--", "keep.txt");
        await File.AppendAllTextAsync(Path.Combine(root, "keep.txt"), "another edit\n");
        await File.WriteAllTextAsync(Path.Combine(root, "未跟踪 文件.txt"), "untracked");
        var mixed = await Read(); Assert.Equal(1, mixed.Staged); Assert.Equal(1, mixed.Unstaged); Assert.Equal(1, mixed.Untracked);
        await TestWorkspace.CommitFixtureAsync(root);
        var renamed = Path.Combine(root, "renamed 中文.txt");
        Assert.StartsWith(area.Root, Path.GetFullPath(renamed));
        File.Move(Path.Combine(root, "keep.txt"), renamed);
        await TestWorkspace.GitAsync(root, "add", "-A");
        var rename = await Read(); Assert.Equal(2, rename.Staged); // rename plus the formerly untracked file
        var blob = (await TestWorkspace.GitAsync(root, "hash-object", "-w", "--", "renamed 中文.txt")).Trim();
        await TestWorkspace.GitInputAsync(root, $"0 {new string('0', 40)}\trenamed 中文.txt\n100644 {blob} 1\trenamed 中文.txt\n100644 {blob} 2\trenamed 中文.txt\n100644 {blob} 3\trenamed 中文.txt\n", "update-index", "--index-info");
        Assert.Equal(1, (await Read()).Conflicts);
        Assert.Empty((await TestWorkspace.GitAsync(root, "for-each-ref", "--format=%(refname)", "refs/heads")).Trim());
    }

    [Fact]
    public async Task LinkedWorktreeAndDeletedGitDirectoryCannotFallThroughToTheSourceRepository()
    {
        var area = new TestWorkspace(); var root = await area.CreateDetachedRepositoryAsync("main fixture");
        var linked = await area.CreateLinkedWorktreeAsync(root, "linked");
        var repo = await Record(linked); var checker = new GitStatusChecker(new GitProcess());
        var result = await checker.CheckAsync(repo, default);
        Assert.Equal(StatusError.None, result.Error); Assert.False(result.Data!.IsDirty);
        var missing = repo with { GitDir = Path.Combine(area.Root, "missing-admin") };
        Assert.Equal(StatusError.NotRepository, (await checker.CheckAsync(missing, default)).Error);
        Assert.Equal(StatusError.DirectoryUnavailable, (await checker.CheckAsync(repo with { WorktreeRoot = Path.Combine(area.Root, "missing-root") }, default)).Error);
        var noGit = new GitStatusChecker(new GitProcess(Path.Combine(area.Root, "missing-git.exe")));
        Assert.Equal(StatusError.GitUnavailable, (await noGit.CheckAsync(repo, default)).Error);
    }

    [Theory]
    [InlineData("fatal: dubious ownership", StatusError.AccessDenied)]
    [InlineData("fatal: Permission denied", StatusError.AccessDenied)]
    [InlineData("fatal: not a git repository", StatusError.NotRepository)]
    [InlineData("private diagnostic with credentials", StatusError.Failed)]
    public async Task FailuresAreClassifiedWithoutLeakingRawDiagnostic(string error, StatusError expected)
    {
        var area = new TestWorkspace(); var fake = new FakeGit(new(128, "", error));
        var result = await new GitStatusChecker(fake).CheckAsync(new(Guid.NewGuid(), area.Root, area.Root, area.Root, "test", null, DateTimeOffset.UtcNow), default);
        Assert.Equal(expected, result.Error); Assert.DoesNotContain(error, result.Message!);
        Assert.Contains("--no-optional-locks", fake.Arguments!); Assert.Contains("core.fsmonitor=false", fake.Arguments!);
        Assert.Contains("-z", fake.Arguments!); Assert.DoesNotContain("fetch", fake.Arguments!);
    }

    [Fact]
    public async Task CorruptStatusIsAnErrorNotClean()
    {
        var area = new TestWorkspace(); var checker = new GitStatusChecker(new FakeGit(new(0, "truncated", "")));
        var result = await checker.CheckAsync(new(Guid.NewGuid(), area.Root, area.Root, area.Root, "test", null, DateTimeOffset.UtcNow), default);
        Assert.Equal(StatusError.InvalidOutput, result.Error); Assert.Null(result.Data);
    }

    [Fact]
    public async Task WatchersObserveWorktreeAndSharedMetadataAndRecoverByPollingWhenUnavailable()
    {
        var area = new TestWorkspace(); var root = await area.CreateDetachedRepositoryAsync("base");
        var linked = await area.CreateLinkedWorktreeAsync(root, "linked"); var repo = await Record(linked);
        var changes = 0;
        using (var watch = new RepositoryWatchFactory().Watch(repo, () => Interlocked.Increment(ref changes)))
        {
            Assert.True(watch.IsActive);
            await File.WriteAllTextAsync(Path.Combine(linked, "edit.txt"), "changed");
            await Until(() => Volatile.Read(ref changes) > 0);
            var count = Volatile.Read(ref changes);
            await File.AppendAllTextAsync(Path.Combine(root, ".git", "config"), "\n# fixture metadata change\n");
            await Until(() => Volatile.Read(ref changes) > count);
        }
        using var missing = new RepositoryWatchFactory().Watch(repo with { WorktreeRoot = Path.Combine(area.Root, "gone"), GitDir = Path.Combine(area.Root, "gone-admin"), CommonDir = Path.Combine(area.Root, "gone-common") }, () => { });
        Assert.False(missing.IsActive);
    }

    [Fact]
    public async Task EventDrivenSchedulerUpdatesRealStatusWithoutUiOrRemoteCalls()
    {
        var area = new TestWorkspace(); var root = await area.CreateDetachedRepositoryAsync("watched");
        await TestWorkspace.CommitFixtureAsync(root); var repo = await Record(root);
        await using var scheduler = new LocalStatusScheduler(new GitStatusChecker(new GitProcess()), new RepositoryWatchFactory(), options: new() { Interval = TimeSpan.FromMinutes(5) });
        scheduler.SetRepositories([repo]); await Until(() => scheduler.Snapshot[repo.Id].Data is not null);
        Assert.False(scheduler.Snapshot[repo.Id].Data!.IsDirty);
        await File.AppendAllTextAsync(Path.Combine(root, "keep.txt"), "automatic update\n");
        await Until(() => scheduler.Snapshot[repo.Id].Data!.IsDirty);
        Assert.Equal(1, scheduler.Snapshot[repo.Id].Data!.Unstaged);
    }

    private static async Task<TrackedRepository> Record(string root)
    {
        var location = await new GitRepositoryLocator(new GitProcess()).LocateAsync(root);
        return new(Guid.NewGuid(), location.WorktreeRoot, location.GitDir, location.CommonDir, "test", null, DateTimeOffset.UtcNow);
    }
    private static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(path => path)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!predicate()) await Task.Delay(40, timeout.Token);
    }
    private sealed class FakeGit(GitResult result) : IGitProcess
    {
        public IReadOnlyList<string>? Arguments;
        public Task<GitResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) { Arguments = arguments; return Task.FromResult(result); }
    }
}

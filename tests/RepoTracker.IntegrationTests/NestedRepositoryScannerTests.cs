using RepoTracker.Core.Tracking;
using RepoTracker.Infrastructure.Git;
using RepoTracker.Tests.Shared;

namespace RepoTracker.IntegrationTests;

public sealed class NestedRepositoryScannerTests
{
    [Fact]
    public async Task FindsNestedRootsAndGitFilesButSkipsOrdinaryFoldersAndMetadataWithoutWrites()
    {
        var area = new TestWorkspace();
        var parent = await area.CreateDetachedRepositoryAsync("parent 中文");
        var child = await area.CreateDetachedRepositoryAsync(Path.Combine("parent 中文", "packages", "child"));
        var grandchild = await area.CreateDetachedRepositoryAsync(Path.Combine("parent 中文", "packages", "child", "grandchild"));
        var linked = await area.CreateLinkedWorktreeAsync(parent, Path.Combine("parent 中文", "linked"));
        var module = await area.CreateDetachedRepositoryAsync(Path.Combine("parent 中文", "initialized-module"));
        var moduleSource = Path.GetFullPath(Path.Combine(module, ".git"));
        var moduleTarget = Path.GetFullPath(Path.Combine(parent, ".git", "modules", "initialized-module"));
        Assert.StartsWith(area.Root + Path.DirectorySeparatorChar, moduleSource, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(area.Root + Path.DirectorySeparatorChar, moduleTarget, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.GetDirectoryName(moduleTarget)!);
        Directory.Move(moduleSource, moduleTarget);
        await File.WriteAllTextAsync(moduleSource, "gitdir: " + moduleTarget.Replace('\\', '/') + "\n");
        Directory.CreateDirectory(Path.Combine(parent, "ordinary", "uninitialized-module"));
        await area.CreateDetachedRepositoryAsync(Path.Combine("parent 中文", ".git", "ignored"));
        var before = Snapshot(parent);
        var result = await new NestedRepositoryScanner(new GitRepositoryLocator(new GitProcess())).ScanAsync(parent);
        Assert.Equal(new[] { parent, child, grandchild, linked, module }.Order(), result.Repositories.Select(repo => repo.WorktreeRoot).Order());
        Assert.Empty(result.Issues); Assert.False(result.LimitReached); Assert.Equal(0, result.SkippedLinks);
        Assert.Equal(before, Snapshot(parent));
    }

    [Fact]
    public async Task InvalidMarkersAreReportedWhileOtherChildrenAreStillFound()
    {
        var area = new TestWorkspace();
        var parent = await area.CreateDetachedRepositoryAsync("parent");
        var child = await area.CreateDetachedRepositoryAsync(Path.Combine("parent", "valid"));
        var broken = Directory.CreateDirectory(Path.Combine(parent, "broken")).FullName;
        await File.WriteAllTextAsync(Path.Combine(broken, ".git"), "gitdir: missing\n");
        var result = await new NestedRepositoryScanner(new GitRepositoryLocator(new GitProcess())).ScanAsync(parent);
        Assert.Equal(2, result.Repositories.Count); Assert.Contains(result.Repositories, repo => repo.WorktreeRoot == child);
        Assert.Equal(broken, Assert.Single(result.Issues).Path);
    }

    [Fact]
    public async Task DirectoryAndRepositoryLimitsAreExplicitAndCancellationStopsScanning()
    {
        var area = new TestWorkspace();
        var parent = await area.CreateDetachedRepositoryAsync("parent");
        await area.CreateDetachedRepositoryAsync(Path.Combine("parent", "child"));
        var locator = new GitRepositoryLocator(new GitProcess());
        var limited = await new NestedRepositoryScanner(locator, directoryLimit: 1).ScanAsync(parent);
        Assert.True(limited.LimitReached); Assert.Equal(1, limited.VisitedDirectories); Assert.Single(limited.Repositories);
        limited = await new NestedRepositoryScanner(locator, repositoryLimit: 1).ScanAsync(parent);
        Assert.True(limited.LimitReached); Assert.Single(limited.Repositories);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NestedRepositoryScanner(locator).ScanAsync(parent, cancellation.Token));
    }

    [Fact]
    public async Task JunctionsToOutsideAndBackToRootAreSkipped()
    {
        var area = new TestWorkspace();
        var parent = await area.CreateDetachedRepositoryAsync("parent");
        var outside = await area.CreateDetachedRepositoryAsync("outside");
        foreach (var (name, target) in new[] { ("loop", parent), ("external", outside) })
        {
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("New-Item -ItemType Junction -Path $env:REPOTRACKER_TEST_ALIAS -Target $env:REPOTRACKER_TEST_TARGET | Out-Null");
            start.Environment["REPOTRACKER_TEST_ALIAS"] = Path.Combine(parent, name);
            start.Environment["REPOTRACKER_TEST_TARGET"] = target;
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, process.ExitCode);
        }
        var result = await new NestedRepositoryScanner(new GitRepositoryLocator(new GitProcess())).ScanAsync(parent);
        Assert.Single(result.Repositories); Assert.Equal(2, result.SkippedLinks); Assert.False(result.LimitReached);
    }

    [Fact]
    public async Task TimeLimitPreservesDiscoveredRootsAndReportsPartialProgress()
    {
        var area = new TestWorkspace();
        var parent = await area.CreateDetachedRepositoryAsync("parent");
        await area.CreateDetachedRepositoryAsync(Path.Combine("parent", "child"));
        var progress = new CapturedProgress();
        var scanner = new NestedRepositoryScanner(new GitRepositoryLocator(new GitProcess()), timeLimit: TimeSpan.FromTicks(1));
        var result = await scanner.ScanAsync(parent, progress: progress);
        Assert.True(result.LimitReached); Assert.True(result.TimeLimitReached);
        Assert.Equal(parent, Assert.Single(result.Repositories).WorktreeRoot);
        Assert.Equal(0, result.VisitedDirectories);
        Assert.NotEmpty(progress.Values);
        Assert.All(progress.Values, value => Assert.Equal(new RepositoryScanProgress(0, 1), value));
    }

    private sealed class CapturedProgress : IProgress<RepositoryScanProgress>
    {
        public List<RepositoryScanProgress> Values { get; } = [];
        public void Report(RepositoryScanProgress value) => Values.Add(value);
    }

    private static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(file => Path.GetRelativePath(root, file) + ":" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)))).ToArray();
}

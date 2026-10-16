using RepoTracker.Core.Tracking;
using RepoTracker.Infrastructure.Configuration;
using RepoTracker.Infrastructure.Git;
using RepoTracker.Tests.Shared;

namespace RepoTracker.IntegrationTests;

public sealed class RepositoryDiscoveryTests
{
    [Fact]
    public async Task RealGitHandlesChineseSpacesSubdirectoriesAndLinkedWorktreesWithoutWritingMetadata()
    {
        var area = new TestWorkspace(); var repository = await area.CreateDetachedRepositoryAsync("中文 repository");
        var linked = await area.CreateLinkedWorktreeAsync(repository, "linked 工作树");
        var subdirectory = Directory.CreateDirectory(Path.Combine(repository, "nested folder")).FullName;
        var before = Snapshot(area.Root);
        var locator = new GitRepositoryLocator(new GitProcess());
        var root = await locator.LocateAsync(subdirectory); var worktree = await locator.LocateAsync(linked);
        Assert.Equal(repository, root.WorktreeRoot, ignoreCase: true);
        Assert.Equal(linked, worktree.WorktreeRoot, ignoreCase: true);
        Assert.Equal(root.CommonDir, worktree.CommonDir, ignoreCase: true); Assert.NotEqual(root.GitDir, worktree.GitDir);
        Assert.Equal(before, Snapshot(area.Root));
        Assert.Empty((await TestWorkspace.GitAsync(repository, "for-each-ref", "--format=%(refname)", "refs/heads")).Trim());

        var store = new JsonConfigurationStore(Path.Combine(area.Root, "profile"));
        var registry = new RepositoryRegistry(store, locator, new NoFolders()); await registry.InitializeAsync();
        var first = await registry.AddAsync(repository, null);
        Assert.True((await registry.AddAsync(subdirectory, null)).AlreadyTracked);
        await registry.AddAsync(linked, null); Assert.Equal(2, registry.State.Repositories.Length);
        var repoBytes = Snapshot(repository);
        await registry.RemoveAsync(first.Repository.Id); await registry.UndoRemoveAsync();
        Assert.Equal(repoBytes, Snapshot(repository));
    }

    [Fact]
    public async Task JunctionAliasResolvesToTheSameWorktreeIdentity()
    {
        var area = new TestWorkspace(); var repository = await area.CreateDetachedRepositoryAsync("original");
        var alias = Path.Combine(area.Root, "alias");
        // Directory junctions do not require developer-mode symbolic-link privileges.
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("New-Item -ItemType Junction -Path $env:REPOTRACKER_TEST_ALIAS -Target $env:REPOTRACKER_TEST_TARGET | Out-Null");
        start.Environment["REPOTRACKER_TEST_ALIAS"] = alias; start.Environment["REPOTRACKER_TEST_TARGET"] = repository;
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, process.ExitCode);
        var locator = new GitRepositoryLocator(new GitProcess());
        Assert.Equal((await locator.LocateAsync(repository)).WorktreeRoot, (await locator.LocateAsync(alias)).WorktreeRoot);
    }

    [Fact]
    public async Task MissingGitMissingDirectoryAndNonRepositoryLeaveRegistryEmpty()
    {
        var area = new TestWorkspace();
        var missingGit = new GitRepositoryLocator(new GitProcess(Path.Combine(area.Root, "missing-git.exe")));
        await Assert.ThrowsAsync<GitExecutionException>(() => missingGit.LocateAsync(area.Root));
        var locator = new GitRepositoryLocator(new GitProcess());
        await Assert.ThrowsAsync<TrackerException>(() => locator.LocateAsync(Path.Combine(area.Root, "missing")));
        // A directory inside this source tree would find the real parent repository; use an isolated invalid .git boundary.
        await File.WriteAllTextAsync(Path.Combine(area.Root, ".git"), "gitdir: " + Path.Combine(area.Root, "missing-git-directory") + "\n");
        await Assert.ThrowsAsync<TrackerException>(() => locator.LocateAsync(area.Root));
        var git = new RejectGit();
        var registry = new RepositoryRegistry(new JsonConfigurationStore(Path.Combine(area.Root, "profile")), new GitRepositoryLocator(git), new NoFolders());
        await registry.InitializeAsync();
        await Assert.ThrowsAsync<TrackerException>(() => registry.AddAsync(area.Root, null));
        Assert.Empty(registry.State.Repositories); Assert.False(File.Exists(Path.Combine(area.Root, "profile", "state.json")));
        Assert.Contains("--no-optional-locks", git.Arguments!); Assert.Equal(area.Root, git.Arguments![4]);
        Assert.Contains("--path-format=absolute", git.Arguments);
    }

    private static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(file => file, StringComparer.Ordinal).Select(file => Path.GetRelativePath(root, file) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)))).ToArray();
    private sealed class NoFolders : IFolderLauncher { public void Open(string path) => throw new InvalidOperationException("Not used by test"); }
    private sealed class RejectGit : IGitProcess
    {
        public IReadOnlyList<string>? Arguments;
        public Task<GitResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        { Arguments = arguments; return Task.FromResult(new GitResult(128, "", "not a repository")); }
    }
}

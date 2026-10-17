using System.Diagnostics;
using System.IO;
using System.Text.Json;
using RepoTracker.Core.Tracking;
using RepoTracker.Infrastructure.Configuration;
using RepoTracker.Infrastructure.Git;
using RepoTracker.Tests.Shared;

namespace RepoTracker.Desktop.TestHost.Performance;

internal static class PerformanceFixture
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string ValidateDirectory(string directory)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "RepoTracker.slnx"))) root = root.Parent;
        var allowed = Path.GetFullPath(Path.Combine(root?.FullName ?? throw new DirectoryNotFoundException("Solution not found"), "tests", "artifacts")) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(directory);
        if (!full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Performance output must be beneath tests/artifacts.");
        return full;
    }
    public static async Task CreateAsync(string directory, int port)
    {
        if (Directory.Exists(Path.Combine(directory, "repositories")) || Directory.Exists(Path.Combine(directory, "profile")))
            throw new IOException("Use a fresh output directory; existing fixtures are preserved.");
        Directory.CreateDirectory(directory);
        var area = new TestWorkspace(Path.Combine(directory, "repositories"));
        var repositories = new List<TrackedRepository>();
        var locator = new GitRepositoryLocator(new GitProcess());
        var group = new RepositoryGroup(Guid.NewGuid(), "Performance fixtures", 0);
        for (var index = 0; index < 20; index++)
        {
            var folder = await area.CreateDetachedRepositoryAsync($"repo-{index:00}");
            await File.AppendAllTextAsync(Path.Combine(folder, ".git", "config"), "\n[core]\nfsmonitor = false\n");
            for (var file = 0; file < 200; file++)
            {
                var subdirectory = Path.Combine(folder, "tracked", $"part-{file / 20:00}");
                Directory.CreateDirectory(subdirectory);
                await File.WriteAllTextAsync(Path.Combine(subdirectory, $"file-{file:000}.txt"),
                    $"repository {index:00} file {file:000}\n".PadRight(4096, 'x'));
            }
            await TestWorkspace.GitAsync(folder, "-c", "core.autocrlf=false", "add", "--", ".");
            var trackedCount = (await TestWorkspace.GitAsync(folder, "ls-files", "-z")).Split('\0', StringSplitOptions.RemoveEmptyEntries).Length;
            if (trackedCount != 201) throw new InvalidOperationException($"Fixture has {trackedCount} tracked files; expected 201. Check global Git excludes or attributes.");
            var tree = (await TestWorkspace.GitAsync(folder, "write-tree")).Trim();
            var commit = (await TestWorkspace.GitAsync(folder, "-c", "user.name=Performance fixture", "-c", "user.email=fixture@example.invalid",
                "commit-tree", tree, "-m", "fixed performance data")).Trim();
            await File.WriteAllTextAsync(Path.Combine(folder, ".git", "HEAD"), commit + "\n");
            var untracked = Path.Combine(folder, "untracked");
            Directory.CreateDirectory(untracked);
            for (var file = 0; file < 50; file++)
                await File.WriteAllTextAsync(Path.Combine(untracked, $"extra-{file:000}.txt"), new string('u', 1024));
            var untrackedCount = (await TestWorkspace.GitAsync(folder, "ls-files", "--others", "--exclude-standard", "-z"))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries).Length;
            if (untrackedCount != 50) throw new InvalidOperationException($"Fixture has {untrackedCount} untracked files; expected 50. Check global Git excludes.");
            // Endpoint is owned by this test process. No branch, upstream, real service or credential is created.
            await File.AppendAllTextAsync(Path.Combine(folder, ".git", "config"),
                $"\n[http]\nproxy =\n[remote \"origin\"]\nurl = https://127.0.0.1:{port}/fixture-{index:00}.git\nfetch = +refs/heads/*:refs/remotes/origin/*\n");
            var location = await locator.LocateAsync(folder, CancellationToken.None);
            repositories.Add(new(Guid.NewGuid(), location.WorktreeRoot, location.GitDir, location.CommonDir, $"repo-{index:00}", group.Id, DateTimeOffset.UtcNow));
            if (!string.IsNullOrWhiteSpace(await TestWorkspace.GitAsync(folder, "for-each-ref", "--format=%(refname)", "refs/heads")))
                throw new InvalidOperationException("Performance fixture created a named branch.");
        }
        var store = new JsonConfigurationStore(Path.Combine(directory, "profile"));
        var loaded = await store.LoadAsync();
        await store.SaveAsync(new() { Repositories = [.. repositories], Groups = [group],
            Preferences = new() { KeepExpanded = true, Language = "en" } }, loaded.Revision);
        var files = Directory.GetFiles(area.Root, "*", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar)).ToArray();
        await File.WriteAllTextAsync(Path.Combine(directory, "fixture.json"), JsonSerializer.Serialize(new
        {
            Repositories = 20, TrackedFilesPerRepository = 201, UntrackedFilesPerRepository = 50,
            WorktreeFiles = files.Length, WorktreeBytes = files.Sum(file => new FileInfo(file).Length),
            Head = "detached", Upstream = "none", LocalIntervalSeconds = 15, LocalConcurrency = 2,
            GitVersion = (await TestWorkspace.GitAsync(repositories[0].WorktreeRoot, "--version")).Trim(),
            Dataset = "200 tracked 4096-byte files + keep.txt; 50 untracked 1024-byte files per worktree",
            Remote = "test-owned loopback HTTPS endpoint closes each connection; failure workload only"
        }, Json));
    }
    public static async Task<int> WriteChangesAsync(string outputDirectory)
    {
        var directory = ValidateDirectory(outputDirectory);
        var watch = Stopwatch.StartNew();
        long batches = 0, writes = 0;
        var stop = Path.Combine(directory, "writer.stop");
        while (!File.Exists(stop) && watch.Elapsed < TimeSpan.FromMinutes(30))
        {
            var phase = Path.Combine(directory, "phase.txt");
            if (File.Exists(phase) && await ReadPhaseAsync(phase) == "churn")
            {
                for (var index = 0; index < 20; index++)
                    await File.AppendAllTextAsync(Path.Combine(directory, "repositories", $"repo-{index:00}", "tracked", "part-00", "file-000.txt"), "changed by external load generator\n");
                batches++; writes += 20;
            }
            await Task.Delay(200);
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "writer.json"), JsonSerializer.Serialize(new { Batches = batches, Writes = writes, ElapsedSeconds = watch.Elapsed.TotalSeconds }, Json));
        return File.Exists(stop) ? 0 : 1;
    }
    private static async Task<string> ReadPhaseAsync(string path)
    {
        // The controller atomically replaces this file; readers must allow deletion/rename on Windows.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}

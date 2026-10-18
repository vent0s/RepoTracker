using System.ComponentModel;
using System.Diagnostics;
using RepoTracker.Core.Localization;
using RepoTracker.Core.Tracking;

namespace RepoTracker.Infrastructure.Git;

/// <summary>Explicit, bounded discovery beneath one worktree. Does not follow directory links or enter Git metadata.</summary>
public sealed class NestedRepositoryScanner(IRepositoryLocator locator, int directoryLimit = 50000, int repositoryLimit = 500, TimeSpan? timeLimit = null)
    : IRepositoryScanner
{
    public Task<RepositoryScanResult> ScanAsync(string repositoryPath, CancellationToken cancellationToken = default,
        IProgress<RepositoryScanProgress>? progress = null)
        => Task.Run(async () =>
        {
            if (directoryLimit < 1 || repositoryLimit < 1) throw new ArgumentOutOfRangeException(nameof(directoryLimit));
            var budget = timeLimit ?? TimeSpan.FromSeconds(30);
            if (budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeLimit));
            var timer = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            var root = await locator.LocateAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
            var repositories = new List<RepositoryLocation> { root };
            var issues = new List<RepositoryScanIssue>();
            var pending = new Queue<string>();
            pending.Enqueue(root.WorktreeRoot);
            var visited = 0;
            var scheduled = 1;
            var skippedLinks = 0;
            var limited = false;
            var timedOut = false;
            progress?.Report(new(0, 1));
            while (pending.TryDequeue(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (timer.Elapsed >= budget) { limited = timedOut = true; break; }
                visited++;
                if (timer.Elapsed - lastReport >= TimeSpan.FromMilliseconds(250))
                {
                    progress?.Report(new(visited, repositories.Count));
                    lastReport = timer.Elapsed;
                }
                try
                {
                    // Check again on dequeue in case a directory became a junction during the scan.
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { skippedLinks++; continue; }
                    if (directory != root.WorktreeRoot && (File.Exists(Path.Combine(directory, ".git")) || Directory.Exists(Path.Combine(directory, ".git"))))
                    {
                        try
                        {
                            var found = await locator.LocateAsync(directory, cancellationToken).ConfigureAwait(false);
                            if (!string.Equals(found.WorktreeRoot, directory, StringComparison.OrdinalIgnoreCase))
                                issues.Add(new(directory, Text.Get("Repository.ScanOutside")));
                            else if (repositories.Count >= repositoryLimit) { limited = true; break; }
                            else repositories.Add(found);
                        }
                        catch (Exception error) when (IsScanError(error)) { issues.Add(new(directory, error.Message)); }
                    }
                    foreach (var child in new DirectoryInfo(directory).EnumerateDirectories())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (timer.Elapsed >= budget) { limited = timedOut = true; break; }
                        if (child.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
                        if ((child.Attributes & FileAttributes.ReparsePoint) != 0) { skippedLinks++; continue; }
                        if (scheduled >= directoryLimit) { limited = true; break; }
                        pending.Enqueue(child.FullName);
                        scheduled++;
                    }
                }
                catch (Exception error) when (IsScanError(error)) { issues.Add(new(directory, error.Message)); }
            }
            progress?.Report(new(visited, repositories.Count));
            return new RepositoryScanResult(repositories, issues, visited, skippedLinks, limited, timedOut);
        }, cancellationToken);

    private static bool IsScanError(Exception error) => error is TrackerException or IOException or UnauthorizedAccessException or Win32Exception;
}

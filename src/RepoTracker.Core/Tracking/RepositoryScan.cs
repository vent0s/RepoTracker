namespace RepoTracker.Core.Tracking;

public sealed record RepositoryScanIssue(string Path, string Message);

public sealed record RepositoryScanResult(IReadOnlyList<RepositoryLocation> Repositories,
    IReadOnlyList<RepositoryScanIssue> Issues, int VisitedDirectories, int SkippedLinks, bool LimitReached, bool TimeLimitReached = false);

public sealed record RepositoryScanProgress(int VisitedDirectories, int Repositories);

public interface IRepositoryScanner
{
    Task<RepositoryScanResult> ScanAsync(string repositoryPath, CancellationToken cancellationToken = default,
        IProgress<RepositoryScanProgress>? progress = null);
}

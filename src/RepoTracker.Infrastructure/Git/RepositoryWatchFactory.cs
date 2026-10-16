using RepoTracker.Core.Status;
using RepoTracker.Core.Tracking;

namespace RepoTracker.Infrastructure.Git;

public sealed class RepositoryWatchFactory : IRepositoryWatchFactory
{
    public IRepositoryWatch Watch(TrackedRepository repository, Action changed) => new WatchSet(repository, changed);
    private sealed class WatchSet : IRepositoryWatch
    {
        private readonly List<FileSystemWatcher> watchers = [];
        private bool healthy = true;
        public bool IsActive => healthy && watchers.Count > 0;
        public WatchSet(TrackedRepository repository, Action changed)
        {
            var roots = new[] { repository.WorktreeRoot, repository.GitDir, repository.CommonDir }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var root in roots.Where(root => !roots.Any(other => other != root && root.StartsWith(Path.TrimEndingDirectorySeparator(other) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
            {
                FileSystemWatcher? watcher = null;
                try
                {
                    watcher = new(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                    watcher.Changed += (_, e) => { if (!e.FullPath.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) changed(); };
                    watcher.Created += (_, _) => changed(); watcher.Deleted += (_, _) => changed(); watcher.Renamed += (_, _) => changed();
                    watcher.Error += (_, _) => { healthy = false; changed(); };
                    watcher.EnableRaisingEvents = true;
                    watchers.Add(watcher);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                { watcher?.Dispose(); healthy = false; }
            }
        }
        public void Dispose() { foreach (var watcher in watchers) watcher.Dispose(); watchers.Clear(); }
    }
}

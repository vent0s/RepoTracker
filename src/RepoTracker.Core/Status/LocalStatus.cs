using RepoTracker.Core.Tracking;
using RepoTracker.Core.Sources;

namespace RepoTracker.Core.Status;

public enum HeadKind { Branch, Detached, Unborn }
public enum StatusError { None, DirectoryUnavailable, NotRepository, GitUnavailable, Timeout, AccessDenied, InvalidOutput, Failed }
public sealed record GitStatus(HeadKind HeadKind, string? Branch, string? Commit, string? Upstream,
    int? Ahead, int? Behind, int Staged, int Unstaged, int Untracked, int Conflicts)
{
    public bool IsDirty => Staged + Unstaged + Untracked + Conflicts > 0;
}
public sealed record StatusResult(GitStatus? Data, StatusError Error = StatusError.None, string? Message = null, RemoteIdentity? Remote = null);
public sealed record StatusSnapshot(GitStatus? Data = null, StatusError Error = StatusError.None, string? Message = null,
    DateTimeOffset? CheckedAt = null, DateTimeOffset? SucceededAt = null, bool IsChecking = false, bool Watching = false, RemoteIdentity? Remote = null);
public interface ILocalStatusChecker
{
    Task<StatusResult> CheckAsync(TrackedRepository repository, CancellationToken cancellationToken);
}
public interface IRepositoryWatch : IDisposable { bool IsActive { get; } }
public interface IRepositoryWatchFactory
{
    IRepositoryWatch Watch(TrackedRepository repository, Action changed);
}

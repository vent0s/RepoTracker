namespace RepoTracker.Core.Tracking;

public sealed record ConfigurationSnapshot(TrackerState State, string Revision, bool CanWrite = true, string? Notice = null);

public interface IConfigurationStore
{
    Task<ConfigurationSnapshot> LoadAsync(CancellationToken cancellationToken = default);
    Task<string> SaveAsync(TrackerState state, string expectedRevision, CancellationToken cancellationToken = default);
}

public interface IRepositoryLocator
{
    Task<RepositoryLocation> LocateAsync(string path, CancellationToken cancellationToken = default);
}

public interface IFolderLauncher
{
    void Open(string path);
}

public sealed record AddRepositoryResult(TrackedRepository Repository, bool AlreadyTracked, bool GroupFellBack);

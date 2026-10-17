using RepoTracker.Core.Localization;
using System.Collections.Immutable;
using RepoTracker.Core.Sources;

namespace RepoTracker.Core.Tracking;

/// <summary>Publishes a new immutable snapshot only after its complete transaction has reached disk.</summary>
public sealed class RepositoryRegistry(IConfigurationStore store, IRepositoryLocator locator, IFolderLauncher folders)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string revision = "";
    private bool initialized;
    private TrackedRepository? removed;
    public TrackerState State { get; private set; } = new();
    public bool CanWrite { get; private set; }
    public bool CanUndo => removed is not null && CanWrite;
    public string? Notice { get; private set; }
    public event EventHandler? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (initialized) return;
            var snapshot = await store.LoadAsync(cancellationToken);
            State = snapshot.State;
            revision = snapshot.Revision;
            CanWrite = snapshot.CanWrite;
            Notice = snapshot.Notice;
            initialized = true;
        }
        finally { gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<Guid> CreateGroupAsync(string name, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        await ChangeAsync(state => state with
        {
            Groups = state.Groups.Add(new(id, GroupNames.ValidateUnique(name, state.Groups),
                state.Groups.IsEmpty ? 0 : checked(state.Groups.Max(group => group.Order) + 1)))
        }, cancellationToken);
        return id;
    }

    public Task RenameGroupAsync(Guid? id, string name, CancellationToken cancellationToken = default) =>
        ChangeAsync(state =>
        {
            var group = FindGroup(state, id);
            var renamed = group with { Name = GroupNames.ValidateUnique(name, state.Groups, id) };
            return state with { Groups = state.Groups.Replace(group, renamed) };
        }, cancellationToken);

    public Task DeleteGroupAsync(Guid? id, CancellationToken cancellationToken = default) =>
        ChangeAsync(state => state with
        {
            Groups = state.Groups.Remove(FindGroup(state, id)),
            Repositories = state.Repositories.Select(repo => repo.GroupId == id ? repo with { GroupId = null } : repo).ToImmutableArray()
        }, cancellationToken);

    public Task SetCollapsedAsync(Guid? id, bool collapsed, CancellationToken cancellationToken = default) =>
        ChangeAsync(state => id is null
            ? state with { UngroupedCollapsed = collapsed }
            : state with { Groups = state.Groups.Replace(FindGroup(state, id), FindGroup(state, id) with { Collapsed = collapsed }) }, cancellationToken);

    public async Task<AddRepositoryResult> AddAsync(string path, Guid? groupId, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        var location = await locator.LocateAsync(path, cancellationToken);
        AddRepositoryResult? result = null;
        await ChangeAsync(state =>
        {
            var existing = state.Repositories.FirstOrDefault(repo => SamePath(repo.WorktreeRoot, location.WorktreeRoot));
            if (existing is not null) { result = new(existing, true, false); return state; }
            var fellBack = groupId is not null && !state.Groups.Any(group => group.Id == groupId);
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(location.WorktreeRoot));
            var repo = new TrackedRepository(Guid.NewGuid(), location.WorktreeRoot, location.GitDir, location.CommonDir,
                string.IsNullOrEmpty(name) ? location.WorktreeRoot : name, fellBack ? null : groupId, DateTimeOffset.UtcNow);
            result = new(repo, false, fellBack);
            return state with { Repositories = state.Repositories.Add(repo) };
        }, cancellationToken);
        return result!;
    }

    public Task MoveAsync(Guid repositoryId, Guid? groupId, CancellationToken cancellationToken = default) =>
        ChangeAsync(state =>
        {
            if (groupId is not null) FindGroup(state, groupId);
            var repo = FindRepository(state, repositoryId);
            return state with { Repositories = state.Repositories.Replace(repo, repo with { GroupId = groupId }) };
        }, cancellationToken);

    public async Task RemoveAsync(Guid repositoryId, CancellationToken cancellationToken = default)
    {
        await ChangeAsync(state =>
        {
            var repo = FindRepository(state, repositoryId);
            return state with { Repositories = state.Repositories.Remove(repo) };
        }, cancellationToken, beforePublish: old => removed = FindRepository(old, repositoryId));
    }

    public Task UndoRemoveAsync(CancellationToken cancellationToken = default) =>
        ChangeAsync(state =>
        {
            var repo = removed ?? throw new TrackerException(Text.Get("Repository.NoUndo"));
            if (state.Repositories.Any(item => SamePath(item.WorktreeRoot, repo.WorktreeRoot)))
                throw new TrackerException(Text.Get("Repository.AlreadyRestored"));
            if (repo.GroupId is not null && !state.Groups.Any(group => group.Id == repo.GroupId)) repo = repo with { GroupId = null };
            return state with { Repositories = state.Repositories.Add(repo) };
        }, cancellationToken, beforePublish: _ => removed = null);

    public void OpenFolder(Guid id) => folders.Open(FindRepository(State, id).WorktreeRoot);

    public Task SetRemoteEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        ChangeAsync(state => state with { RemoteEnabled = enabled }, cancellationToken);

    public Task UpdatePreferencesAsync(Func<AppPreferences, AppPreferences> update, CancellationToken cancellationToken = default) =>
        ChangeAsync(state => state with { Preferences = update(state.Preferences).Validate() }, cancellationToken);

    public Task SaveSourceAsync(SourceRule rule, CancellationToken cancellationToken = default) =>
        ChangeAsync(state =>
        {
            var validated = SourceRules.Validate(rule, state.Sources);
            var previous = state.Sources.FirstOrDefault(item => item.Id == rule.Id);
            return state with { Sources = previous is null ? state.Sources.Add(validated) : state.Sources.Replace(previous, validated) };
        }, cancellationToken);

    public Task DeleteSourceAsync(Guid id, CancellationToken cancellationToken = default) =>
        ChangeAsync(state => state with { Sources = state.Sources.Where(rule => rule.Id != id).ToImmutableArray() }, cancellationToken);

    private async Task ChangeAsync(Func<TrackerState, TrackerState> change, CancellationToken cancellationToken,
        Action<TrackerState>? beforePublish = null)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureWritable();
            var next = change(State);
            if (ReferenceEquals(next, State)) return;
            var nextRevision = await store.SaveAsync(next, revision, cancellationToken);
            beforePublish?.Invoke(State);
            State = next;
            revision = nextRevision;
        }
        finally { gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureWritable()
    {
        if (!initialized) throw new TrackerException(Text.Get("Config.Loading"));
        if (!CanWrite) throw new TrackerException(Notice ?? Text.Get("Config.ReadOnly"));
    }

    private static RepositoryGroup FindGroup(TrackerState state, Guid? id) => id is null
        ? throw new TrackerException(Text.Get("Group.SystemProtected"))
        : state.Groups.FirstOrDefault(group => group.Id == id) ?? throw new TrackerException(Text.Get("Group.Missing"));
    private static TrackedRepository FindRepository(TrackerState state, Guid id) =>
        state.Repositories.FirstOrDefault(repo => repo.Id == id) ?? throw new TrackerException(Text.Get("Repository.Missing"));
    private static bool SamePath(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}

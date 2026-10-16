using System.Collections.Immutable;
using RepoTracker.Core.Tracking;

namespace RepoTracker.Core.Tests;

public sealed class RepositoryRegistryTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("未分组")]
    [InlineData("a\nb")]
    [InlineData("1234567890123456789012345678901")]
    public void InvalidGroupNamesAreRejected(string name) => Assert.Throws<TrackerException>(() => GroupNames.Normalize(name));

    [Fact]
    public void NamesUseNormalizedTextElementsAndCaseInsensitiveUniqueness()
    {
        Assert.Equal("é", GroupNames.Normalize(" e\u0301 "));
        var emoji = string.Concat(Enumerable.Repeat("👩‍💻", 30));
        Assert.Equal(emoji, GroupNames.Normalize(emoji));
        Assert.Throws<TrackerException>(() => GroupNames.Normalize(emoji + "a"));
        Assert.Throws<TrackerException>(() => GroupNames.ValidateUnique("WORK", [new(Guid.NewGuid(), "Work", 0)]));
        Assert.Throws<TrackerException>(() => GroupNames.ValidateUnique("e\u0301", [new(Guid.NewGuid(), "é", 0)]));
    }

    [Fact]
    public async Task RenameMoveAndDeleteKeepRepositoryIdentityAndMembershipUnique()
    {
        var work = Guid.NewGuid(); var personal = Guid.NewGuid();
        var repos = Enumerable.Range(0, 6).Select(i => Repo(i, i < 3 ? work : i < 5 ? personal : null)).ToImmutableArray();
        var store = new MemoryStore(new() { Repositories = repos, Groups = [new(work, "工作", 0), new(personal, "个人", 1)] });
        var registry = await Create(store);
        await registry.RenameGroupAsync(work, " 项目 ");
        Assert.Equal(work, registry.State.Groups[0].Id);
        Assert.Equal("项目", registry.State.Groups[0].Name);
        await registry.MoveAsync(repos[0].Id, personal);
        Assert.Equal(1, registry.State.Repositories.Count(repo => repo.Id == repos[0].Id));
        await registry.MoveAsync(repos[0].Id, work);
        await registry.DeleteGroupAsync(personal);
        Assert.Equal(3, registry.State.Repositories.Count(repo => repo.GroupId == work));
        Assert.Equal(3, registry.State.Repositories.Count(repo => repo.GroupId is null));
        Assert.Equal(repos.Select(repo => repo.Id), registry.State.Repositories.Select(repo => repo.Id));
        Assert.Equal(repos.Select(repo => repo.WorktreeRoot), registry.State.Repositories.Select(repo => repo.WorktreeRoot));
    }

    [Fact]
    public async Task SaveFailureDoesNotPublishHalfDeletedGroupOrUndoRecord()
    {
        var group = Guid.NewGuid(); var repo = Repo(0, group);
        var store = new MemoryStore(new() { Groups = [new(group, "工作", 0)], Repositories = [repo] });
        var registry = await Create(store); var original = registry.State;
        store.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => registry.DeleteGroupAsync(group));
        Assert.Same(original, registry.State);
        await Assert.ThrowsAsync<IOException>(() => registry.RemoveAsync(repo.Id));
        Assert.False(registry.CanUndo);
        Assert.Same(original, store.State);
    }

    [Fact]
    public async Task RootsDeduplicateButWorktreesWithCommonGitDirectoryStaySeparate()
    {
        var registry = await Create(new MemoryStore(new()));
        var first = await registry.AddAsync(@"E:\fixtures\repo", null);
        var duplicate = await registry.AddAsync(@"e:\FIXTURES\REPO", null);
        var linked = await registry.AddAsync(@"E:\fixtures\linked", null);
        Assert.True(duplicate.AlreadyTracked);
        Assert.Equal(first.Repository.Id, duplicate.Repository.Id);
        Assert.NotEqual(first.Repository.Id, linked.Repository.Id);
        Assert.Equal(first.Repository.CommonDir, linked.Repository.CommonDir);
        Assert.Equal(2, registry.State.Repositories.Length);
    }

    [Fact]
    public async Task AddFallsBackIfSelectedGroupIsDeletedDuringDiscovery()
    {
        var discovered = new TaskCompletionSource<RepositoryLocation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = await Create(new MemoryStore(new()), new Locator(_ => discovered.Task));
        var group = await registry.CreateGroupAsync("工作");
        var add = registry.AddAsync(@"E:\fixtures\repo", group);
        await registry.DeleteGroupAsync(group);
        discovered.SetResult(new(@"E:\fixtures\repo", @"E:\fixtures\repo\.git", @"E:\fixtures\repo\.git"));
        var result = await add;
        Assert.True(result.GroupFellBack);
        Assert.Null(Assert.Single(registry.State.Repositories).GroupId);
    }

    [Fact]
    public async Task UndoPreservesIdAndFallsBackWhenOriginalGroupIsGone()
    {
        var group = Guid.NewGuid(); var repo = Repo(1, group);
        var registry = await Create(new MemoryStore(new() { Groups = [new(group, "个人", 0)], Repositories = [repo] }));
        await registry.RemoveAsync(repo.Id);
        await registry.DeleteGroupAsync(group);
        await registry.UndoRemoveAsync();
        var restored = Assert.Single(registry.State.Repositories);
        Assert.Equal(repo.Id, restored.Id); Assert.Null(restored.GroupId); Assert.False(registry.CanUndo);
    }

    [Fact]
    public async Task UngroupedIsProtectedAndEmptyGroupsAndCollapseSurviveReload()
    {
        var store = new MemoryStore(new()); var registry = await Create(store);
        await Assert.ThrowsAsync<TrackerException>(() => registry.DeleteGroupAsync(null));
        await Assert.ThrowsAsync<TrackerException>(() => registry.RenameGroupAsync(null, "组"));
        var empty = await registry.CreateGroupAsync("空组");
        await registry.SetCollapsedAsync(empty, true); await registry.SetCollapsedAsync(null, true);
        var reloaded = await Create(store);
        Assert.True(Assert.Single(reloaded.State.Groups).Collapsed);
        Assert.True(reloaded.State.UngroupedCollapsed); Assert.Empty(reloaded.State.Repositories);
    }

    [Fact]
    public async Task ParallelChangesAreSerializedWithoutLosingGroups()
    {
        var registry = await Create(new MemoryStore(new()));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(index => registry.CreateGroupAsync("Group " + index)));
        Assert.Equal(20, registry.State.Groups.Length);
        Assert.Equal(20, registry.State.Groups.Select(group => group.Order).Distinct().Count());
    }

    [Fact]
    public async Task OpeningFolderTargetsOnlyStoredRootAndCannotMutateState()
    {
        var repo = Repo(2, null); var folders = new Folders();
        var registry = new RepositoryRegistry(new MemoryStore(new() { Repositories = [repo] }), new Locator(), folders);
        await registry.InitializeAsync();
        var original = registry.State; registry.OpenFolder(repo.Id);
        Assert.Equal(repo.WorktreeRoot, folders.Opened); Assert.Same(original, registry.State);
    }

    [Fact]
    public async Task PreferenceMutationsSerializeAndPublishOnlyAfterDurableSave()
    {
        var store = new MemoryStore(new()); var registry = await Create(store);
        await Task.WhenAll(registry.UpdatePreferencesAsync(p => p with { KeepExpanded = true }),
            registry.UpdatePreferencesAsync(p => p with { Theme = AppTheme.Light, LocalIntervalSeconds = 30 }));
        Assert.True(registry.State.Preferences.KeepExpanded); Assert.Equal(AppTheme.Light, registry.State.Preferences.Theme);
        var before = registry.State;
        await Assert.ThrowsAsync<TrackerException>(() => registry.UpdatePreferencesAsync(p => p with { LocalConcurrency = 0 }));
        store.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => registry.UpdatePreferencesAsync(p => p with { ScalePercent = 150 }));
        Assert.Same(before, registry.State); Assert.Same(before, store.State);
        Assert.Equal(before.Preferences, (await Create(store)).State.Preferences);
    }

    private static TrackedRepository Repo(int number, Guid? group) => new(Guid.NewGuid(), @"E:\fixtures\repo" + number,
        @"E:\fixtures\repo" + number + @"\.git", @"E:\fixtures\repo" + number + @"\.git", "repo" + number, group, DateTimeOffset.UnixEpoch);
    private static async Task<RepositoryRegistry> Create(MemoryStore store, Locator? locator = null)
    {
        var registry = new RepositoryRegistry(store, locator ?? new Locator(), new Folders());
        await registry.InitializeAsync(); return registry;
    }
    private sealed class Locator(Func<string, Task<RepositoryLocation>>? locate = null) : IRepositoryLocator
    {
        public Task<RepositoryLocation> LocateAsync(string path, CancellationToken cancellationToken = default) =>
            locate?.Invoke(path) ?? Task.FromResult(new RepositoryLocation(path, path + @"\.git", @"E:\fixtures\repo\.git"));
    }
    private sealed class Folders : IFolderLauncher { public string? Opened; public void Open(string path) => Opened = path; }
    private sealed class MemoryStore(TrackerState initial) : IConfigurationStore
    {
        private int revision;
        public TrackerState State = initial;
        public bool Fail;
        public Task<ConfigurationSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ConfigurationSnapshot(State, revision.ToString()));
        public async Task<string> SaveAsync(TrackerState state, string expectedRevision, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (Fail) throw new IOException("Injected write failure");
            Assert.Equal(revision.ToString(), expectedRevision); State = state; return (++revision).ToString();
        }
    }
}

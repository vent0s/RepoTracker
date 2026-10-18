using System.Text.Json;
using System.Text.Json.Nodes;
using RepoTracker.Core.Tracking;
using RepoTracker.Infrastructure.Configuration;
using RepoTracker.Tests.Shared;

namespace RepoTracker.IntegrationTests;

public sealed class ConfigurationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static TrackerState Sample(string name = "工作")
    {
        var group = Guid.NewGuid();
        return new() { Groups = [new(group, name, 0, true)], UngroupedCollapsed = true,
            Repositories = [new(Guid.NewGuid(), @"E:\fixtures\repo", @"E:\fixtures\repo\.git", @"E:\fixtures\repo\.git", "repo", group, DateTimeOffset.UnixEpoch)] };
    }

    [Fact]
    public async Task AtomicRoundtripPreservesRepositoryGroupAndCollapsedState()
    {
        var area = new TestWorkspace(); var store = new JsonConfigurationStore(area.Root);
        var empty = await store.LoadAsync(); Assert.True(empty.CanWrite); Assert.Empty(empty.State.Repositories);
        var state = Sample(); await store.SaveAsync(state, empty.Revision);
        var loaded = await new JsonConfigurationStore(area.Root).LoadAsync();
        Assert.Equal(state.Repositories.ToArray(), loaded.State.Repositories.ToArray());
        Assert.Equal(state.Groups.ToArray(), loaded.State.Groups.ToArray()); Assert.True(loaded.State.UngroupedCollapsed);
        Assert.Empty(Directory.GetFiles(area.Root, "*.tmp"));
    }

    [Fact]
    public async Task RepositoryExpansionRoundtripsAndOlderProfilesDefaultToExpanded()
    {
        var area = new TestWorkspace(); var state = Sample();
        var json = JsonSerializer.SerializeToNode(state, Json)!;
        json["repositories"]![0]!.AsObject().Remove("childrenCollapsed");
        await File.WriteAllTextAsync(Path.Combine(area.Root, "state.json"), json.ToJsonString());
        var store = new JsonConfigurationStore(area.Root); var loaded = await store.LoadAsync();
        Assert.False(loaded.State.Repositories[0].ChildrenCollapsed);
        var collapsed = loaded.State with { Repositories = [loaded.State.Repositories[0] with { ChildrenCollapsed = true }] };
        await store.SaveAsync(collapsed, loaded.Revision);
        var reloaded = await new JsonConfigurationStore(area.Root).LoadAsync();
        Assert.True(reloaded.State.Repositories[0].ChildrenCollapsed);
        Assert.Equal(4, reloaded.State.SchemaVersion);
        Assert.Equal(collapsed.Repositories[0], reloaded.State.Repositories[0]);
    }

    [Fact]
    public async Task LegacyWithoutGroupsAndUnknownGroupIdFallBackWithoutLosingRepositories()
    {
        var area = new TestWorkspace(); var state = Sample();
        var legacy = JsonSerializer.SerializeToNode(state, Json)!;
        legacy["schemaVersion"] = 1; legacy.AsObject().Remove("groups");
        await File.WriteAllTextAsync(Path.Combine(area.Root, "state.json"), legacy.ToJsonString());
        var store = new JsonConfigurationStore(area.Root); var loaded = await store.LoadAsync();
        Assert.True(loaded.CanWrite); Assert.Equal(4, loaded.State.SchemaVersion);
        Assert.Null(Assert.Single(loaded.State.Repositories).GroupId);
        Assert.Equal(state.Repositories[0].Id, loaded.State.Repositories[0].Id);
        await store.SaveAsync(loaded.State, loaded.Revision);
        Assert.Contains("\"schemaVersion\": 4", await File.ReadAllTextAsync(Path.Combine(area.Root, "state.json")));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"schemaVersion\":\"two\",\"repositories\":[]}")]
    [InlineData("{\"schemaVersion\":2,\"repositories\":null}")]
    [InlineData("{\"schemaVersion\":2,\"repositories\":[],\"groups\":null}")]
    [InlineData("[]")]
    public async Task CorruptionIsPreservedBeforeAnEditableEmptyState(string bytes)
    {
        var area = new TestWorkspace(); var path = Path.Combine(area.Root, "state.json");
        await File.WriteAllTextAsync(path, bytes);
        var store = new JsonConfigurationStore(area.Root); var loaded = await store.LoadAsync();
        Assert.True(loaded.CanWrite); Assert.NotNull(loaded.Notice); Assert.Empty(loaded.State.Repositories);
        Assert.Equal(bytes, await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(area.Root, "state.corrupt-*.json"))));
        Assert.Equal(bytes, await File.ReadAllTextAsync(path));
        await store.SaveAsync(Sample(), loaded.Revision);
        Assert.Single((await new JsonConfigurationStore(area.Root).LoadAsync()).State.Repositories);
    }

    [Fact]
    public async Task ValidBackupRecoversAndNeverGetsReplacedWithCorruptBytes()
    {
        var area = new TestWorkspace(); var store = new JsonConfigurationStore(area.Root);
        var initial = await store.LoadAsync(); var first = Sample("第一次");
        var revision = await store.SaveAsync(first, initial.Revision);
        await store.SaveAsync(first with { Groups = [first.Groups[0] with { Name = "第二次" }] }, revision);
        await File.WriteAllTextAsync(Path.Combine(area.Root, "state.json"), "corrupt");
        var recover = new JsonConfigurationStore(area.Root); var loaded = await recover.LoadAsync();
        Assert.Equal("第一次", Assert.Single(loaded.State.Groups).Name);
        await recover.SaveAsync(loaded.State, loaded.Revision);
        Assert.Contains("schemaVersion", await File.ReadAllTextAsync(Path.Combine(area.Root, "state.json.bak")));
        Assert.Equal("corrupt", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(area.Root, "state.corrupt-*.json"))));
    }

    [Fact]
    public async Task FutureSchemaIsReadOnlyAndItsBytesStayUntouched()
    {
        var area = new TestWorkspace(); var path = Path.Combine(area.Root, "state.json");
        const string bytes = "{\"schemaVersion\":99,\"repositories\":[],\"futureData\":\"keep\"}";
        await File.WriteAllTextAsync(path, bytes);
        var store = new JsonConfigurationStore(area.Root); var loaded = await store.LoadAsync();
        Assert.False(loaded.CanWrite);
        await Assert.ThrowsAsync<TrackerException>(() => store.SaveAsync(new(), loaded.Revision));
        Assert.Equal(bytes, await File.ReadAllTextAsync(path)); Assert.Single(Directory.GetFiles(area.Root));
    }

    [Fact]
    public async Task FailedReplacementKeepsOriginalAndRemovesTemporaryFile()
    {
        var area = new TestWorkspace(); var store = new JsonConfigurationStore(area.Root);
        var initial = await store.LoadAsync(); var state = Sample();
        var revision = await store.SaveAsync(state, initial.Revision);
        var path = Path.Combine(area.Root, "state.json"); var bytes = await File.ReadAllBytesAsync(path);
        using (var readLease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync(state with { Groups = [] }, revision));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path)); Assert.Empty(Directory.GetFiles(area.Root, "*.tmp"));
        Assert.Single((await new JsonConfigurationStore(area.Root).LoadAsync()).State.Groups);
    }

    [Fact]
    public async Task UnreadableFileDoesNotBecomeAnEditableEmptyConfiguration()
    {
        var area = new TestWorkspace(); var path = Path.Combine(area.Root, "state.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Sample(), Json));
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var loaded = await new JsonConfigurationStore(area.Root).LoadAsync();
        Assert.False(loaded.CanWrite); Assert.NotNull(loaded.Notice);
        Assert.Empty(Directory.GetFiles(area.Root, "state.corrupt-*.json"));
    }

    [Fact]
    public async Task UnavailableRepositoryRetainsItsRecordAndFolderFailureDoesNotWriteConfiguration()
    {
        var area = new TestWorkspace(); var store = new JsonConfigurationStore(area.Root);
        var loaded = await store.LoadAsync(); var state = Sample();
        var missing = state.Repositories[0] with { WorktreeRoot = Path.Combine(area.Root, "missing") };
        await store.SaveAsync(state with { Repositories = [missing] }, loaded.Revision);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(area.Root, "state.json"));
        Assert.Equal(missing, Assert.Single((await new JsonConfigurationStore(area.Root).LoadAsync()).State.Repositories));
        Assert.Throws<TrackerException>(() => new RepoTracker.Infrastructure.Runtime.FolderLauncher().Open(missing.WorktreeRoot));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(area.Root, "state.json")));
    }

    [Fact]
    public async Task VersionThreeKeepsExplicitRemoteOptInAndSourcesWhilePreferencesDefaultThenRoundtrip()
    {
        var area = new TestWorkspace(); var original = Sample() with { RemoteEnabled = true };
        var legacy = JsonSerializer.SerializeToNode(original, Json)!; legacy["schemaVersion"] = 3; legacy.AsObject().Remove("preferences");
        await File.WriteAllTextAsync(Path.Combine(area.Root, "state.json"), legacy.ToJsonString());
        var store = new JsonConfigurationStore(area.Root); var loaded = await store.LoadAsync();
        Assert.Equal(4, loaded.State.SchemaVersion); Assert.Equal(new(), loaded.State.Preferences);
        Assert.True(loaded.State.RemoteEnabled); Assert.Equal(original.Sources.ToArray(), loaded.State.Sources.ToArray());
        var desired = loaded.State with { Preferences = new() { KeepExpanded = true, Theme = AppTheme.Light, ScalePercent = 150, LocalIntervalSeconds = 30, RemoteIntervalMinutes = 12, LocalConcurrency = 3 } };
        await store.SaveAsync(desired, loaded.Revision);
        Assert.Equal(desired.Preferences, (await new JsonConfigurationStore(area.Root).LoadAsync()).State.Preferences);
    }

    [Fact]
    public async Task ExternalEditCannotBeOverwrittenByStaleRegistry()
    {
        var area = new TestWorkspace(); var first = new JsonConfigurationStore(area.Root); var second = new JsonConfigurationStore(area.Root);
        var a = await first.LoadAsync(); var b = await second.LoadAsync(); var state = Sample();
        await first.SaveAsync(state, a.Revision);
        await Assert.ThrowsAsync<TrackerException>(() => second.SaveAsync(new(), b.Revision));
        Assert.Equal(state.Repositories.ToArray(), (await new JsonConfigurationStore(area.Root).LoadAsync()).State.Repositories.ToArray());
    }
}

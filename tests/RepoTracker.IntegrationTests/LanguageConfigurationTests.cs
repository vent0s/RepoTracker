using System.Text.Json;
using RepoTracker.Core.Tracking;
using RepoTracker.Infrastructure.Configuration;
using RepoTracker.Tests.Shared;

namespace RepoTracker.IntegrationTests;

public sealed class LanguageConfigurationTests
{
    [Theory]
    [InlineData("system")]
    [InlineData("zh-Hans")]
    [InlineData("en")]
    public async Task LanguagePersistsWithoutChangingUserNames(string language)
    {
        var area = new TestWorkspace();
        var store = new JsonConfigurationStore(area.Root);
        var loaded = await store.LoadAsync();
        var group = new RepositoryGroup(Guid.NewGuid(), "我的 Tools", 0);
        var desired = loaded.State with { Groups = [group], Preferences = new() { Language = language } };
        await store.SaveAsync(desired, loaded.Revision);
        var restored = await new JsonConfigurationStore(area.Root).LoadAsync();
        Assert.True(restored.CanWrite);
        Assert.Equal(language, restored.State.Preferences.Language);
        Assert.Equal(group, Assert.Single(restored.State.Groups));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("future-language")]
    public async Task LegacyOrUnknownLanguageKeepsSettingsAndGroups(string? language)
    {
        var area = new TestWorkspace();
        var original = new TrackerState { Groups = [new(Guid.NewGuid(), "我的 Tools", 0)], Preferences = new() { LocalIntervalSeconds = 45, Theme = AppTheme.Light } };
        var node = JsonSerializer.SerializeToNode(original, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        if (language is null) node["preferences"]!.AsObject().Remove("language");
        else node["preferences"]!["language"] = language;
        await File.WriteAllTextAsync(Path.Combine(area.Root, "state.json"), node.ToJsonString());
        var loaded = await new JsonConfigurationStore(area.Root).LoadAsync();
        Assert.True(loaded.CanWrite);
        Assert.Equal("system", loaded.State.Preferences.Language);
        Assert.Equal(45, loaded.State.Preferences.LocalIntervalSeconds);
        Assert.Equal(AppTheme.Light, loaded.State.Preferences.Theme);
        Assert.Equal(original.Groups[0], Assert.Single(loaded.State.Groups));
        Assert.Empty(Directory.GetFiles(area.Root, "state.corrupt-*"));
    }
}

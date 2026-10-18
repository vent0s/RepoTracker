using RepoTracker.Core.Localization;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using RepoTracker.Core.Sources;

namespace RepoTracker.Core.Tracking;

public sealed record TrackedRepository(Guid Id, string WorktreeRoot, string GitDir, string CommonDir,
    string DisplayName, Guid? GroupId, DateTimeOffset AddedAt, bool ChildrenCollapsed = false);
public sealed record RepositoryGroup(Guid Id, string Name, long Order, bool Collapsed = false);
public sealed record RepositoryLocation(string WorktreeRoot, string GitDir, string CommonDir);

public sealed record TrackerState
{
    public const int CurrentSchema = 4;
    public int SchemaVersion { get; init; } = CurrentSchema;
    public ImmutableArray<TrackedRepository> Repositories { get; init; } = [];
    public ImmutableArray<RepositoryGroup> Groups { get; init; } = [];
    public bool UngroupedCollapsed { get; init; }
    public ImmutableArray<SourceRule> Sources { get; init; } = SourceRules.Defaults;
    public bool RemoteEnabled { get; init; }
    public AppPreferences Preferences { get; init; } = new();
}

public class TrackerException(string message) : Exception(message);

public static class GroupNames
{
    public const string Ungrouped = "未分组";

    public static string Normalize(string name)
    {
        var normalized = name.Trim().Normalize(NormalizationForm.FormC);
        var length = new StringInfo(normalized).LengthInTextElements;
        if (length is < 1 or > 30) throw new TrackerException(Text.Get("Group.NameLength"));
        if (normalized.Any(char.IsControl)) throw new TrackerException(Text.Get("Group.ControlCharacters"));
        if (string.Equals(normalized, Ungrouped, StringComparison.OrdinalIgnoreCase))
            throw new TrackerException(Text.Get("Group.Reserved"));
        return normalized;
    }

    public static string ValidateUnique(string name, IEnumerable<RepositoryGroup> groups, Guid? exceptId = null)
    {
        var normalized = Normalize(name);
        if (groups.Any(group => group.Id != exceptId && string.Equals(group.Name, normalized, StringComparison.OrdinalIgnoreCase)))
            throw new TrackerException(Text.Get("Group.Duplicate"));
        return normalized;
    }
}

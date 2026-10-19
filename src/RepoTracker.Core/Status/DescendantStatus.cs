using RepoTracker.Core.Tracking;

namespace RepoTracker.Core.Status;

/// <summary>Counts affected worktrees, not commits, without including the parent itself.</summary>
public sealed record DescendantStatus(int Total = 0, int Modified = 0, int Ahead = 0, int Behind = 0,
    int Problems = 0, int Unknown = 0, int NoUpstream = 0, int NoComparison = 0)
{
    public bool WorktreesClean => Total > 0 && Modified + Problems + Unknown == 0;
    public bool IsClear => WorktreesClean && Ahead + Behind + NoUpstream + NoComparison == 0;

    private static DescendantStatus From(StatusSnapshot snapshot) => new(1,
        snapshot.Data?.IsDirty == true ? 1 : 0,
        snapshot.Data?.Ahead > 0 ? 1 : 0,
        snapshot.Data?.Behind > 0 ? 1 : 0,
        snapshot.Error != StatusError.None || snapshot.Data?.Conflicts > 0 ? 1 : 0,
        snapshot.Error == StatusError.None && (snapshot.Data is null ||
            snapshot.Data is { HeadKind: HeadKind.Branch, Upstream: not null } data && (data.Ahead is null || data.Behind is null)) ? 1 : 0,
        snapshot.Error == StatusError.None && snapshot.Data is { HeadKind: HeadKind.Branch, Upstream: null } ? 1 : 0,
        snapshot.Error == StatusError.None && snapshot.Data is { HeadKind: not HeadKind.Branch } ? 1 : 0);

    private DescendantStatus Plus(DescendantStatus other) => new(Total + other.Total, Modified + other.Modified,
        Ahead + other.Ahead, Behind + other.Behind, Problems + other.Problems, Unknown + other.Unknown,
        NoUpstream + other.NoUpstream, NoComparison + other.NoComparison);

    public static IReadOnlyDictionary<Guid, DescendantStatus> Build(IReadOnlyList<RepositoryTreeRow> hierarchy,
        IReadOnlyDictionary<Guid, StatusSnapshot> statuses)
    {
        var summaries = hierarchy.ToDictionary(row => row.Repository.Id, _ => new DescendantStatus());
        // Hierarchy is parent-before-child, including rows hidden by collapsed ancestors.
        for (var index = hierarchy.Count - 1; index >= 0; index--)
        {
            var row = hierarchy[index];
            if (row.ParentId is not { } parent) continue;
            var own = From(statuses.GetValueOrDefault(row.Repository.Id) ?? new());
            summaries[parent] = summaries[parent].Plus(own).Plus(summaries[row.Repository.Id]);
        }
        return summaries;
    }
}

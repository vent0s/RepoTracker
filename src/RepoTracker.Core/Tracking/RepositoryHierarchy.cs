namespace RepoTracker.Core.Tracking;

public sealed record RepositoryTreeRow(TrackedRepository Repository, Guid? ParentId, string DisplayPath,
    int Depth, bool IsVisible, int DescendantCount = 0);

/// <summary>Derives directory ancestry within each manual group, without changing repository ownership.</summary>
public static class RepositoryHierarchy
{
    public static IReadOnlyList<RepositoryTreeRow> Build(IEnumerable<TrackedRepository> repositories)
    {
        var entries = repositories.ToArray();
        var paths = entries.ToDictionary(repo => Path.TrimEndingDirectorySeparator(repo.WorktreeRoot), StringComparer.OrdinalIgnoreCase);
        var parents = new Dictionary<Guid, TrackedRepository>();
        var children = new Dictionary<Guid, List<TrackedRepository>>();
        var roots = new List<TrackedRepository>();
        foreach (var repo in entries)
        {
            var directory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(repo.WorktreeRoot));
            while (directory is not null)
            {
                if (paths.TryGetValue(Path.TrimEndingDirectorySeparator(directory), out var parent) && parent.GroupId == repo.GroupId)
                {
                    parents.Add(repo.Id, parent);
                    if (!children.TryGetValue(parent.Id, out var siblings)) children.Add(parent.Id, siblings = []);
                    siblings.Add(repo);
                    break;
                }
                directory = Path.GetDirectoryName(directory);
            }
            if (!parents.ContainsKey(repo.Id)) roots.Add(repo);
        }

        var result = new List<RepositoryTreeRow>();
        var pending = new Stack<(TrackedRepository Repo, int Depth, bool Visible)>();
        foreach (var root in roots.AsEnumerable().Reverse()) pending.Push((root, 0, true));
        while (pending.TryPop(out var item))
        {
            var parent = parents.GetValueOrDefault(item.Repo.Id);
            result.Add(new(item.Repo, parent?.Id, parent is null ? item.Repo.DisplayName
                : Path.GetRelativePath(parent.WorktreeRoot, item.Repo.WorktreeRoot), item.Depth, item.Visible));
            if (children.TryGetValue(item.Repo.Id, out var nested))
                foreach (var child in nested.AsEnumerable().Reverse())
                    pending.Push((child, item.Depth + 1, item.Visible && !item.Repo.ChildrenCollapsed));
        }
        var counts = new Dictionary<Guid, int>();
        for (var index = result.Count - 1; index >= 0; index--)
        {
            var row = result[index];
            var count = counts.GetValueOrDefault(row.Repository.Id);
            result[index] = row with { DescendantCount = count };
            if (row.ParentId is { } parent) counts[parent] = counts.GetValueOrDefault(parent) + count + 1;
        }
        return result;
    }
}

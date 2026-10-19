using RepoTracker.Core.Status;
using RepoTracker.Core.Tracking;

namespace RepoTracker.Core.Tests;

public sealed class DescendantStatusTests
{
    [Fact]
    public void CollapsedAncestorsCountEachDescendantOnceAndExcludeTheirOwnChanges()
    {
        var root = Repo(@"E:\root") with { ChildrenCollapsed = true };
        var child = Repo(@"E:\root\child") with { ChildrenCollapsed = true };
        var deep = Repo(@"E:\root\child\deep");
        var sibling = Repo(@"E:\root\sibling");
        var tree = RepositoryHierarchy.Build([deep, sibling, child, root]);
        var summaries = DescendantStatus.Build(tree, new Dictionary<Guid, StatusSnapshot>
        {
            [root.Id] = Status(100, 100, dirty: true), [child.Id] = Status(0, 3, dirty: true),
            [deep.Id] = Status(7, 0), [sibling.Id] = Status(5, 2)
        });
        Assert.Equal(new DescendantStatus(3, 1, 2, 2), summaries[root.Id]);
        Assert.Equal(new DescendantStatus(1, 0, 1, 0), summaries[child.Id]);
        Assert.Equal(new DescendantStatus(), summaries[deep.Id]);
        Assert.Single(tree, row => row.IsVisible);
    }

    [Fact]
    public void MovingAndRemovingARepositoryRecomputesTheSameGroupAncestry()
    {
        var root = Repo(@"E:\root");
        var child = Repo(@"E:\root\child");
        var deep = Repo(@"E:\root\child\deep");
        var states = new Dictionary<Guid, StatusSnapshot> { [child.Id] = Status(0, 1, dirty: true), [deep.Id] = Status(3, 0) };
        Assert.Equal(new DescendantStatus(2, 1, 1, 1), DescendantStatus.Build(RepositoryHierarchy.Build([root, child, deep]), states)[root.Id]);
        child = child with { GroupId = Guid.NewGuid() };
        var moved = DescendantStatus.Build(RepositoryHierarchy.Build([root, child, deep]), states);
        Assert.Equal(new DescendantStatus(1, 0, 1, 0), moved[root.Id]);
        Assert.Equal(new DescendantStatus(), moved[child.Id]);
        Assert.All(DescendantStatus.Build(RepositoryHierarchy.Build([child, deep]), states).Values, summary => Assert.Equal(new DescendantStatus(), summary));
    }

    [Fact]
    public void UnknownConflictingAndFailedChildrenCannotAppearAllClearAndRecoveryClearsBadges()
    {
        var root = Repo(@"E:\root");
        var children = Enumerable.Range(0, 4).Select(index => Repo(@"E:\root\child" + index)).ToArray();
        var tree = RepositoryHierarchy.Build([root, .. children]);
        var statuses = new Dictionary<Guid, StatusSnapshot>
        {
            [children[1].Id] = new(new(HeadKind.Branch, "main", "123", null, null, null, 0, 0, 0, 0)),
            [children[2].Id] = Status(2, 0) with { Error = StatusError.DirectoryUnavailable },
            [children[3].Id] = new(new(HeadKind.Branch, "main", "123", "origin/main", 0, 0, 0, 0, 0, 1))
        };
        var summary = DescendantStatus.Build(tree, statuses)[root.Id];
        Assert.Equal(new DescendantStatus(4, 1, 1, 0, 2, 1, 1), summary);
        Assert.False(summary.IsClear);
        foreach (var child in children) statuses[child.Id] = Status(0, 0);
        var recovered = DescendantStatus.Build(tree, statuses)[root.Id];
        Assert.Equal(new DescendantStatus(4), recovered);
        Assert.True(recovered.IsClear);
    }

    [Fact]
    public void KnownMissingUpstreamAndDetachedHeadsAreDistinctFromIncompleteStatus()
    {
        var root = Repo(@"E:\root");
        var children = Enumerable.Range(0, 5).Select(index => Repo(@"E:\root\child" + index)).ToArray();
        var tree = RepositoryHierarchy.Build([root, .. children]);
        var statuses = new Dictionary<Guid, StatusSnapshot>
        {
            [children[0].Id] = new(new(HeadKind.Branch, "feature/without-tracking", "123", null, null, null, 0, 0, 0, 0)),
            [children[1].Id] = new(new(HeadKind.Detached, null, "123", null, null, null, 0, 0, 0, 0)),
            [children[2].Id] = new(new(HeadKind.Unborn, "main", null, null, null, null, 0, 0, 0, 0)),
            [children[3].Id] = new(new(HeadKind.Branch, "main", "123", "origin/main", null, null, 0, 0, 0, 0))
        };
        Assert.Equal(new DescendantStatus(5, Unknown: 2, NoUpstream: 1, NoComparison: 2), DescendantStatus.Build(tree, statuses)[root.Id]);
        var known = DescendantStatus.Build(RepositoryHierarchy.Build([root, children[0]]), statuses)[root.Id];
        Assert.True(known.WorktreesClean);
        Assert.False(known.IsClear);
        Assert.Equal(0, known.Unknown);
        Assert.Equal(1, known.NoUpstream);
    }

    private static StatusSnapshot Status(int ahead, int behind, bool dirty = false) =>
        new(new(HeadKind.Branch, "main", "123", "origin/main", ahead, behind, 0, dirty ? 1 : 0, 0, 0));

    private static TrackedRepository Repo(string path) => new(Guid.NewGuid(), path, path + @"\.git", path + @"\.git",
        Path.GetFileName(path), null, DateTimeOffset.UnixEpoch);
}

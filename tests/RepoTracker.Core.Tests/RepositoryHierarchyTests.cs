using RepoTracker.Core.Tracking;

namespace RepoTracker.Core.Tests;

public sealed class RepositoryHierarchyTests
{
    [Fact]
    public void ChoosesNearestDirectoryAncestorRegardlessOfImportOrderAndPreservesIntermediatePath()
    {
        var parent = Repo(@"E:\repos\project");
        var child = Repo(@"e:\REPOS\project\packages\api");
        var grandchild = Repo(@"E:\repos\project\packages\api\vendor\dependency");
        var siblingPrefix = Repo(@"E:\repos\project-extra");
        var tree = RepositoryHierarchy.Build([grandchild, child, parent, siblingPrefix]);
        Assert.Equal(new[] { parent.Id, child.Id, grandchild.Id, siblingPrefix.Id }, tree.Select(row => row.Repository.Id));
        Assert.Equal(new[] { 0, 1, 2, 0 }, tree.Select(row => row.Depth));
        Assert.Equal(parent.Id, tree[1].ParentId); Assert.Equal(child.Id, tree[2].ParentId);
        Assert.Equal(@"packages\api", tree[1].DisplayPath); Assert.Equal(@"vendor\dependency", tree[2].DisplayPath);
        Assert.Equal(new[] { 2, 1, 0, 0 }, tree.Select(row => row.DescendantCount));
    }

    [Fact]
    public void CollapseHidesAllDescendantsButRetainsTheirIndividualRecordsAndExpansion()
    {
        var parent = Repo(@"E:\root") with { ChildrenCollapsed = true };
        var child = Repo(@"E:\root\child") with { ChildrenCollapsed = true };
        var grandchild = Repo(@"E:\root\child\deep");
        var tree = RepositoryHierarchy.Build([parent, child, grandchild]);
        Assert.Equal(new[] { true, false, false }, tree.Select(row => row.IsVisible));
        tree = RepositoryHierarchy.Build([parent with { ChildrenCollapsed = false }, child, grandchild]);
        Assert.Equal(new[] { true, true, false }, tree.Select(row => row.IsVisible));
        Assert.Equal(2, tree[0].DescendantCount);
    }

    [Fact]
    public void MovingOrRemovingParentNeverOrphansOrReassignsChildRecords()
    {
        var parent = Repo(@"E:\root");
        var child = Repo(@"E:\root\child") with { GroupId = Guid.NewGuid() };
        var grandchild = Repo(@"E:\root\child\deep");
        var tree = RepositoryHierarchy.Build([parent, child, grandchild]);
        Assert.Equal(parent.Id, tree.Single(row => row.Repository.Id == grandchild.Id).ParentId);
        Assert.Null(tree.Single(row => row.Repository.Id == child.Id).ParentId);
        tree = RepositoryHierarchy.Build([child, grandchild]);
        Assert.All(tree, row => { Assert.Null(row.ParentId); Assert.Equal(0, row.Depth); Assert.True(row.IsVisible); });
        Assert.Empty(RepositoryHierarchy.Build([]));
    }

    private static TrackedRepository Repo(string root) => new(Guid.NewGuid(), root, root + @"\.git", root + @"\.git",
        Path.GetFileName(root), null, DateTimeOffset.UnixEpoch);
}

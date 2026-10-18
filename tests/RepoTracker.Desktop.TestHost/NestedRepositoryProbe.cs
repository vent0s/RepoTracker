using System.IO;
using System.Windows;
using System.Windows.Controls;
using RepoTracker.Core.Localization;
using RepoTracker.Core.Status;
using RepoTracker.Desktop.ViewModels;
using RepoTracker.Infrastructure.Configuration;
using RepoTracker.Tests.Shared;

namespace RepoTracker.Desktop.TestHost;

internal sealed partial class WindowProbe
{
    private async Task CheckNestedRepositories(MainWindow panel)
    {
        if (Environment.GetEnvironmentVariable("REPOTRACKER_TEST_NESTED") == "reload")
        {
            Require(panel.Model.RepositoryCount == 3, "Restart lost nested repositories.");
            var members = panel.Model.Groups.Single().Repositories;
            var root = members.Single(row => row.ParentId is null);
            Require(root.DescendantCount == 2 && !root.IsExpanded && panel.Model.Groups.Single().VisibleRepositories.Count == 1,
                "Restart lost hierarchy or collapse state.");
            Invoke(RowButton(panel, root.Id, "ToggleRepositoryChildren"));
            await WaitFor(() => panel.Model.Groups.Single().VisibleRepositories.Count == 3 && !panel.Model.IsBusy);
            await WaitFor(() => members.All(row => row.Status.Data is not null));
            SaveImage(panel, "nested-restart.png");
            checks.Add("new process restored directory ancestry and collapsed root; expanding exposes all three independently checked repositories");
            return;
        }
        await panel.Model.SetKeepExpandedAsync(true);
        panel.RestorePanel();
        var area = new TestWorkspace(Path.Combine(outputDirectory, "repositories"));
        var parent = await area.CreateDetachedRepositoryAsync("工程 parent");
        await TestWorkspace.CommitFixtureAsync(parent);
        var child = await area.CreateDetachedRepositoryAsync(Path.Combine("工程 parent", "packages", "API"));
        await TestWorkspace.CommitFixtureAsync(child);
        var grandchild = await area.CreateDetachedRepositoryAsync(Path.Combine("工程 parent", "packages", "API", "vendor", "dependency"));
        var linked = await area.CreateLinkedWorktreeAsync(parent, Path.Combine("工程 parent", "linked 工作树"));
        Directory.CreateDirectory(Path.Combine(parent, ".git", "info"));
        Directory.CreateDirectory(Path.Combine(child, ".git", "info"));
        await File.WriteAllTextAsync(Path.Combine(parent, ".git", "info", "exclude"), "packages/\nlinked 工作树/\n");
        await File.WriteAllTextAsync(Path.Combine(child, ".git", "info", "exclude"), "vendor/\n");
        var before = FixtureBytes(area.Root);
        await Dialog(() => Invoke(panel, "ChooseFolder"), async entry =>
        {
            var scanChildren = Descendants(entry).OfType<CheckBox>().Single(box => System.Windows.Automation.AutomationProperties.GetAutomationId(box) == "ScanChildrenOnAdd");
            Require(scanChildren.IsChecked == true, "Adding a repository did not default to discovering children.");
            scanChildren.IsChecked = false;
            EntryText(entry).Text = parent;
            Invoke(entry, "Submit");
            await WaitFor(() => !entry.IsVisible);
        });
        Require(panel.Model.RepositoryCount == 1 && panel.Model.Registry.State.Repositories.Single().WorktreeRoot == parent,
            "Opting out of discovery did not add only the root.");
        await panel.Model.Registry.RemoveAsync(panel.Model.Registry.State.Repositories.Single().Id);
        await WaitFor(() => panel.Model.RepositoryCount == 0);
        await Dialog(() => Invoke(panel, "ChooseFolder"), async entry =>
        {
            EntryText(entry).Text = parent;
            await Discovery(() => Invoke(entry, "Submit"), async dialog =>
            {
                await WaitFor(() => ScanChoices(dialog).Length == 4);
                Require(dialog.Owner == entry && panel.IsAutoCollapseSuspended, "Discovery dialog lost modal owner or collapse guard.");
                Require(ScanChoices(dialog).All(box => box.IsChecked == true), "New repositories should be selected initially.");
                SaveImage(dialog, "nested-preview.png");
                Invoke(dialog, "Cancel");
            });
            Require(panel.Model.RepositoryCount == 0 && entry.IsVisible, "Cancel imported data or closed the input form.");
            await Discovery(() => Invoke(entry, "Submit"), async dialog =>
            {
                await WaitFor(() => ScanChoices(dialog).Length == 4);
                SelectPath(dialog, grandchild).IsChecked = false;
                SelectPath(dialog, linked).IsChecked = false;
                Invoke(dialog, "ImportRepositories");
            });
        });
        await WaitFor(() => panel.Model.RepositoryCount == 2 && !panel.Model.IsBusy);
        Require(panel.Model.Registry.State.Repositories.All(repo => repo.WorktreeRoot == parent || repo.WorktreeRoot == child), "Unchecked repositories were imported.");
        checks.Add("add form discovers nested roots and git-file worktree; cancel writes nothing; selected roots imported atomically");

        var rootId = panel.Model.Registry.State.Repositories.Single(repo => repo.WorktreeRoot == parent).Id;
        var existingGroup = await panel.Model.Registry.CreateGroupAsync("Existing parent group");
        foreach (var repo in panel.Model.Registry.State.Repositories)
            await panel.Model.Registry.MoveAsync(repo.Id, existingGroup);
        await Dialog(() => Invoke(panel, "ChooseFolder"), async entry =>
        {
            EntryText(entry).Text = parent;
            await Discovery(() => Invoke(entry, "Submit"), async dialog =>
            {
                await WaitFor(() => ScanChoices(dialog).Length == 4);
                Require(!SelectPath(dialog, parent).IsEnabled && !SelectPath(dialog, child).IsEnabled, "Readding a parent did not deduplicate tracked roots.");
                SelectPath(dialog, linked).IsChecked = false;
                Invoke(dialog, "ImportRepositories");
            });
        });
        await WaitFor(() => panel.Model.RepositoryCount == 3 && !panel.Model.IsBusy);
        Require(panel.Model.Registry.State.Repositories.All(repo => repo.GroupId == existingGroup),
            "Adding a child through an existing parent did not preserve its group.");
        await panel.Model.Registry.DeleteGroupAsync(existingGroup);
        var actions = RowButton(panel, rootId, "RepositoryActions");
        Invoke(actions);
        await WaitFor(() => actions.ContextMenu?.IsOpen == true);
        var scan = actions.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, Text.Get("Repository.ScanAction")));
        await Discovery(() => { Invoke(scan); actions.ContextMenu.IsOpen = false; }, async dialog =>
        {
            await WaitFor(() => ScanChoices(dialog).Length == 4);
            Require(!SelectPath(dialog, parent).IsEnabled && !SelectPath(dialog, child).IsEnabled, "Already tracked repositories were selectable.");
            Require(!SelectPath(dialog, grandchild).IsEnabled && SelectPath(dialog, linked).IsEnabled, "Existing-row scan did not distinguish new and tracked children.");
            Invoke(dialog, "Cancel");
        });
        await WaitFor(() => panel.Model.RepositoryCount == 3 && !panel.Model.IsBusy);
        var loaded = await new JsonConfigurationStore(Path.Combine(outputDirectory, "profile")).LoadAsync();
        Require(loaded.State.Repositories.Length == 3, "Batch did not persist all selected repositories.");
        Require(before.SequenceEqual(FixtureBytes(area.Root)), "Scanning/import changed a repository or its metadata.");
        checks.Add("existing repository menu rescans; tracked rows are disabled; deeper child persists independently; repository hashes unchanged");

        RepositoryRow Row(string path) => panel.Model.Groups.SelectMany(group => group.Repositories).Single(row => row.Path == path);
        RepositoryRow[] Visible() => panel.Model.Groups.SelectMany(group => group.VisibleRepositories).ToArray();
        Require(Visible().Select(row => row.Path).SequenceEqual(new[] { parent, child, grandchild }), "Tree order is not parent / child / grandchild.");
        Require(Row(parent).Depth == 0 && Row(child).Depth == 1 && Row(grandchild).Depth == 2 && Row(grandchild).ParentId == Row(child).Id,
            "Directory ancestry was not preserved.");
        Require(Row(child).DisplayPath == @"packages\API" && Row(grandchild).DisplayPath == @"vendor\dependency", "Intermediate directories were lost.");
        Invoke(RowButton(panel, Row(child).Id, "ToggleRepositoryChildren"));
        await WaitFor(() => Visible().Length == 2 && !panel.Model.IsBusy);
        Invoke(RowButton(panel, rootId, "ToggleRepositoryChildren"));
        await WaitFor(() => Visible().Length == 1 && !panel.Model.IsBusy);
        var collapsed = await new JsonConfigurationStore(Path.Combine(outputDirectory, "profile")).LoadAsync();
        Require(collapsed.State.Repositories.Count(repo => repo.ChildrenCollapsed) == 2, "Parent and child collapse was not persisted.");
        await WaitFor(() => panel.Model.Groups.SelectMany(group => group.Repositories).All(row => row.Status.Data is not null));
        Require(!Row(parent).Status.Data!.IsDirty && !Row(child).Status.Data!.IsDirty && Row(grandchild).Status.Data!.IsDirty,
            "Parent, child and grandchild do not have independent local statuses.");
        await File.AppendAllTextAsync(Path.Combine(child, "keep.txt"), "child-only change\n");
        panel.StatusScheduler.Refresh();
        await WaitFor(() => Row(child).Status.Data?.Unstaged == 1);
        Require(!Row(parent).Status.Data!.IsDirty, "Ignored nested worktree changes contaminated the parent's status.");
        Require(Visible().Length == 1 && !Row(parent).IsExpanded, "Status refresh expanded the tree.");
        SaveImage(panel, "nested-collapsed.png");
        await Dialog(() => Invoke(panel, "ChooseFolder"), entry =>
        {
            EntryText(entry).Text = grandchild; Invoke(entry, "Submit"); return Task.CompletedTask;
        });
        await WaitFor(() => Visible().Length == 3 && !panel.Model.IsBusy && Row(grandchild).IsHighlighted);
        Require(panel.Model.RepositoryCount == 3, "Revealing a hidden duplicate created another record.");
        var parentButton = RowButton(panel, rootId, "RepositoryActions");
        var childButton = RowButton(panel, Row(child).Id, "RepositoryActions");
        Require(childButton.TranslatePoint(new Point(0, 0), panel).Y > parentButton.TranslatePoint(new Point(0, 0), panel).Y,
            "Child is not rendered below parent.");
        var groupId = await panel.Model.Registry.CreateGroupAsync("Moved child");
        await panel.Model.Registry.MoveAsync(Row(child).Id, groupId);
        Require(Row(child).ParentId is null && Row(grandchild).ParentId == rootId, "Moving one row orphaned descendants or crossed groups.");
        await panel.Model.Registry.RemoveAsync(rootId);
        Require(Row(grandchild).ParentId is null && Visible().Length == 2, "Removing parent hid or removed descendants.");
        await panel.Model.Registry.UndoRemoveAsync();
        await panel.Model.Registry.MoveAsync(Row(child).Id, null);
        await panel.Model.Registry.DeleteGroupAsync(groupId);
        Require(Visible().Select(row => row.Path).SequenceEqual(new[] { parent, child, grandchild }), "Undo or move back lost the hierarchy.");
        checks.Add("multi-level tree, intermediate directory labels, durable expansion, checks while collapsed, duplicate reveal, single-row move/remove and undo");
        // Ahead/behind rendering uses independent snapshots; fixtures stay detached and create no branches.
        Row(parent).Apply(new(new(HeadKind.Branch, "fixture", "01234567", "origin/fixture", 2, 0, 0, 0, 0, 0)));
        Row(child).Apply(new(new(HeadKind.Branch, "fixture", "01234567", "origin/fixture", 0, 3, 0, 1, 0, 0)));
        Require(Row(parent).ComparisonText == "↑2  ↓0" && Row(child).ComparisonText == "↑0  ↓3", "Nested comparison counters were combined.");
        SaveImage(panel, "nested-status.png");
        checks.Add("real per-repository dirty checks and child file changes; independent ahead/behind rendering from synthetic snapshots (no branches or network created)");
        await panel.Model.Registry.SetChildrenCollapsedAsync(rootId, true);
    }

    private static CheckBox[] ScanChoices(RepositoryDiscoveryWindow dialog) => Descendants(dialog).OfType<CheckBox>().ToArray();
    private static CheckBox SelectPath(RepositoryDiscoveryWindow dialog, string path) => ScanChoices(dialog)
        .Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == path);

    private static async Task Discovery(Action open, Func<RepositoryDiscoveryWindow, Task> inspect)
    {
        async Task Watch()
        {
            RepositoryDiscoveryWindow? dialog = null;
            try
            {
                await WaitFor(() => Application.Current.Windows.OfType<RepositoryDiscoveryWindow>().Any(window => window.IsVisible));
                dialog = Application.Current.Windows.OfType<RepositoryDiscoveryWindow>().Single(window => window.IsVisible);
                await inspect(dialog);
                await WaitFor(() => !dialog.IsVisible);
            }
            finally { if (dialog?.IsVisible == true) dialog.Close(); }
        }
        var watch = Watch();
        open();
        await watch;
        await Task.Delay(120);
    }
}

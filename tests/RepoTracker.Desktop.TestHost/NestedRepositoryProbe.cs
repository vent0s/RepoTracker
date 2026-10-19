using System.IO;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        var ordinary = Directory.CreateDirectory(Path.Combine(parent, "ordinary")).FullName;
        var before = FixtureBytes(area.Root);
        await Dialog(() => Invoke(panel, "ChooseFolder"), async entry =>
        {
            Require(!Descendants(entry).OfType<CheckBox>().Any(), "Manual Add still offers automatic scanning.");
            EntryText(entry).Text = parent;
            Invoke(entry, "Submit");
            await WaitFor(() => !entry.IsVisible);
        });
        await WaitFor(() => panel.Model.RepositoryCount == 1 && !panel.Model.IsBusy);
        Require(panel.Model.Registry.State.Repositories.Single().WorktreeRoot == parent, "Manual Add imported unselected children.");
        var rootId = panel.Model.Registry.State.Repositories.Single().Id;
        var existingGroup = await panel.Model.Registry.CreateGroupAsync("Existing parent group");
        await panel.Model.Registry.MoveAsync(rootId, existingGroup);

        await AddChildDialog(panel, rootId, async entry =>
        {
            Require(entry.Owner == panel && panel.IsAutoCollapseSuspended, "Child entry lost its modal owner or collapse guard.");
            EntryText(entry).Text = child;
            SaveImage(entry, "nested-entry.png");
            Invoke(entry, "Cancel");
            await WaitFor(() => !entry.IsVisible);
        });
        Require(panel.Model.RepositoryCount == 1, "Cancel added a child.");
        await AddChildDialog(panel, rootId, async entry =>
        {
            EntryText(entry).Text = ordinary;
            Invoke(entry, "Submit");
            await WaitFor(() => Error(entry).Text.Length > 0);
            Require(entry.IsVisible && EntryText(entry).Text == ordinary && panel.Model.RepositoryCount == 1,
                "An ordinary folder was accepted or its input was lost.");
            SaveImage(entry, "nested-invalid.png");
            EntryText(entry).Text = child;
            Invoke(entry, "Submit");
        });
        await WaitFor(() => panel.Model.RepositoryCount == 2 && !panel.Model.IsBusy);
        var childId = panel.Model.Registry.State.Repositories.Single(repo => repo.WorktreeRoot == child).Id;
        await AddChildDialog(panel, childId, entry =>
        {
            EntryText(entry).Text = grandchild;
            Invoke(entry, "Submit");
            return Task.CompletedTask;
        });
        await WaitFor(() => panel.Model.RepositoryCount == 3 && !panel.Model.IsBusy);
        Require(panel.Model.Registry.State.Repositories.All(repo => repo.GroupId == existingGroup), "New children did not inherit their parent's group.");
        await AddChildDialog(panel, rootId, entry =>
        {
            EntryText(entry).Text = child.ToUpperInvariant() + Path.DirectorySeparatorChar;
            Invoke(entry, "Submit");
            return Task.CompletedTask;
        });
        await WaitFor(() => panel.Model.Groups.SelectMany(group => group.Repositories).Single(row => row.Id == childId).IsHighlighted && !panel.Model.IsBusy);
        Require(panel.Model.RepositoryCount == 3 && panel.Model.Notice == Text.Get("Repository.Duplicate"), "Duplicate child was not identified and revealed.");
        Require(!panel.Model.Registry.State.Repositories.Any(repo => repo.WorktreeRoot == linked), "Unselected worktree was added.");
        await panel.Model.Registry.DeleteGroupAsync(existingGroup);
        var loaded = await new JsonConfigurationStore(Path.Combine(outputDirectory, "profile")).LoadAsync();
        Require(loaded.State.Repositories.Length == 3, "Manual children did not persist.");
        Require(before.SequenceEqual(FixtureBytes(area.Root)), "Manual adding changed repository files or metadata.");
        checks.Add("manual root and child entries; no directory scan; cancel and invalid child preserve state; group inheritance, duplicate notice and highlight, persistence and unchanged repository hashes");
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
        Require(Row(parent).Descendants.Modified == 1 && Row(child).Descendants.Modified == 1,
            "Collapsed ancestor summary missed a dirty grandchild.");
        var originalChild = await File.ReadAllTextAsync(Path.Combine(child, "keep.txt"));
        await File.AppendAllTextAsync(Path.Combine(child, "keep.txt"), "child-only change\n");
        panel.StatusScheduler.Refresh();
        await WaitFor(() => Row(child).Status.Data?.Unstaged == 1);
        Require(!Row(parent).Status.Data!.IsDirty, "Ignored nested worktree changes contaminated the parent's status.");
        Require(Row(parent).Descendants.Modified == 2, "Collapsed parent summary did not update after a child changed.");
        Require(Visible().Length == 1 && !Row(parent).IsExpanded, "Status refresh expanded the tree.");
        SaveImage(panel, "nested-collapsed.png");
        await File.WriteAllTextAsync(Path.Combine(child, "keep.txt"), originalChild);
        panel.StatusScheduler.Refresh();
        await WaitFor(() => Row(child).Status.Data?.IsDirty == false && Row(parent).Descendants.Modified == 1);
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
        panel.StatusScheduler.Suspend(); panel.RemoteScheduler.Suspend();
        await WaitFor(() => panel.Model.Groups.SelectMany(group => group.Repositories).All(row => !row.Status.IsChecking));
        void ApplyStatusFixture()
        {
            Row(parent).Apply(new(new(HeadKind.Branch, "feature/parent-long-branch", "01234567", "origin/fixture", 2, 0, 0, 0, 0, 0)));
            Row(child).Apply(new(new(HeadKind.Branch, "feature/child-long-branch", "01234567", "origin/fixture", 0, 3, 0, 1, 0, 0)));
            Row(grandchild).Apply(new(new(HeadKind.Branch, "feature/dependency", "01234567", "origin/fixture", 7, 0, 0, 0, 0, 0)));
            var members = panel.Model.Groups.SelectMany(group => group.Repositories).ToDictionary(row => row.Id);
            var summaries = DescendantStatus.Build(RepoTracker.Core.Tracking.RepositoryHierarchy.Build(panel.Model.Registry.State.Repositories),
                members.ToDictionary(pair => pair.Key, pair => pair.Value.Status));
            foreach (var (id, summary) in summaries) members[id].ApplyDescendants(summary);
            Require(Row(parent).ComparisonText == "↑2  ↓0" && Row(child).ComparisonText == "↑0  ↓3", "Nested comparison counters were combined.");
            Require(Row(parent).Descendants == new DescendantStatus(2, 1, 1, 1), "Summary must count affected worktrees at every level, excluding its own commits.");
            Require(Row(parent).StatusBadges.Any(badge => badge.Glyph == "↑2") && Row(parent).DescendantBadges.Any(badge => badge.Glyph == "↓1"),
                "Own commit icons and child repository icons were conflated.");
        }
        ApplyStatusFixture();
        CheckTreeGeometry(panel);
        SaveImage(panel, "nested-status.png");
        void ApplyNoUpstreamFixture()
        {
            foreach (var row in panel.Model.Groups.SelectMany(group => group.Repositories))
                row.Apply(new(new(HeadKind.Branch, "feature/no-tracking", "01234567", null, null, null, 0, 0, 0, 0)));
            var localOnlyMembers = panel.Model.Groups.SelectMany(group => group.Repositories).ToDictionary(row => row.Id);
            foreach (var (id, summary) in DescendantStatus.Build(RepoTracker.Core.Tracking.RepositoryHierarchy.Build(panel.Model.Registry.State.Repositories),
                         localOnlyMembers.ToDictionary(pair => pair.Key, pair => pair.Value.Status))) localOnlyMembers[id].ApplyDescendants(summary);
            Require(Row(parent).Descendants.NoUpstream == 2 && Row(parent).Descendants.Unknown == 0,
                "A known missing upstream was still reported as unknown status.");
            Require(Row(parent).DescendantBadges.Any(badge => badge.Glyph == "✓") && Row(parent).DescendantBadges.Any(badge => badge.Glyph == "—2") &&
                !Row(parent).DescendantBadges.Any(badge => badge.Glyph.StartsWith('?')), "Clean local worktrees and missing comparison target are not shown separately.");
        }
        ApplyNoUpstreamFixture();
        var expander = RowButton(panel, rootId, "ToggleRepositoryChildren");
        expander.Focus();
        Require(expander.Template.FindName("TreeButtonChrome", expander) is Border { BorderThickness.Left: 0 },
            "Mouse/programmatic focus still leaves an oversized border next to the source icon.");
        SaveImage(panel, "nested-no-upstream.png");
        await panel.Model.Registry.SetChildrenCollapsedAsync(rootId, true);
        ApplyNoUpstreamFixture();
        CheckTreeGeometry(panel);
        SaveImage(panel, "nested-no-upstream-collapsed.png");
        ApplyStatusFixture();
        SaveImage(panel, "nested-summary-collapsed.png");
        Require(FindById(panel, "DescendantStatus") is FrameworkElement { IsVisible: true }, "Collapsed parent hides its summary.");
        await CheckRepositoryMenuTheme(panel, rootId, "dark");
        await panel.Model.Registry.UpdatePreferencesAsync(preferences => preferences with { Theme = RepoTracker.Core.Tracking.AppTheme.Light, ScalePercent = 150 });
        ApplyStatusFixture();
        CheckTreeGeometry(panel);
        SaveImage(panel, "nested-summary-light-150.png");
        await CheckRepositoryMenuTheme(panel, rootId, "light-150");
        await panel.Model.Registry.SetChildrenCollapsedAsync(rootId, false);
        ApplyStatusFixture();
        CheckTreeGeometry(panel);
        SaveImage(panel, "nested-expanded-light-150.png");
        await panel.Model.Registry.SetChildrenCollapsedAsync(rootId, true);
        await panel.Model.Registry.UpdatePreferencesAsync(preferences => preferences with { Theme = RepoTracker.Core.Tracking.AppTheme.Dark, ScalePercent = 100 });
        checks.Add("hidden dirty grandchildren and live dirty/clean updates; independent commit icons and descendant repository counts from synthetic snapshots; collapsed summary; dark/light 150% menu and submenu rendering");
        checks.Add("no upstream is distinct from incomplete status; clean worktree badge remains; square chevron hit targets, source icon/name gaps and vertical alignment checked expanded/collapsed at 100% and 150%");
    }

    private static void CheckTreeGeometry(MainWindow panel)
    {
        panel.UpdateLayout();
        var elements = Descendants(panel).OfType<FrameworkElement>().Where(element => element.IsVisible).ToArray();
        var expanders = elements.OfType<Button>().Where(button => AutomationProperties.GetAutomationId(button) == "ToggleRepositoryChildren").ToArray();
        Require(expanders.Length > 0, "No visible tree expander was checked.");
        foreach (var button in expanders)
        {
            var row = (RepositoryRow)button.DataContext;
            var icon = elements.Single(element => element.DataContext == row && AutomationProperties.GetAutomationId(element) == "RepositorySourceIcon");
            var name = elements.Single(element => element.DataContext == row && AutomationProperties.GetAutomationId(element) == "RepositoryName");
            Rect Bounds(FrameworkElement element) => element.TransformToAncestor(panel).TransformBounds(new Rect(element.RenderSize));
            var buttonBounds = Bounds(button); var iconBounds = Bounds(icon); var nameBounds = Bounds(name);
            Require(Math.Abs(buttonBounds.Width - buttonBounds.Height) < 0.5, "Tree expander hit target is not square.");
            Require(buttonBounds.Right + 1 <= iconBounds.Left, "Expander overlaps or touches the repository source icon.");
            Require(iconBounds.Right + 7 <= nameBounds.Left, "Source icon crowds the repository name.");
            Require(Math.Abs(buttonBounds.Top + buttonBounds.Height / 2 - iconBounds.Top - iconBounds.Height / 2) < 0.5,
                "Tree expander and source icon are not vertically centered together.");
        }
    }

    private static async Task AddChildDialog(MainWindow panel, Guid parentId, Func<EntryWindow, Task> inspect)
    {
        var actions = RowButton(panel, parentId, "RepositoryActions");
        Invoke(actions);
        await WaitFor(() => actions.ContextMenu?.IsOpen == true);
        var add = actions.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, Text.Get("Repository.AddChildAction")));
        await Dialog(() => { Invoke(add); actions.ContextMenu.IsOpen = false; }, inspect);
    }

    private async Task CheckRepositoryMenuTheme(MainWindow panel, Guid id, string theme)
    {
        var actions = RowButton(panel, id, "RepositoryActions");
        Invoke(actions);
        await WaitFor(() => actions.ContextMenu?.IsVisible == true);
        var menu = actions.ContextMenu!;
        SaveElement(menu, "nested-menu-" + theme + ".png");
        var move = menu.Items.OfType<MenuItem>().First();
        move.IsSubmenuOpen = true;
        await WaitFor(() => move.Template.FindName("PART_Popup", move) is System.Windows.Controls.Primitives.Popup { IsOpen: true, Child.IsVisible: true });
        var submenu = (System.Windows.Controls.Primitives.Popup)move.Template.FindName("PART_Popup", move);
        SaveElement((FrameworkElement)submenu.Child, "nested-submenu-" + theme + ".png");
        Require(move.Items.OfType<MenuItem>().Any(item => item.IsChecked), "The current group check mark was lost.");
        move.IsSubmenuOpen = false;
        menu.IsOpen = false;
    }

    private void SaveElement(FrameworkElement element, string fileName)
    {
        element.UpdateLayout();
        var scale = element.LayoutTransform.Value.M11;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale),
            (int)Math.Ceiling(element.ActualHeight * scale), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(outputDirectory, fileName)); encoder.Save(file);
    }
}

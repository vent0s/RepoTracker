using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using RepoTracker.Desktop.ViewModels;
using RepoTracker.Tests.Shared;

namespace RepoTracker.Desktop.TestHost;

internal sealed partial class WindowProbe
{
    private async Task CheckRepositoryManagement(MainWindow panel)
    {
        await panel.Model.SetKeepExpandedAsync(true);
        Require(panel.Model.CanEdit && panel.Model.RepositoryCount == 0, "Isolated profile must start empty and editable.");
        await Dialog(() => Invoke(panel, "NewGroup"), async dialog =>
        {
            EntryText(dialog).Text = "未分组";
            Invoke(dialog, "Submit");
            await WaitFor(() => Error(dialog).Text.Length > 0);
            Require(dialog.IsVisible && EntryText(dialog).Text == "未分组", "Invalid input was discarded.");
            SaveImage(dialog, "group-validation.png");
            EntryText(dialog).Text = " 工作 ";
            Invoke(dialog, "Submit");
        });
        await WaitFor(() => panel.Model.Registry.State.Groups.Length == 1);
        var work = panel.Model.Registry.State.Groups.Single().Id;
        await Dialog(() => Invoke(panel, "NewGroup"), async dialog =>
        {
            EntryText(dialog).Text = "工作";
            Invoke(dialog, "Submit");
            await WaitFor(() => Error(dialog).Text.Length > 0);
            Require(panel.Model.Registry.State.Groups.Length == 1, "Duplicate group was saved.");
            EntryText(dialog).Text = "个人";
            Invoke(dialog, "Submit");
        });
        var personal = panel.Model.Registry.State.Groups.Single(group => group.Name == "个人").Id;
        checks.Add("group form: reserved/duplicate name stays editable; trimmed name and empty groups persist");

        var area = new TestWorkspace(Path.Combine(outputDirectory, "repositories"));
        var paths = new List<string>();
        foreach (var name in new[] { "桌面工具", "服务 API", "设计资源", "个人笔记", "学习项目", "临时实验" })
            paths.Add(await area.CreateDetachedRepositoryAsync(name));
        await TestWorkspace.CommitFixtureAsync(paths[0]);
        await TestWorkspace.CommitFixtureAsync(paths[1]);
        var originalFiles = FixtureBytes(area.Root);
        for (var index = 0; index < paths.Count; index++)
        {
            Guid? groupId = index < 3 ? work : index < 5 ? personal : null;
            await Dialog(() => Invoke(panel, "ChooseFolder"), async dialog =>
            {
                Require(dialog.Owner == panel && panel.IsAutoCollapseSuspended, "Add dialog lost owner/focus guard.");
                EntryText(dialog).Text = paths[index];
                var choice = (ComboBox)FindById(dialog, "GroupChoice")!;
                choice.SelectedItem = choice.Items.Cast<GroupChoice>().Single(group => group.Id == groupId);
                if (index == 0)
                {
                    choice.IsDropDownOpen = true;
                    await Task.Delay(100);
                    Require(panel.WindowState == WindowState.Normal, "Group picker collapsed its owner.");
                    choice.IsDropDownOpen = false;
                    dialog.UpdateLayout();
                    Require(Descendants(choice).OfType<TextBlock>().Any(text => text.Text == "工作"), "Group selector must render its display name.");
                    SaveImage(dialog, "add-repository.png");
                }
                Invoke(dialog, "Submit");
            });
            await WaitFor(() => panel.Model.RepositoryCount == index + 1 && !panel.Model.IsBusy);
        }
        Require(Counts(panel).SequenceEqual(new[] { 3, 2, 1 }), "Initial group counts are incorrect.");
        SaveImage(panel, "groups-six.png");
        checks.Add("six real detached Git fixtures added through path/group dialogs: 工作 3 / 个人 2 / 未分组 1");

        await Dialog(() => Invoke(panel, "ChooseFolder"), async dialog =>
        {
            EntryText(dialog).Text = Path.Combine(area.Root, "missing directory");
            Invoke(dialog, "Submit");
            await WaitFor(() => Error(dialog).Text.Length > 0);
            Require(EntryText(dialog).Text.EndsWith("missing directory"), "Failed path input was cleared.");
            SaveImage(dialog, "path-validation.png");
            Invoke(dialog, "Cancel");
        });
        Require(panel.Model.RepositoryCount == 6, "Cancelled/invalid add changed the list.");

        var first = panel.Model.Registry.State.Repositories[0];
        await Dialog(() => Invoke(panel, "ChooseFolder"), dialog =>
        {
            EntryText(dialog).Text = Directory.CreateDirectory(Path.Combine(paths[0], "nested")).FullName;
            Invoke(dialog, "Submit");
            return Task.CompletedTask;
        });
        await WaitFor(() => panel.Model.Groups.SelectMany(group => group.Repositories).Any(row => row.Id == first.Id && row.IsHighlighted));
        Require(panel.Model.RepositoryCount == 6, "Subdirectory duplicate added another record.");
        checks.Add("missing path error preserves form; cancel leaves list intact; subdirectory duplicate selects existing row");

        Invoke(panel, "ManageGroups");
        await WaitFor(() => panel.Model.ManagingGroups);
        await Dialog(() => Invoke(GroupButton(panel, work, "RenameGroup")), dialog =>
        {
            EntryText(dialog).Text = "团队项目";
            Invoke(dialog, "Submit");
            return Task.CompletedTask;
        });
        Require(panel.Model.Registry.State.Groups.Single(group => group.Id == work).Name == "团队项目", "Rename lost stable identity.");

        var rowButton = RowButton(panel, first.Id, "RepositoryActions");
        Invoke(rowButton);
        await WaitFor(() => rowButton.ContextMenu?.IsOpen == true);
        var move = rowButton.ContextMenu!.Items.OfType<MenuItem>().First();
        move.IsSubmenuOpen = true;
        await Task.Delay(100);
        Invoke(move.Items.OfType<MenuItem>().Single(item => (string)item.Header == "个人"));
        rowButton.ContextMenu.IsOpen = false;
        await WaitFor(() => panel.Model.Registry.State.Repositories.Single(repo => repo.Id == first.Id).GroupId == personal && !panel.Model.IsBusy);
        Require(Counts(panel).SequenceEqual(new[] { 2, 3, 1 }), "Move failed to update group counts.");
        await WaitFor(() => !panel.IsAutoCollapseSuspended);

        var beforeIds = panel.Model.Registry.State.Repositories.Select(repo => repo.Id).ToHashSet();
        await Dialog(() => Invoke(GroupButton(panel, personal, "DeleteGroup")), dialog =>
        {
            Require(((TextBlock)dialog.FindName("Description")).Text.Contains("3 个仓库"), "Delete confirmation count is wrong.");
            SaveImage(dialog, "delete-group.png");
            Invoke(dialog, "Submit");
            return Task.CompletedTask;
        });
        Require(beforeIds.SetEquals(panel.Model.Registry.State.Repositories.Select(repo => repo.Id)), "Deleting a group lost repositories.");
        Require(Counts(panel).SequenceEqual(new[] { 2, 4 }), "Deleted group members did not return to ungrouped.");
        Require(!GroupButton(panel, null, "DeleteGroup").IsEnabled && !GroupButton(panel, null, "RenameGroup").IsEnabled, "Ungrouped must be protected.");
        checks.Add("real rename, row move menu and delete confirmation: stable IDs, counts 2/4, protected ungrouped");

        rowButton = RowButton(panel, first.Id, "RepositoryActions");
        Invoke(rowButton);
        await WaitFor(() => rowButton.ContextMenu?.IsOpen == true);
        await Dialog(() => Invoke(rowButton.ContextMenu!.Items.OfType<MenuItem>().Last()), dialog =>
        {
            rowButton.ContextMenu!.IsOpen = false;
            Invoke(dialog, "Submit");
            return Task.CompletedTask;
        });
        await WaitFor(() => panel.Model.RepositoryCount == 5 && panel.Model.CanUndo);
        Invoke(panel, "UndoRemove");
        await WaitFor(() => panel.Model.RepositoryCount == 6 && !panel.Model.IsBusy);
        Require(beforeIds.SetEquals(panel.Model.Registry.State.Repositories.Select(repo => repo.Id)), "Undo changed the restored repository ID.");

        // Force a real save failure without touching application or fixture data.
        var statePath = Path.Combine(outputDirectory, "profile", "state.json");
        using (var lease = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Dialog(() => Invoke(panel, "NewGroup"), async dialog =>
            {
                EntryText(dialog).Text = "不能保存的分组";
                Invoke(dialog, "Submit");
                await WaitFor(() => Error(dialog).Text.Length > 0);
                Require(EntryText(dialog).Text == "不能保存的分组" && panel.Model.Registry.State.Groups.Length == 1, "Failed save published or discarded user input.");
                SaveImage(dialog, "save-failure.png");
                Invoke(dialog, "Cancel");
            });
        checks.Add("remove confirmation and undo restore stable record; locked file save failure keeps form and previous state");

        Invoke(panel, "ManageGroups");
        await WaitFor(() => !panel.Model.ManagingGroups);
        SaveImage(panel, "managed-repositories.png");
        foreach (var id in new Guid?[] { work, null })
        {
            var expander = Descendants(panel).OfType<Expander>().Single(element => element.DataContext is GroupRow group && group.Id == id);
            expander.IsExpanded = false;
            await WaitFor(() => panel.Model.Groups.Single(group => group.Id == id).IsExpanded == false && !panel.Model.IsBusy);
        }
        Require(originalFiles.SequenceEqual(FixtureBytes(area.Root)), "Repository management changed fixture files or Git metadata.");
        File.WriteAllText(Path.Combine(outputDirectory, "expected-repositories.json"), JsonSerializer.Serialize(beforeIds.OrderBy(id => id).ToArray()));
        SaveImage(panel, "collapsed-groups.png");
        checks.Add("custom and ungrouped collapse persisted; all fixture file and Git metadata hashes unchanged");
        await CheckLiveStatus(panel, area.Root);
        await CheckSourcesAndRemote(panel);
    }

    private async Task CheckReload(MainWindow panel)
    {
        Require(panel.Model.KeepExpanded, "Restart lost keep-expanded setting.");
        var preferences = panel.Model.Registry.State.Preferences;
        Require(preferences.LocalIntervalSeconds == 30 && preferences.RemoteIntervalMinutes == 12 && preferences.LocalConcurrency == 3 &&
            preferences.Theme == Core.Tracking.AppTheme.Light && preferences.ScalePercent == 125, "Restart lost M5 preferences.");
        Require(panel.StartupRegistration.IsEnabled, "Isolated startup setting did not survive restart.");
        panel.StartupRegistration.SetEnabled(false);
        panel.RestorePanel();
        await Task.Delay(180);
        var expected = JsonSerializer.Deserialize<Guid[]>(File.ReadAllText(Path.Combine(outputDirectory, "expected-repositories.json")))!;
        Require(expected.ToHashSet().SetEquals(panel.Model.Registry.State.Repositories.Select(repo => repo.Id)), "Process restart lost tracked repository identities.");
        Require(Counts(panel).SequenceEqual(new[] { 2, 4 }), "Process restart lost group membership.");
        Require(panel.Model.Groups[0].Name == "团队项目" && panel.Model.Groups.All(group => !group.IsExpanded), "Process restart lost rename or collapse state.");
        Require(!panel.Model.RemoteEnabled && panel.Model.Registry.State.Sources.Any(rule => rule.Name == "公司 Git" && rule.Pattern == "code.example.invalid"), "Restart lost M4 settings.");
        SaveImage(panel, "restart.png");
        checks.Add("new application process reloaded six repository IDs, renamed group, membership 2/4 and both collapsed states");
        checks.Add("new application process reloaded remote OFF and the edited custom source rule");
        checks.Add("M5 new process restored intervals, concurrency, keep-expanded, light theme, 125% scale and isolated startup registration; isolated entry removed");
    }

    private static int[] Counts(MainWindow panel) => panel.Model.Groups.Select(group => group.Repositories.Count).ToArray();
    private static TextBox EntryText(EntryWindow dialog) => (TextBox)FindById(dialog, "EntryText")!;
    private static TextBlock Error(EntryWindow dialog) => (TextBlock)dialog.FindName("ErrorText");
    private static Button GroupButton(MainWindow panel, Guid? id, string action) => Descendants(panel).OfType<Button>().Single(button =>
        AutomationProperties.GetAutomationId(button) == action && button.DataContext is GroupRow group && group.Id == id);
    private static Button RowButton(MainWindow panel, Guid id, string action) => Descendants(panel).OfType<Button>().Single(button =>
        AutomationProperties.GetAutomationId(button) == action && button.DataContext is RepositoryRow row && row.Id == id);
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static void Invoke(MenuItem menu) => ((IInvokeProvider)new MenuItemAutomationPeer(menu).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static async Task WaitFor(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("UI condition did not become true.");
            await Task.Delay(40);
        }
    }
    private static async Task Dialog(Action open, Func<EntryWindow, Task> inspect)
    {
        async Task Watch()
        {
            EntryWindow? dialog = null;
            try
            {
                await WaitFor(() => Application.Current.Windows.OfType<EntryWindow>().Any(window => window.IsVisible));
                dialog = Application.Current.Windows.OfType<EntryWindow>().Single(window => window.IsVisible);
                await Task.Delay(100);
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
    private static string[] FixtureBytes(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
}

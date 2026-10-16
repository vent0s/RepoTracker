using System.IO;
using System.Windows;
using System.Windows.Controls;
using RepoTracker.Core.Status;
using RepoTracker.Desktop.ViewModels;

namespace RepoTracker.Desktop.TestHost;

internal sealed partial class WindowProbe
{
    private async Task CheckLiveStatus(MainWindow panel, string fixtures)
    {
        RepositoryRow Row(Guid id) => panel.Model.Groups.SelectMany(group => group.Repositories).Single(row => row.Id == id);
        await WaitFor(() => panel.Model.Groups.SelectMany(group => group.Repositories).All(row => row.Status.Data is not null));
        Require(panel.Model.Groups.SelectMany(group => group.Repositories).All(row => row.Status.Error == StatusError.None), "A real status check failed.");
        var repo = panel.Model.Registry.State.Repositories.Single(item => item.DisplayName == "服务 API");
        Require(Row(repo.Id).Status.Data!.IsDirty == false && Row(repo.Id).BranchText == "分离 HEAD" && Row(repo.Id).ComparisonText == "无上游", "Clean detached status is not rendered correctly.");
        var checkedAt = Row(repo.Id).Status.CheckedAt;
        Invoke(panel, "RefreshLocal");
        await WaitFor(() => Row(repo.Id).Status.CheckedAt > checkedAt);
        Require(panel.Model.CanEdit, "Background refresh blocked repository management.");
        panel.WindowState = WindowState.Minimized;
        await File.AppendAllTextAsync(Path.Combine(repo.WorktreeRoot, "keep.txt"), "changed while collapsed and minimized\n");
        await WaitFor(() => Row(repo.Id).Status.Data!.Unstaged == 1);
        Require(panel.WindowState == WindowState.Minimized && panel.Model.Groups.All(group => !group.IsExpanded), "Background update unexpectedly expanded the panel or groups.");
        panel.RestorePanel();
        foreach (var group in panel.Model.Groups.ToArray())
        {
            await panel.Model.Registry.SetCollapsedAsync(group.Id, false);
        }
        await Task.Delay(150);
        SaveImage(panel, "local-status.png");
        checks.Add("real local refresh: clean detached / no upstream, file event updates dirty state while minimized and groups collapsed");

        var unavailable = panel.Model.Registry.State.Repositories.Single(item => item.DisplayName == "设计资源");
        var offline = unavailable.WorktreeRoot + ".offline";
        Require(Path.GetFullPath(unavailable.WorktreeRoot).StartsWith(Path.GetFullPath(fixtures) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFullPath(offline).StartsWith(Path.GetFullPath(fixtures) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Fixture move escaped the test root.");
        var remembered = Row(unavailable.Id).Status.Data;
        Directory.Move(unavailable.WorktreeRoot, offline);
        try
        {
            Invoke(panel, "RefreshLocal");
            await WaitFor(() => Row(unavailable.Id).Status.Error == StatusError.DirectoryUnavailable);
            Require(Row(unavailable.Id).StatusText == "不可达" && Row(unavailable.Id).Status.Data == remembered && panel.Model.RepositoryCount == 6, "Unavailable repository lost its prior result or tracking record.");
            SaveImage(panel, "local-error.png");
        }
        finally { Directory.Move(offline, unavailable.WorktreeRoot); }
        Invoke(panel, "RefreshLocal");
        await WaitFor(() => Row(unavailable.Id).Status.Error == StatusError.None);
        foreach (var group in panel.Model.Groups.ToArray()) await panel.Model.Registry.SetCollapsedAsync(group.Id, true);
        checks.Add("unavailable directory retains record and last result, then recovers after restoring the isolated fixture");
    }
}

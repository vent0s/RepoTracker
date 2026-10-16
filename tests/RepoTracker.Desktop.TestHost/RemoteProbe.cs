using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using RepoTracker.Core.Sources;
using RepoTracker.Core.Status;
using RepoTracker.Desktop.ViewModels;

namespace RepoTracker.Desktop.TestHost;

internal sealed partial class WindowProbe
{
    private async Task CheckSourcesAndRemote(MainWindow panel)
    {
        Require(!panel.Model.RemoteEnabled, "Remote checks must default to OFF.");
        Require(panel.Model.Groups.SelectMany(group => group.Repositories).All(row => row.Source.Name == "本地仓库"), "Local-only sources not identified.");
        async Task Watch()
        {
            SettingsWindow? dialog = null;
            try
            {
                await WaitFor(() => Application.Current.Windows.OfType<SettingsWindow>().Any(window => window.IsVisible));
                dialog = Application.Current.Windows.OfType<SettingsWindow>().Single(window => window.IsVisible);
                await Task.Delay(100);
                var toggle = (CheckBox)dialog.FindName("RemoteSwitch");
                void Toggle()
                {
                    ((IToggleProvider)new CheckBoxAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)).Toggle();
                    toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                }
                Toggle(); await WaitFor(() => panel.Model.RemoteEnabled);
                await WaitFor(() => panel.RemoteScheduler.Snapshot.Values.All(value => value.Outcome == RemoteOutcome.Skipped));
                var row = panel.Model.Groups.SelectMany(group => group.Repositories).First();
                var checkedAt = row.Status.CheckedAt;
                panel.StatusScheduler.Refresh(); await WaitFor(() => row.Status.CheckedAt > checkedAt);
                SaveImage(dialog, "remote-settings.png");
                Toggle(); await WaitFor(() => !panel.Model.RemoteEnabled && toggle.IsEnabled);
                Require(row.RemoteText == "远程已关闭", "Remote OFF state not rendered.");
                ((TabControl)dialog.FindName("Tabs")).SelectedIndex = 1;
                await Task.Delay(100);
                TextBox Box(string name) => (TextBox)dialog.FindName(name);
                TextBlock Feedback() => (TextBlock)dialog.FindName("Feedback");
                Invoke(dialog, "NewSource"); await WaitFor(() => Feedback().Text.StartsWith("填写"));
                Box("SourceName").Text = "内网 Git"; Box("SourcePattern").Text = "https://code.example.invalid";
                Feedback().Text = "";
                Invoke(dialog, "SaveSource"); await WaitFor(() => Feedback().Text.StartsWith("请输入") && ((TabControl)dialog.FindName("Tabs")).IsEnabled);
                Require(Box("SourcePattern").Text.StartsWith("https://"), "Invalid form input was discarded.");
                Box("SourcePattern").Text = "code.example.invalid"; Box("PreviewUrl").Text = "git@code.example.invalid:team/repo.git";
                Invoke(dialog, "PreviewSource"); await WaitFor(() => ((TextBlock)dialog.FindName("PreviewResult")).Text.Contains("内网 Git"));
                Invoke(dialog, "SaveSource"); await WaitFor(() => panel.Model.Registry.State.Sources.Any(rule => rule.Name == "内网 Git"));
                await WaitFor(() => ((TabControl)dialog.FindName("Tabs")).IsEnabled);
                Box("SourceName").Text = "公司 Git"; Invoke(dialog, "SaveSource");
                await WaitFor(() => panel.Model.Registry.State.Sources.Any(rule => rule.Name == "公司 Git"));
                await WaitFor(() => ((TabControl)dialog.FindName("Tabs")).IsEnabled);
                Invoke(dialog, "NewSource"); await WaitFor(() => Feedback().Text.StartsWith("填写"));
                Box("SourceName").Text = "临时"; Box("SourcePattern").Text = "*.example.invalid";
                Invoke(dialog, "SaveSource"); await WaitFor(() => panel.Model.Registry.State.Sources.Length == 4);
                await WaitFor(() => ((TabControl)dialog.FindName("Tabs")).IsEnabled);
                Invoke(dialog, "DeleteSource"); await WaitFor(() => panel.Model.Registry.State.Sources.Length == 3);
                await WaitFor(() => ((TabControl)dialog.FindName("Tabs")).IsEnabled);
                var list = (ListBox)dialog.FindName("RuleList"); list.SelectedItem = panel.Model.Registry.State.Sources.Single(rule => rule.Name == "公司 Git");
                Invoke(dialog, "PreviewSource"); await WaitFor(() => ((TextBlock)dialog.FindName("PreviewResult")).Text.Contains("公司 Git"));
                SaveImage(dialog, "source-rules.png");
                dialog.Close();
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(outputDirectory, "m4-failure.txt"), error.ToString());
                if (dialog?.IsVisible == true) SaveImage(dialog, "source-last-state.png");
                throw;
            }
            finally
            {
                if (dialog is not null)
                {
                    await WaitFor(() => ((TabControl)dialog.FindName("Tabs")).IsEnabled);
                    dialog.Close();
                }
            }
        }
        var watch = Watch(); panel.ShowSettings(); await watch;
        // The remote is configured only AFTER OFF has been saved. .invalid is preview data, never a fetch target in this test.
        var repo = panel.Model.Registry.State.Repositories.Single(repo => repo.DisplayName == "服务 API");
        var config = Path.Combine(repo.GitDir, "config");
        await File.AppendAllTextAsync(config, "\n[remote \"origin\"]\nurl = https://code.example.invalid/team/repo.git\nfetch = +refs/heads/*:refs/remotes/origin/*\n");
        var before = await File.ReadAllBytesAsync(config);
        panel.StatusScheduler.Refresh();
        RepositoryRow Row() => panel.Model.Groups.SelectMany(group => group.Repositories).Single(row => row.Id == repo.Id);
        await WaitFor(() => Row().Source.Name == "公司 Git");
        var after = await File.ReadAllBytesAsync(config);
        Require(before.SequenceEqual(after), "Source matching modified remote configuration.");
        Require(Row().SourceBadge == "自建" && !panel.Model.RemoteEnabled, "Source badge or OFF state incorrect.");
        foreach (var group in panel.Model.Groups.ToArray()) await panel.Model.Registry.SetCollapsedAsync(group.Id, false);
        await Task.Delay(100); SaveImage(panel, "sources-panel.png");
        foreach (var group in panel.Model.Groups.ToArray()) await panel.Model.Registry.SetCollapsedAsync(group.Id, true);
        checks.Add("M4 real settings: default OFF, enable local-only skips, local refresh continues, disable saved; no network-enabled fixture has a remote URL");
        checks.Add("M4 real source editor: invalid-input retention, draft scp preview, add/edit/delete, source badge update and unchanged remote config");
    }
}

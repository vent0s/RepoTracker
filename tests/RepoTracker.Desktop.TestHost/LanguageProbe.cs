using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using RepoTracker.Core.Localization;
using RepoTracker.Tests.Shared;

namespace RepoTracker.Desktop.TestHost;

internal sealed partial class WindowProbe
{
    private async Task CheckLanguage(MainWindow panel)
    {
        var expected = Environment.GetEnvironmentVariable("REPOTRACKER_TEST_LANGUAGE")!;
        var english = expected == "en";
        Require(Text.Get("Settings.Heading") == (english ? "Settings" : "设置"), "Incorrect startup display language.");
        Require(panel.Model.Groups.Last().Name == (english ? "Ungrouped" : "未分组"), "System group was not translated.");
        Require(panel.Model.Registry.State.Groups.Single().Name == "我的 Tools", "User-defined name was translated.");
        await panel.Model.SetKeepExpandedAsync(true);
        panel.RestorePanel();
        var area = new TestWorkspace(Path.Combine(outputDirectory, "repositories"));
        var repo = await area.CreateDetachedRepositoryAsync("多语言 demo");
        await panel.Model.Registry.AddAsync(repo, panel.Model.Registry.State.Groups.Single().Id);
        await WaitFor(() => panel.Model.Groups.SelectMany(g => g.Repositories).All(r => r.Status.Data is not null));
        var row = panel.Model.Groups.SelectMany(g => g.Repositories).Single();
        Require(row.StatusText == (english ? "Modified" : "有修改"), "Background status language differs from UI language.");
        Require(row.BranchText == (english ? "Detached HEAD" : "分离 HEAD"), "Dynamic branch state was not translated.");
        Require(row.OpenFolderAccessibleName.Contains("多语言 demo"), "Accessible name lost the user repository name.");
        SaveImage(panel, "language-main.png");
        async Task Watch()
        {
            SettingsWindow? dialog = null;
            try
            {
                await WaitFor(() => application.Windows.OfType<SettingsWindow>().Any(w => w.IsVisible));
                dialog = application.Windows.OfType<SettingsWindow>().Single();
                Require(dialog.Title == (english ? "Settings · RepoTracker" : "设置 · RepoTracker"), "Window title was not translated.");
                var language = (ComboBox)dialog.FindName("LanguageChoice");
                var feedback = (TextBlock)dialog.FindName("Feedback");
                async Task ClickAndWait(string id)
                {
                    feedback.Text = "";
                    Invoke(dialog, id);
                    await WaitFor(() => feedback.Text.Length > 0 && ((TabControl)dialog.FindName("Tabs")).IsEnabled);
                }
                Require(new ComboBoxAutomationPeer(language).GetName() == (english ? "Interface language" : "界面语言"), "Language field lacks a translated accessible name.");
                ((ComboBox)dialog.FindName("ScaleChoice")).SelectedIndex = 2;
                await ClickAndWait("SavePreferences");
                dialog.UpdateLayout();
                AssertInsideWorkArea(dialog);
                ((ScrollViewer)dialog.FindName("CheckScroll")).ScrollToTop();
                SaveImage(dialog, "language-settings.png");
                ((TextBox)dialog.FindName("LocalInterval")).Text = "0";
                await ClickAndWait("SavePreferences");
                Require(feedback.Text == (english ? "Local check interval must be 5–300 seconds." : "本地检查间隔应为 5–300 秒。"), "Validation was not translated: " + feedback.Text);
                var original = panel.Model.Registry.State.Preferences.Language;
                language.SelectedIndex = english ? 1 : 2;
                using (var lease = new FileStream(Path.Combine(outputDirectory, "profile", "state.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    await ClickAndWait("SaveLanguage");
                    Require(panel.Model.Registry.State.Preferences.Language == original, "Failed language save changed live settings.");
                    Require(feedback.Text == Text.Get("Action.Failed"), "Save error was not translated.");
                }
                await ClickAndWait("SaveLanguage");
                Require(panel.Model.Registry.State.Preferences.Language == (english ? "zh-Hans" : "en"), "Language preference was not saved.");
                Require(feedback.Text == (english ? "Language preference saved. It will apply on next launch." : "语言设置已保存，下次启动生效。"), "Restart requirement is not visible.");
                Require(dialog.Title == (english ? "Settings · RepoTracker" : "设置 · RepoTracker"), "Language partially changed before restart.");
            }
            finally { dialog?.Close(); }
        }
        var watch = Watch();
        panel.ShowSettings();
        await watch;
        checks.Add($"Language {expected}: real startup, XAML, background state, accessibility, user names, 150% app scale, validation and failed/successful language save.");
        // A reload-only probe in the parent test checks the saved override in a fresh process.
    }
}

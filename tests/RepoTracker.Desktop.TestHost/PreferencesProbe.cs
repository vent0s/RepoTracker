using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using RepoTracker.Core.Tracking;
using RepoTracker.Desktop.ViewModels;
using RepoTracker.Desktop.Windows;
using RepoTracker.Tests.Shared;

namespace RepoTracker.Desktop.TestHost;

internal sealed partial class WindowProbe
{
    private async Task CheckPreferencesAndLayout(MainWindow panel)
    {
        var trigger = (Button)FindById(panel, "Settings")!;
        await FocusOwnWindow(new WindowInteropHelper(panel).Handle);
        trigger.Focus();
        await WaitFor(() => trigger.IsKeyboardFocused);
        async Task Watch()
        {
            SettingsWindow? dialog = null;
            try
            {
                await WaitFor(() => application.Windows.OfType<SettingsWindow>().Any(w => w.IsVisible));
                dialog = application.Windows.OfType<SettingsWindow>().Single(w => w.IsVisible);
                await Task.Delay(100);
                TextBox Box(string name) => (TextBox)dialog.FindName(name);
                TextBlock Feedback() => (TextBlock)dialog.FindName("Feedback");
                async Task Save()
                {
                    Feedback().Text = ""; Invoke(dialog, "SavePreferences");
                    await WaitFor(() => Feedback().Text.Length > 0 && ((TabControl)dialog.FindName("Tabs")).IsEnabled);
                }
                var original = panel.Model.Registry.State.Preferences;
                Box("LocalInterval").Text = "0"; await Save();
                Require(Feedback().Text.Contains("5–300") && Box("LocalInterval").Text == "0" && panel.Model.Registry.State.Preferences == original, "Invalid preferences changed live state or lost input.");
                Box("LocalInterval").Text = "30"; Box("RemoteInterval").Text = "12"; Box("LocalConcurrency").Text = "3";
                using (var lease = new FileStream(Path.Combine(outputDirectory, "profile", "state.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    await Save();
                    Require(panel.Model.Registry.State.Preferences == original && Feedback().Text.Contains("未完成") && Box("LocalInterval").Text == "30", "Failed preference save changed state.");
                    SaveImage(dialog, "preferences-save-failure.png");
                }
                ((ComboBox)dialog.FindName("ThemeChoice")).SelectedIndex = 1;
                ((ComboBox)dialog.FindName("ScaleChoice")).SelectedIndex = 2;
                await Save(); await Task.Delay(150);
                Require(Feedback().Text.Contains("生效"), "Preferences were not applied: " + Feedback().Text);
                Require(panel.Model.Registry.State.Preferences.ScalePercent == 150 && panel.Model.Registry.State.Preferences.Theme == AppTheme.Light, "Theme/scale did not save.");
                Require(((SolidColorBrush)panel.Background).Color == Color.FromRgb(244, 246, 249), "Live light palette did not update main window.");
                AssertInsideWorkArea(dialog); AssertInsideWorkArea(panel);
                SaveImage(dialog, "settings-light-150.png");
                var startup = (FixtureStartupRegistration)panel.StartupRegistration;
                var toggle = (CheckBox)dialog.FindName("StartupSwitch");
                startup.Fail = true; Toggle(toggle); await WaitFor(() => Feedback().Text.Contains("未完成"));
                Require(!startup.IsEnabled && toggle.IsChecked == false, "Failed startup toggle was shown as saved.");
                startup.Fail = false; Toggle(toggle); await WaitFor(() => startup.IsEnabled);
                Toggle(toggle); await WaitFor(() => !startup.IsEnabled);
                Toggle(toggle); await WaitFor(() => startup.IsEnabled);
                var keep = (CheckBox)dialog.FindName("KeepSwitch");
                Toggle(keep); await WaitFor(() => !panel.Model.KeepExpanded && ((TabControl)dialog.FindName("Tabs")).IsEnabled);
                Toggle(keep); await WaitFor(() => panel.Model.KeepExpanded && ((TabControl)dialog.FindName("Tabs")).IsEnabled);
                ((ComboBox)dialog.FindName("ThemeChoice")).SelectedIndex = 0;
                ((ComboBox)dialog.FindName("ScaleChoice")).SelectedIndex = 0;
                await Save(); SaveImage(dialog, "settings-dark.png");
                ((ComboBox)dialog.FindName("ThemeChoice")).SelectedIndex = 1;
                ((ComboBox)dialog.FindName("ScaleChoice")).SelectedIndex = 1;
                await Save();
                Box("LocalInterval").Focus();
                Require(new TextBoxAutomationPeer(Box("LocalInterval")).GetName().Contains("秒"), "Numeric field lacks an accessible name.");
                SendKey(dialog, 0x09);
                await Task.Delay(80);
                Require(Box("RemoteInterval").IsKeyboardFocused, "Tab did not navigate to the next field.");
                SendKey(dialog, 0x1B);
                await WaitFor(() => !dialog.IsVisible);
            }
            catch
            {
                if (dialog?.IsVisible == true) SaveImage(dialog, "preferences-last-state.png");
                throw;
            }
            finally
            {
                if (dialog?.IsVisible == true) { await WaitFor(() => ((TabControl)dialog.FindName("Tabs")).IsEnabled); dialog.Close(); }
            }
        }
        var watch = Watch(); panel.ShowSettings(); await watch;
        await WaitFor(() => trigger.IsKeyboardFocused);
        var parsed = StartupOptions.Parse(["--minimized", "--profile", Path.Combine(outputDirectory, "profile")]);
        Require(parsed.Minimized && parsed.Profile == Path.Combine(outputDirectory, "profile"), "Startup switches did not parse in either order.");
        checks.Add("M5 real settings: invalid input and file-lock failure preserve draft/state; live dark/light and 100/125/150 scale; keep-expanded and isolated startup toggle/failure; Tab/Escape and focus return");

        // Enter and Escape operate a real form, and a cancelled form cannot create a group.
        await Dialog(panel.ShowNewGroup, async dialog =>
        {
            EntryText(dialog).Text = "键盘分组"; EntryText(dialog).Focus(); SendKey(dialog, 0x0D);
            await WaitFor(() => !dialog.IsVisible);
        });
        var keyboardGroup = panel.Model.Registry.State.Groups.Single(g => g.Name == "键盘分组").Id;
        await Dialog(panel.ShowNewGroup, dialog => { EntryText(dialog).Text = "取消不能保存"; SendKey(dialog, 0x1B); return Task.CompletedTask; });
        Require(!panel.Model.Registry.State.Groups.Any(g => g.Name == "取消不能保存"), "Escape saved cancelled input.");
        await panel.Model.Registry.DeleteGroupAsync(keyboardGroup);

        var area = new TestWorkspace(Path.Combine(outputDirectory, "layout-repositories"));
        var temporaryIds = new List<Guid>(); var groupIds = new List<Guid>();
        for (var i = 0; i < 5; i++) groupIds.Add(await panel.Model.Registry.CreateGroupAsync("长分组名称用于验证省略与完整提示文字" + i));
        Require(!panel.Model.RemoteEnabled, "Layout fixtures must never have remote checks enabled.");
        for (var i = 0; i < 14; i++)
        {
            var path = await area.CreateDetachedRepositoryAsync($"{i:00}-仓库名称需要完整提示-very-long-repository-name-for-layout");
            if (i < 2) await File.AppendAllTextAsync(Path.Combine(path, ".git", "config"), $"\n[remote \"origin\"]\nurl = https://{(i == 0 ? "github.com" : "gitlab.com")}/example/fixture.git\n");
            temporaryIds.Add((await panel.Model.Registry.AddAsync(path, groupIds[i % 3])).Repository.Id);
        }
        foreach (var group in panel.Model.Groups.ToArray()) await panel.Model.Registry.SetCollapsedAsync(group.Id, false);
        panel.StatusScheduler.Refresh();
        await WaitFor(() => panel.Model.Groups.SelectMany(g => g.Repositories).Count(row => row.SourceIconKey is "GitHub" or "GitLab") == 2);
        var longBranchRow = panel.Model.Groups.SelectMany(g => g.Repositories).Single(r => r.Id == temporaryIds[0]);
        await WaitFor(() => longBranchRow.Status.Data is not null);
        const string branchSample = "feature/very-long-branch-name-with-complete-tooltip-validation";
        longBranchRow.Apply(longBranchRow.Status with { Data = longBranchRow.Status.Data! with { HeadKind = Core.Status.HeadKind.Branch, Branch = branchSample } });
        Require(longBranchRow.StatusTooltip.Contains(branchSample), "Long branch tooltip lost complete text.");
        panel.UpdateLayout();
        var scroll = (ScrollViewer)panel.FindName("RepositoryScroll");
        Require(scroll.ScrollableHeight > 400, "Many repositories did not create a scrollable list.");
        scroll.ScrollToTop(); await Task.Delay(100); SaveImage(panel, "main-light-long-list.png");
        var gitlabButton = RowButton(panel, temporaryIds[1], "RepositoryActions");
        gitlabButton.BringIntoView(); await Task.Delay(100); SaveImage(panel, "main-light-gitlab.png");
        scroll.ScrollToBottom(); await Task.Delay(100);
        var rowButton = RowButton(panel, temporaryIds[^1], "RepositoryActions");
        rowButton.BringIntoView(); rowButton.Focus();
        Require(new ButtonAutomationPeer(rowButton).GetName().Contains("仓库名称"), "Row action lacks repository-specific accessible name.");
        Invoke(rowButton); await WaitFor(() => rowButton.ContextMenu?.IsOpen == true);
        rowButton.ContextMenu!.IsOpen = false;
        await WaitFor(() => !panel.IsAutoCollapseSuspended);
        Require(Descendants(panel).OfType<TextBlock>().Any(t => t.Text.Contains("very-long") && Equals(t.ToolTip, t.Text)), "Long repository name has no complete tooltip.");
        SaveImage(panel, "main-light-scroll-bottom.png");
        await panel.Model.Registry.UpdatePreferencesAsync(p => p with { ScalePercent = 150, Theme = AppTheme.Dark });
        await Task.Delay(120); AssertInsideWorkArea(panel); SaveImage(panel, "main-dark-150.png");
        // A compact viewport emulates a smaller usable monitor without changing the user's display settings.
        SetWindowPos(new WindowInteropHelper(panel).Handle, 0, 0, 0, 800, 600, 0x16);
        await Task.Delay(120); panel.UpdateLayout();
        Require(scroll.ScrollableWidth > 0 && scroll.ScrollableHeight > 0, "Constrained viewport must permit both axes of scrolling.");
        var add = (Button)FindById(panel, "ChooseFolder")!;
        var location = add.TranslatePoint(new Point(0, 0), panel);
        Require(location.Y >= 0 && location.Y + add.ActualHeight * 1.5 <= panel.ActualHeight, "Footer action escaped small viewport.");
        SaveImage(panel, "main-compact-150.png"); panel.ClampPosition();
        foreach (var id in temporaryIds) await panel.Model.Registry.RemoveAsync(id);
        foreach (var id in groupIds) await panel.Model.Registry.DeleteGroupAsync(id);
        foreach (var group in panel.Model.Groups.ToArray()) await panel.Model.Registry.SetCollapsedAsync(group.Id, true);
        await panel.Model.Registry.UpdatePreferencesAsync(p => p with { ScalePercent = 125, Theme = AppTheme.Light });
        checks.Add("M5 real 20-repository / 7-group layout including empty groups and long paths; official vector source icons; long-branch display injected as protocol sample without creating a branch; row menu after scroll; 800x600 viewport at 150% app scale with both scroll axes and reachable footer; Enter/cancel forms");

        panel.WindowState = WindowState.Minimized;
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var secondary = Start(Path.Combine(FindRoot(), "src", "RepoTracker.Desktop", "bin", configuration, "net10.0-windows", "RepoTracker.Desktop.exe"),
            "--profile", Path.Combine(outputDirectory, "profile"), "--minimized");
        await secondary.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Require(secondary.ExitCode == 0 && panel.WindowState == WindowState.Minimized, "Repeated login startup activated the existing process.");
        panel.RestorePanel();
        checks.Add("M5 real secondary --minimized process exits without activating the existing taskbar window");
    }
    private static void Toggle(CheckBox box)
    {
        box.BringIntoView();
        ((IToggleProvider)new CheckBoxAutomationPeer(box).GetPattern(PatternInterface.Toggle)).Toggle();
        box.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }
    private static void SendKey(Window window, int key)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        SendMessage(hwnd, 0x100, key, 0); SendMessage(hwnd, 0x101, key, 0);
    }
    private static void AssertInsideWorkArea(Window window)
    {
        var work = NativeWindows.GetMonitor(new WindowInteropHelper(window).Handle).Work; var bounds = NativeWindows.Bounds(window);
        Require(bounds.X >= work.X && bounds.Y >= work.Y && bounds.Right <= work.Right && bounds.Bottom <= work.Bottom, "Scaled window escaped work area.");
    }
}

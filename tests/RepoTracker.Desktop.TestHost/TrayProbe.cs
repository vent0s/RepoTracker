using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using RepoTracker.Core.Localization;
using RepoTracker.Desktop.Windows;

namespace RepoTracker.Desktop.TestHost;

internal sealed partial class WindowProbe
{
    private async Task CheckTray(MainWindow panel)
    {
        var tray = application.TrayIcon ?? throw new InvalidOperationException("Tray was not created.");
        var handle = tray.Handle;
        var data = new TestNotifyIconData { Size = (uint)Marshal.SizeOf<TestNotifyIconData>(), Window = handle, Id = TrayIcon.IconId };
        // A flags=0 MODIFY confirms the shell owns this icon without changing its fields.
        // GetRect cannot require a rectangle for an icon in a closed overflow flyout.
        await WaitFor(() => Shell_NotifyIcon(1, ref data));
        Require(panel.ShowInTaskbar, "Tray replaced the taskbar entry.");
        if (Environment.GetEnvironmentVariable("REPOTRACKER_TEST_MINIMIZED") == "1")
            Require(panel.WindowState == WindowState.Minimized && !panel.IsActive, "Tray creation activated minimized startup.");
        checks.Add("Real shell notification icon exists, including minimized startup; taskbar entry preserved.");

        await panel.Model.SetKeepExpandedAsync(true);
        foreach (var notification in new[] { 0x400, 0x401 }) // NIN_SELECT and NIN_KEYSELECT
        {
            panel.WindowState = WindowState.Minimized;
            PostMessage(handle, TrayIcon.CallbackMessage, 0, (nint)((TrayIcon.IconId << 16) | notification));
            await WaitFor(() => panel.WindowState == WindowState.Normal);
        }
        checks.Add("Mouse and keyboard shell activation callbacks restore the minimized panel.");

        // Remove only this test process's icon, then replay the broadcast to its own hidden window.
        // Explorer and other programs are never restarted or modified.
        Require(Shell_NotifyIcon(2, ref data), "Could not remove isolated test icon.");
        Require(!Shell_NotifyIcon(1, ref data), "Icon still exists after removal.");
        var taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        SendMessage(handle, taskbarCreated, 0, 0);
        await WaitFor(() => Shell_NotifyIcon(1, ref data));
        SendMessage(handle, taskbarCreated, 0, 0);
        Require(Shell_NotifyIcon(1, ref data), "Duplicate recreation lost the icon.");
        checks.Add("Explorer recreation notification restores a missing icon; repeated notification preserves it.");

        // A cancelled tray menu must not restore a minimized panel or leak the collapse guard.
        panel.WindowState = WindowState.Minimized;
        await UseTrayMenu(handle, null);
        Require(panel.WindowState == WindowState.Minimized && !panel.IsAutoCollapseSuspended, "Menu dismissal changed window state or leaked suspension.");
        await UseTrayMenu(handle, Text.Get("Tray.Open"));
        await WaitFor(() => panel.WindowState == WindowState.Normal);

        var settingsTask = WatchSettings();
        await UseTrayMenu(handle, Text.Get("Settings.Window"));
        await settingsTask;
        Require(!panel.IsAutoCollapseSuspended, "Tray settings leaked the collapse guard.");
        checks.Add("Real localized native menu: dismissal, open, settings, modal reuse and exit; settings explain overflow and per-computer startup.");
        await UseTrayMenu(handle, Text.Get("App.Exit"));

        async Task WatchSettings()
        {
            SettingsWindow? settings = null;
            try
            {
                await WaitFor(() => application.Windows.OfType<SettingsWindow>().Any(w => w.IsVisible));
                settings = application.Windows.OfType<SettingsWindow>().Single(w => w.IsVisible);
                var help = (TextBlock)FindById(settings, "TrayHelp")!;
                Require(help.Text == Text.Get("Tray.Help"), "Tray visibility help was not translated.");
                Require(((CheckBox)settings.FindName("StartupSwitch")).IsChecked == false, "Tray automatically enabled login startup.");
                PostMessage(handle, TrayIcon.CallbackMessage, 0, (nint)((TrayIcon.IconId << 16) | 0x400));
                await Task.Delay(150);
                Require(application.Windows.OfType<SettingsWindow>().Count() == 1 && settings.IsVisible, "Tray opened another modal dialog.");
                await UseTrayMenu(handle, null, unavailable: true);
                ((ScrollViewer)settings.FindName("CheckScroll")).ScrollToBottom();
                settings.UpdateLayout();
                SaveImage(settings, "tray-settings.png");
            }
            finally { settings?.Close(); }
        }
    }

    private static async Task UseTrayMenu(nint handle, string? action, bool unavailable = false)
    {
        PostMessage(handle, TrayIcon.CallbackMessage, (100 << 16) | 100, (nint)((TrayIcon.IconId << 16) | 0x7B));
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in new[] { "--tray-menu", Environment.ProcessId.ToString(), handle.ToString(), action ?? "", unavailable.ToString() })
            start.ArgumentList.Add(argument);
        using var driver = Process.Start(start)!;
        try { await driver.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)); }
        finally { if (!driver.HasExited) { driver.Kill(); await driver.WaitForExitAsync(); } }
        Require(driver.ExitCode == 0, await driver.StandardError.ReadToEndAsync());
    }

    internal static async Task<int> DriveTrayMenu(int processId, nint handle, string action, bool unavailable)
    {
        // UI Automation runs in a separate process, just as in the published package probe.
        var expected = new[] { Text.Get("Tray.Open"), Text.Get("Settings.Window"), Text.Get("App.Exit") };
        try
        {
            var invoked = false;
            try
            {
                AutomationElement? menu = null;
                var clock = Stopwatch.StartNew();
                while (menu is null && clock.Elapsed < TimeSpan.FromSeconds(8))
                {
                    menu = AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
                        new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu)));
                    if (menu is null) await Task.Delay(50);
                }
                Require(menu is not null, "Native tray context menu did not appear.");
                var items = menu!.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem))
                    .Cast<AutomationElement>().ToArray();
                Require(items.Select(item => item.Current.Name).SequenceEqual(expected), "Tray menu labels or language differ.");
                Require(items[0].Current.IsEnabled && items.Skip(1).All(item => item.Current.IsEnabled != unavailable), "Modal tray command availability is incorrect.");
                if (action.Length > 0)
                {
                    Require(items.Single(item => item.Current.Name == action).Current.IsEnabled, "Tray command is disabled.");
                    // Exercise the native menu's WM_COMMAND contract without global mouse/keyboard input.
                    PostMessage(handle, 0x0111, Array.IndexOf(expected, action) + 1, 0);
                    invoked = true;
                }
            }
            finally
            {
                if (!invoked) PostMessage(handle, 0x001F, 0, 0); // Cancel or release the native loop on assertion failure.
            }
            await Task.Delay(150); // Allow the native selection / cancellation to reach the menu loop.
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TestNotifyIconData
    {
        public uint Size; public nint Window; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string? Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string? InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref TestNotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
}

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RepoTracker.Desktop.Windows;

namespace RepoTracker.Desktop.TestHost;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception error)
        {
            WriteStartupFailure(args, error);
            return 1;
        }
    }

    private static void WriteStartupFailure(string[] args, Exception error)
    {
        if (args.Length == 2 && args[0] == "--profile")
            File.WriteAllText(Path.Combine(Directory.GetParent(args[1])!.FullName, "report.json"),
                JsonSerializer.Serialize(new { Passed = false, Error = error.ToString() }));
    }

    private static int Run(string[] args)
    {
        if (args.Length == 2 && args[0] == "--foreign-window")
        {
            var app = new Application();
            var window = new Window { Title = "RepoTracker focus test", Width = 260, Height = 100, ShowInTaskbar = false,
                Content = new TextBlock { Text = "正在验证应用切换，稍后自动关闭", Margin = new Thickness(12) } };
            window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle).AddHook(FocusHandshake);
            window.Loaded += (_, _) =>
            {
                NativeWindows.Activate(window);
                File.WriteAllText(args[1] + ".pending", new WindowInteropHelper(window).Handle.ToString());
                File.Move(args[1] + ".pending", args[1]);
            };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            timer.Tick += (_, _) => app.Shutdown();
            timer.Start();
            return app.Run(window);
        }
        var probe = new App { StartupRegistrationFactory = profile => new FixtureStartupRegistration(profile) };
        probe.DispatcherUnhandledException += (_, e) =>
        {
            WriteStartupFailure(args, e.Exception);
            e.Handled = true;
            probe.Shutdown(1);
        };
        probe.InitializeComponent();
        probe.Startup += (_, _) => probe.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            () => new WindowProbe(probe, Directory.GetParent(StartupOptions.ProfileDirectory(args))!.FullName).Run());
        return probe.Run();
    }

    private static nint FocusHandshake(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x8001) { AllowSetForegroundWindow((uint)wParam); handled = true; }
        return 0;
    }
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
}

internal sealed partial class WindowProbe(App application, string outputDirectory)
{
    private readonly List<string> checks = [];
    private readonly List<Process> children = [];
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public async void Run()
    {
        var panel = (MainWindow)application.MainWindow;
        object? error = null;
        var exitCode = 0;
        try
        {
            await panel.Initialization;
            if (Environment.GetEnvironmentVariable("REPOTRACKER_TEST_MINIMIZED") == "1")
            {
                Require(panel.WindowState == WindowState.Minimized && panel.ShowInTaskbar && !panel.IsActive, "Login startup must remain minimized without activation.");
                checks.Add("M5 actual --minimized process starts in taskbar without activation");
                application.Shutdown(); return;
            }
            if (Environment.GetEnvironmentVariable("REPOTRACKER_TEST_RELOAD") == "1")
            {
                await CheckReload(panel);
                application.Shutdown();
                return;
            }
            await panel.Model.SetKeepExpandedAsync(true);
            panel.RestorePanel();
            await FocusOwnWindow(new WindowInteropHelper(panel).Handle);
            await Task.Delay(200);
            var hwnd = new WindowInteropHelper(panel).Handle;
            Require(panel.ShowInTaskbar && GetWindow(hwnd, 4) == 0 && (GetWindowLongPtr(hwnd, -20).ToInt64() & 0x80) == 0,
                "Main window is not eligible for the taskbar / Alt+Tab.");
            var monitor = NativeWindows.GetMonitor(hwnd);
            var bounds = NativeWindows.Bounds(panel);
            Require(bounds.X >= monitor.Work.X - 1 && bounds.Y >= monitor.Work.Y - 1 &&
                bounds.Right <= monitor.Work.Right + 1 && bounds.Bottom <= monitor.Work.Bottom + 1, "Panel is outside work area.");
            checks.Add($"taskbar style, unowned window, physical bounds {bounds}; DPI {monitor.Dpi}; monitor {monitor.Monitor}");
            checks.Add($"initial foreground={NativeWindows.GetForegroundWindow()}, panel={hwnd}, active={panel.IsActive}");
            SaveImage(panel, "main.png");
            SaveImage(panel, "main-150.png", 1.5);
            SaveImage(panel, "main-200.png", 2);

            var monitors = new List<NativeRect>();
            EnumDisplayMonitors(0, 0, (_, _, rect, _) =>
            {
                monitors.Add(Marshal.PtrToStructure<NativeRect>(rect));
                return true;
            }, 0);
            foreach (var target in monitors)
            {
                SetWindowPos(hwnd, 0, target.Left + 20, target.Top + 20, 0, 0, 0x15);
                await Task.Delay(150);
                panel.ClampPosition();
                var current = NativeWindows.GetMonitor(hwnd);
                var placed = NativeWindows.Bounds(panel);
                Require(current.Monitor.X == target.Left && current.Monitor.Y == target.Top,
                    "Window did not move to requested monitor.");
                Require(placed.X >= current.Work.X && placed.Y >= current.Work.Y &&
                    placed.Right <= current.Work.Right && placed.Bottom <= current.Work.Bottom, "Cross-monitor placement escaped work area.");
                checks.Add($"physical monitor placement: {current.Monitor}, DPI={current.Dpi}, panel={placed}");
            }
            SetWindowPos(hwnd, 0, (int)bounds.X, (int)bounds.Y, 0, 0, 0x15);
            panel.ClampPosition();

            Invoke(panel, "Collapse");
            await Task.Delay(150);
            Require(panel.WindowState == WindowState.Minimized && panel.ShowInTaskbar, "Collapse must minimize without hiding taskbar entry.");
            SendMessage(hwnd, 0x112, 0xF120, 0); // SC_RESTORE, as used by the shell.
            await Task.Delay(150);
            Require(panel.WindowState == WindowState.Normal, "System restore failed.");
            checks.Add("collapse button and Windows SC_RESTORE");

            panel.RestorePanel();
            ((Button)FindById(panel, "KeepExpanded")!).Focus();
            SendMessage(hwnd, 0x100, 0x1B, 0); // Keydown/up delivered only to this test window.
            SendMessage(hwnd, 0x101, 0x1B, 0);
            await Task.Delay(150);
            Require(panel.WindowState == WindowState.Minimized, "Escape did not collapse panel.");
            checks.Add("Escape keyboard input");

            var root = FindRoot();
#if DEBUG
            const string configuration = "Debug";
#else
            const string configuration = "Release";
#endif
            var secondary = Start(Path.Combine(root, "src", "RepoTracker.Desktop", "bin", configuration, "net10.0-windows", "RepoTracker.Desktop.exe"),
                "--profile", Path.Combine(outputDirectory, "profile"));
            await secondary.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(150);
            Require(secondary.ExitCode == 0 && panel.WindowState == WindowState.Normal, "Second process did not reactivate primary.");
            checks.Add("real second application process: IPC activation and clean secondary exit");

            await panel.Model.SetKeepExpandedAsync(false);
            panel.RestorePanel();
            var settingsSeen = false;
            var settingsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            settingsTimer.Tick += (_, _) =>
            {
                var settings = application.Windows.OfType<SettingsWindow>().FirstOrDefault();
                if (settings is null) return;
                settingsSeen = settings.Owner == panel && !settings.ShowInTaskbar && panel.WindowState == WindowState.Normal && panel.IsAutoCollapseSuspended;
                SaveImage(settings, "settings.png");
                settings.Close();
                settingsTimer.Stop();
            };
            settingsTimer.Start();
            panel.ShowSettings();
            settingsTimer.Stop();
            Require(settingsSeen && !panel.IsAutoCollapseSuspended, "Owned settings collapsed main window or leaked suppression.");
            checks.Add("real owned settings dialog, close, focus guard release");

            panel.RestorePanel();
            var pickerSeen = false;
            var pickerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            pickerTimer.Tick += (_, _) => EnumThreadWindows(GetCurrentThreadId(), (dialog, _) =>
            {
                var name = new StringBuilder(128);
                GetClassName(dialog, name, name.Capacity);
                if (name.ToString() == "#32770")
                {
                    pickerSeen = GetWindow(dialog, 4) == hwnd && panel.WindowState == WindowState.Normal && panel.IsAutoCollapseSuspended;
                    PostMessage(dialog, 0x10, 0, 0);
                    pickerTimer.Stop();
                }
                return true;
            }, 0);
            pickerTimer.Start();
            panel.ChooseFolder();
            pickerTimer.Stop();
            Require(pickerSeen && !panel.IsAutoCollapseSuspended, "Native folder picker ownership or focus guard failed.");
            checks.Add("real native folder picker: owner, no premature collapse, cancellation");

            panel.RestorePanel();
            Invoke(panel, "More");
            await Task.Delay(200);
            Require(panel.IsAutoCollapseSuspended && panel.WindowState == WindowState.Normal, "Menu did not protect focus.");
            var menu = ((Button)FindById(panel, "More")!).ContextMenu;
            menu.IsOpen = false;
            for (var retry = 0; retry < 20 && panel.IsAutoCollapseSuspended; retry++) await Task.Delay(50);
            Require(!panel.IsAutoCollapseSuspended, "Menu focus guard leaked.");
            checks.Add("real context menu open/close");

            await panel.Model.SetKeepExpandedAsync(true);
            panel.RestorePanel();
            await FocusOwnWindow(hwnd);
            var ready = Path.Combine(outputDirectory, "foreign-window.txt");
            var foreign = Start(Environment.ProcessPath!, "--foreign-window", ready);
            for (var retry = 0; retry < 50 && !File.Exists(ready); retry++) await Task.Delay(100);
            Require(File.Exists(ready), "Foreign test window failed to start.");
            var foreignHwnd = nint.Parse(File.ReadAllText(ready));
            AllowSetForegroundWindow((uint)foreign.Id);
            var focusTransferred = SetForegroundWindow(foreignHwnd);
            await Task.Delay(400);
            Require(NativeWindows.GetForegroundWindow() == foreignHwnd,
                $"OS refused focus transfer: result={focusTransferred}, foreground={NativeWindows.GetForegroundWindow()}, foreign={foreignHwnd}, panel={hwnd}, active={panel.IsActive}.");
            Require(panel.WindowState == WindowState.Normal, "Keep expanded did not survive foreign focus.");
            checks.Add("keep expanded survives actual focus transfer to another process");

            SendMessage(foreignHwnd, 0x8001, Environment.ProcessId, 0);
            panel.RestorePanel();
            await FocusOwnWindow(hwnd);
            await WaitFor(() => NativeWindows.GetForegroundWindow() == hwnd);
            Require(NativeWindows.GetForegroundWindow() == hwnd, "Focus test could not first activate the primary panel.");
            await panel.Model.SetKeepExpandedAsync(false);
            SetForegroundWindow(foreignHwnd);
            await Task.Delay(400);
            Require(panel.WindowState == WindowState.Minimized,
                $"Default focus loss did not collapse panel: foreground={NativeWindows.GetForegroundWindow()}, foreign={foreignHwnd}, active={panel.IsActive}, suspended={panel.IsAutoCollapseSuspended}.");
            checks.Add("default focus loss minimizes; taskbar entry remains");
            PostMessage(foreignHwnd, 0x10, 0, 0);
            await foreign.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            await panel.Model.SetKeepExpandedAsync(true);
            panel.RestorePanel();
            await CheckRepositoryManagement(panel);
            await CheckPreferencesAndLayout(panel);
            Invoke(panel, "More");
            await Task.Delay(100);
            var exit = menu.Items.OfType<MenuItem>().Single(item => (string)item.Header == "退出 RepoTracker");
            ((IInvokeProvider)new MenuItemAutomationPeer(exit).GetPattern(PatternInterface.Invoke)).Invoke();
            checks.Add("exit menu command invoked; process shutdown checked by parent test");
        }
        catch (Exception failure) { error = failure.ToString(); exitCode = 1; }
        finally
        {
            foreach (var child in children)
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(5000); }
                child.Dispose();
            }
            var reportName = Environment.GetEnvironmentVariable("REPOTRACKER_TEST_MINIMIZED") == "1" ? "minimized-report.json" : Environment.GetEnvironmentVariable("REPOTRACKER_TEST_RELOAD") == "1" ? "reload-report.json" : "report.json";
            File.WriteAllText(Path.Combine(outputDirectory, reportName), JsonSerializer.Serialize(new
            {
                Passed = exitCode == 0, Os = Environment.OSVersion.ToString(), Runtime = Environment.Version.ToString(),
                Checks = checks, Error = error,
                NotCovered = new[] { "Windows 11", "physical 150% / 200% DPI and mixed-DPI transitions", "taskbar actual mouse click / pinning / auto-hide", "monitor hot unplug", "real Windows logon / current-user Run writes; UI uses isolated startup adapter" }
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (exitCode != 0) application.Shutdown(exitCode);
        }
    }

    private Process Start(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        var process = Process.Start(info)!;
        children.Add(process);
        return process;
    }

    private static Task FocusOwnWindow(nint hwnd) => Task.Run(() => AutomationElement.FromHandle(hwnd).SetFocus());

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RepoTracker.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Solution not found.");
    }

    private static DependencyObject? FindById(DependencyObject root, string id)
    {
        if (AutomationProperties.GetAutomationId(root) == id) return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindById(VisualTreeHelper.GetChild(root, i), id) is { } found) return found;
        return null;
    }

    private static void Invoke(Window window, string id)
    {
        var button = (Button)(FindById(window, id) ?? throw new InvalidOperationException("Missing control: " + id));
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private void SaveImage(Window window, string fileName, double scale = 1)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var sx = content.LayoutTransform.Value.M11; var sy = content.LayoutTransform.Value.M22;
        var width = content.ActualWidth * sx + content.Margin.Left + content.Margin.Right;
        var height = content.ActualHeight * sy + content.Margin.Top + content.Margin.Bottom;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            var offset = VisualTreeHelper.GetOffset(content);
            var brush = new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(offset.X, offset.Y, content.ActualWidth * sx, content.ActualHeight * sy), Stretch = Stretch.Fill };
            drawing.DrawRectangle(brush, null,
                new Rect(content.Margin.Left, content.Margin.Top, content.ActualWidth * sx, content.ActualHeight * sy));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale),
            (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(outputDirectory, fileName));
        encoder.Save(file);
    }

    private delegate bool EnumWindowsCallback(nint hwnd, nint parameter);
    private delegate bool MonitorCallback(nint monitor, nint dc, nint rect, nint parameter);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, EnumWindowsCallback callback, nint parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder value, int length);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
}

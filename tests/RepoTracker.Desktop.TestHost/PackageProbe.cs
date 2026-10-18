using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using RepoTracker.Tests.Shared;

namespace RepoTracker.Desktop.TestHost;

// Drives the published apphost through out-of-process UI Automation; never substitutes TestHost for the product.
internal static class PackageProbe
{
    private static string phasePath = "";
    public static int Run(string originalExe, string relocatedExe, string evidenceDirectory)
    {
        phasePath = Path.Combine(evidenceDirectory, "phase.txt");
        var checks = new List<string>();
        var modules = new List<object>();
        var displays = new List<object>();
        var profile = Path.Combine(evidenceDirectory, "profile 中文");
        var statePath = Path.Combine(profile, "state.json");
        Process? child = null;
        try
        {
            Require(!Directory.Exists(profile), "Package probe requires a new isolated profile.");
            child = Start(originalExe, profile);
            var panel = Panel(child);
            WaitForTrayEvent(profile, "tray.registered", 1);
            var expected = RepoTracker.Core.Localization.Text.ResolveCulture("system", CultureInfo.CurrentUICulture).Name == "zh-Hans" ? "窗口设置" : "Settings";
            var actualName = ById(panel, "Settings").Current.Name;
            Require(actualName == expected, $"First launch language: expected '{expected}', got '{actualName}'.");
            Require(Directory.Exists(Path.Combine(profile, "logs")), "First launch did not create the external profile log directory.");
            CheckModules(child, originalExe, modules);
            Invoke(ById(panel, "KeepExpanded"));
            Wait(() => File.Exists(statePath), "First preference save");
            Wait(() => ReadState(statePath).GetProperty("preferences").GetProperty("keepExpanded").GetBoolean(), "Keep preference save");
            CheckDisplays(panel, displays);
            checks.Add("Published window moved across every attached physical monitor; work-area bounds and actual window DPI recorded.");
            Require(ReadState(statePath).GetProperty("preferences").GetProperty("language").GetString() == "system", "Default language was not persisted as system.");
            SaveLanguage(child, panel, "English", "en", statePath);
            Stop(child); child.Dispose(); child = null;
            WaitForTrayEvent(profile, "tray.removed", 1);
            checks.Add("Fresh extracted apphost: system display language, external profile/log creation, UI save and graceful exit.");

            // Import through the real published Add command, then verify the bundled runtime performs real Git collection.
            var fixture = new TestWorkspace(Path.Combine(evidenceDirectory, "repositories"));
            var repository = fixture.CreateDetachedRepositoryAsync("发布 fixture").GetAwaiter().GetResult();
            var nested = fixture.CreateDetachedRepositoryAsync(Path.Combine("发布 fixture", "子仓库 nested")).GetAwaiter().GetResult();
            var nestedHead = File.ReadAllText(Path.Combine(nested, ".git", "HEAD"));
            var nestedKeep = File.ReadAllBytes(Path.Combine(nested, "keep.txt"));
            var head = File.ReadAllText(Path.Combine(repository, ".git", "HEAD"));
            var keep = File.ReadAllBytes(Path.Combine(repository, "keep.txt"));
            child = Start(originalExe, profile);
            panel = Panel(child);
            WaitForTrayEvent(profile, "tray.registered", 2);
            Require(ById(panel, "Settings").Current.Name == "Settings", "Saved English override was not loaded.");
            Phase("Add repository through the published window");
            Invoke(ById(panel, "ChooseFolder"));
            var entry = OwnedDialog(panel, "EntryDialog");
            Require(((TogglePattern)ById(entry, "ScanChildrenOnAdd").GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On,
                "Packaged Add did not enable child discovery by default.");
            ((ValuePattern)ById(entry, "EntryText").GetCurrentPattern(ValuePattern.Pattern)).SetValue(repository);
            Invoke(ById(entry, "Submit"));
            var discovery = OwnedDialog(entry, "RepositoryDiscovery");
            Wait(() => ById(discovery, "ImportRepositories").Current.IsEnabled, "Packaged default nested scan");
            Require(discovery.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "SelectRepository")).Count == 2,
                "Packaged discovery did not find root and child.");
            Invoke(ById(discovery, "ImportRepositories"));
            Wait(() => panel.Current.IsEnabled && ReadState(statePath).GetProperty("repositories").GetArrayLength() == 2,
                "Packaged parent and child imported through Add");
            checks.Add("Published Add defaults to child discovery; root and nested repository selected and imported through the real preview window.");
            Wait(() => HasText(panel, "Detached HEAD") && HasText(panel, "Modified") && HasText(panel, "发布 fixture"), "Packaged real Git status");
            Require(HasText(panel, "子仓库 nested"), "Packaged new child was not visible.");
            Invoke(ById(panel, "ToggleRepositoryChildren"));
            Wait(() => !HasText(panel, "子仓库 nested") && ReadState(statePath).GetProperty("repositories")[0].GetProperty("childrenCollapsed").GetBoolean(),
                "Packaged initial collapse saved");
            Invoke(ById(panel, "ToggleRepositoryChildren"));
            Wait(() => HasText(panel, "子仓库 nested") && !ReadState(statePath).GetProperty("repositories")[0].GetProperty("childrenCollapsed").GetBoolean(),
                "Packaged tree expanded and saved");
            Invoke(ById(panel, "ToggleRepositoryChildren"));
            Wait(() => !HasText(panel, "子仓库 nested") && ReadState(statePath).GetProperty("repositories")[0].GetProperty("childrenCollapsed").GetBoolean(),
                "Packaged tree collapsed and saved");
            checks.Add("Published nested repository tree: stored collapsed state, real expand/collapse UI commands, child visibility and atomic persistence.");
            using (var secondary = Start(originalExe, profile, minimized: true))
            {
                Require(secondary.WaitForExit(10000) && secondary.ExitCode == 0 && !child.HasExited,
                    "Packaged secondary instance did not exit cleanly.");
            }
            SaveLanguage(child, panel, "简体中文", "zh-Hans", statePath);
            Stop(child); child.Dispose(); child = null;
            WaitForTrayEvent(profile, "tray.removed", 2);
            checks.Add("Restart: English resources, preserved settings, same-profile single instance, Chinese override saved through UI.");

            var before = File.ReadAllBytes(statePath);
            child = Start(relocatedExe, profile, minimized: true);
            // Wait for a real top-level window before checking the startup state.
            var minimizedPanel = FindPanel(child);
            WaitForTrayEvent(profile, "tray.registered", 3);
            Require(((WindowPattern)minimizedPanel.GetCurrentPattern(WindowPattern.Pattern)).Current.WindowVisualState == WindowVisualState.Minimized,
                "Packaged --minimized startup did not remain minimized.");
            panel = Panel(child);
            Require(ById(panel, "Settings").Current.Name == "窗口设置", "Chinese satellite resources were not loaded after relocation.");
            Wait(() => HasText(panel, "分离 HEAD") && HasText(panel, "有修改") && HasText(panel, "发布 fixture"), "Relocated Chinese Git status");
            Require(!HasText(panel, "子仓库 nested"), "Relocation lost nested collapse state.");
            CheckModules(child, relocatedExe, modules);
            Stop(child); child.Dispose(); child = null;
            WaitForTrayEvent(profile, "tray.removed", 3);
            checks.Add("Published Shell registration succeeded on fresh, restarted and relocated minimized launches; each graceful exit removed its tray icon.");
            Require(before.SequenceEqual(File.ReadAllBytes(statePath)), "Relocation/restart modified existing preferences.");
            Require(File.Exists(statePath + ".bak"), "Atomic saves did not retain the configuration backup.");
            Require(head == File.ReadAllText(Path.Combine(repository, ".git", "HEAD")) &&
                keep.SequenceEqual(File.ReadAllBytes(Path.Combine(repository, "keep.txt"))), "Packaged collection changed the fixture.");
            Require(nestedHead == File.ReadAllText(Path.Combine(nested, ".git", "HEAD")) &&
                nestedKeep.SequenceEqual(File.ReadAllBytes(Path.Combine(nested, "keep.txt"))), "Packaged collection changed the child fixture.");
            Require(string.IsNullOrWhiteSpace(TestWorkspace.GitAsync(repository, "for-each-ref", "--format=%(refname)", "refs/heads").GetAwaiter().GetResult()),
                "Fixture unexpectedly acquired a named branch.");
            checks.Add("Real Git detached fixture: English and Chinese status after restart/relocation; user name and file/HEAD unchanged; no named branch created.");
            checks.Add("Upgrade layout rehearsal: second extraction in a different path, minimized launch, Chinese satellite, unchanged external state/backup.");
            WriteReport(true, null);
            return 0;
        }
        catch (Exception error) { WriteReport(false, error.ToString()); return 1; }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(10000); }
                child.Dispose();
            }
        }

        void WriteReport(bool passed, string? error) => File.WriteAllText(Path.Combine(evidenceDirectory, "package-report.json"),
            JsonSerializer.Serialize(new { Passed = passed, Error = error, Checks = checks, Modules = modules, Displays = displays,
                OperatingSystem = Environment.OSVersion.VersionString, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                SystemDisplayCulture = CultureInfo.CurrentUICulture.Name, Profile = profile,
                Scope = "Real published process; no user Run registry changes or Windows logon performed." }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WaitForTrayEvent(string profile, string eventName, int count) => Wait(() =>
        Directory.GetFiles(Path.Combine(profile, "logs"), "*.log")
            .Sum(path =>
            {
                using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                return reader.ReadToEnd().Split('\n').Count(line => line.TrimEnd('\r').EndsWith(" " + eventName, StringComparison.Ordinal));
            }) >= count,
        "Published " + eventName);

    private static Process Start(string executable, string profile, bool minimized = false)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.ArgumentList.Add("--profile"); start.ArgumentList.Add(profile);
        if (minimized) start.ArgumentList.Add("--minimized");
        start.Environment["DOTNET_ROOT"] = Path.Combine(profile, "no-shared-runtime");
        start.Environment["DOTNET_ROOT_X64"] = start.Environment["DOTNET_ROOT"];
        start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        return Process.Start(start) ?? throw new InvalidOperationException("Published process did not start.");
    }

    private static AutomationElement FindPanel(Process process)
    {
        AutomationElement? panel = null;
        Phase("Find published main window");
        Wait(() =>
        {
            Require(!process.HasExited, "Published app exited before its window appeared.");
            process.Refresh();
            var handle = process.MainWindowHandle;
            panel = handle == 0 ? null : AutomationElement.FromHandle(handle);
            return panel is not null;
        }, "Published main window");
        return panel!;
    }

    private static AutomationElement Panel(Process process)
    {
        var panel = FindPanel(process);
        Phase("Restore published main window");
        ((WindowPattern)panel.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Normal);
        Phase("Focus published main window");
        SetForegroundWindow(panel.Current.NativeWindowHandle);
        Wait(() => FindById(panel, "Settings") is { Current.IsEnabled: true }, "Initialized main window");
        return panel;
    }

    private static AutomationElement OwnedDialog(AutomationElement parent, string id)
    {
        AutomationElement? dialog = null;
        Wait(() =>
        {
            var owner = (nint)parent.Current.NativeWindowHandle;
            var popup = GetWindow(owner, 6);
            dialog = popup == 0 || popup == owner ? null : AutomationElement.FromHandle(popup);
            return dialog?.Current.AutomationId == id;
        }, id);
        return dialog!;
    }

    private static void SaveLanguage(Process process, AutomationElement panel, string choice, string code, string statePath)
    {
        Phase("Open settings: " + code);
        Invoke(ById(panel, "Settings"));
        AutomationElement? settings = null;
        Phase("Find owned settings window: " + code);
        Wait(() =>
        {
            var owner = (nint)panel.Current.NativeWindowHandle;
            var popup = GetWindow(owner, 6); // GW_ENABLEDPOPUP: only this product window's active owned dialog.
            settings = popup == 0 || popup == owner ? null : AutomationElement.FromHandle(popup);
            if (settings?.Current.AutomationId != "WindowSettings") settings = null;
            return settings is not null;
        }, "Settings window");
        Phase("Expand language choices: " + code);
        var combo = ById(settings!, "LanguageChoice");
        ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        Phase("Find language choice: " + code);
        var item = combo.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, choice))
            ?? throw new InvalidOperationException("Language choice not found: " + choice);
        Phase("Select language choice: " + code);
        ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        Phase("Collapse language choices: " + code);
        ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
        Phase("Save language: " + code);
        Invoke(ById(settings!, "SaveLanguage"));
        Wait(() => ReadState(statePath).GetProperty("preferences").GetProperty("language").GetString() == code, "Language save");
        Phase("Close settings: " + code);
        ((WindowPattern)settings!.GetCurrentPattern(WindowPattern.Pattern)).Close();
        Wait(() => panel.Current.IsEnabled, "Settings close");
    }

    private static void CheckModules(Process process, string executable, List<object> modules)
    {
        process.Refresh();
        var root = Path.GetDirectoryName(executable)! + Path.DirectorySeparatorChar;
        foreach (var name in new[] { "coreclr.dll", "hostfxr.dll", "PresentationFramework.dll" })
        {
            var module = process.Modules.Cast<ProcessModule>().Single(m => m.ModuleName.Equals(name, StringComparison.OrdinalIgnoreCase));
            Require(module.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase), "Runtime loaded outside package: " + module.FileName);
            modules.Add(new { Process = process.Id, Module = name, Path = module.FileName, Version = module.FileVersionInfo.ProductVersion });
        }
    }

    private static void CheckDisplays(AutomationElement panel, List<object> displays)
    {
        Phase("Check attached physical monitors");
        var window = (nint)panel.Current.NativeWindowHandle;
        Require(GetWindowRect(window, out var original), "Cannot read original window bounds.");
        var monitors = new List<MonitorInfo>();
        Require(EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info)) monitors.Add(info);
            return true;
        }, 0), "Cannot enumerate displays.");
        Require(monitors.Count > 0, "No physical displays available.");
        try
        {
            foreach (var monitor in monitors)
            {
                Require(SetWindowPos(window, 0, monitor.Work.Left + 12, monitor.Work.Top + 12, 0, 0, 0x15), "Cannot move test window.");
                NativeRect bounds = default;
                Wait(() => GetWindowRect(window, out bounds) && bounds.Left >= monitor.Work.Left && bounds.Top >= monitor.Work.Top &&
                    bounds.Right <= monitor.Work.Right && bounds.Bottom <= monitor.Work.Bottom, "Published window within monitor work area");
                var dpi = GetDpiForWindow(window);
                Require(dpi > 0, "Cannot read published window DPI.");
                displays.Add(new { Monitor = monitor.Monitor.ToString(), WorkArea = monitor.Work.ToString(), Bounds = bounds.ToString(), Dpi = dpi });
            }
        }
        finally { SetWindowPos(window, 0, original.Left, original.Top, 0, 0, 0x15); }
    }

    private static void Stop(Process process)
    {
        var panel = FindPanel(process);
        Phase("Graceful exit");
        Wait(() => panel.Current.IsEnabled, "Main window ready to close");
        ((WindowPattern)panel.GetCurrentPattern(WindowPattern.Pattern)).Close();
        Require(process.WaitForExit(15000) && process.ExitCode == 0, "Published app failed to exit cleanly.");
    }
    private static JsonElement ReadState(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(file);
        return document.RootElement.Clone();
    }
    private static AutomationElement? FindById(AutomationElement parent, string id) => parent.FindFirst(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.AutomationIdProperty, id));
    private static bool HasText(AutomationElement parent, string text) => parent.FindFirst(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.NameProperty, text)) is not null;
    private static AutomationElement ById(AutomationElement parent, string id) => FindById(parent, id)
        ?? throw new InvalidOperationException("Control not found: " + id);
    private static void Invoke(AutomationElement button) => ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    private static void Wait(Func<bool> condition, string label)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(label);
            Thread.Sleep(100);
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Phase(string message) => File.WriteAllText(phasePath, message);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public override readonly string ToString() => $"{Left},{Top},{Right},{Bottom}";
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    private delegate bool MonitorCallback(nint monitor, nint device, nint rectangle, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint device, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
}

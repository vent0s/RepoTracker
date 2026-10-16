using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using RepoTracker.Core.Tracking;
using RepoTracker.Desktop.Windows;

namespace RepoTracker.Desktop;

public static class ThemeManager
{
    private static AppPreferences current = new();
    private static readonly Dictionary<Window, Size> dialogs = [];
    public static void Apply(AppPreferences preferences)
    {
        if (preferences.Theme != current.Theme)
            Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri($"/RepoTracker.Desktop;component/Themes/Palette.{preferences.Theme}.xaml", UriKind.Relative) };
        if (preferences.ScalePercent != current.ScalePercent)
            Application.Current.Resources["UiScaleTransform"] = new ScaleTransform(preferences.ScalePercent / 100.0, preferences.ScalePercent / 100.0);
        var resized = preferences.ScalePercent != current.ScalePercent;
        current = preferences;
        if (resized) foreach (var window in dialogs.Keys.ToArray()) Fit(window);
    }
    public static void PrepareDialog(Window window)
    {
        dialogs.Add(window, new(window.Width, window.Height));
        HwndSource? source = null;
        nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (message is 0x02E0 or 0x007E or 0x001A) window.Dispatcher.BeginInvoke(() => Fit(window));
            return 0;
        }
        window.SourceInitialized += (_, _) => { source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle); source.AddHook(Hook); };
        window.Loaded += (_, _) => Fit(window);
        window.Closed += (_, _) => { dialogs.Remove(window); source?.RemoveHook(Hook); };
    }
    private static void Fit(Window window)
    {
        if (!window.IsLoaded || !dialogs.TryGetValue(window, out var desired)) return;
        var monitor = NativeWindows.GetMonitor(new WindowInteropHelper(window).Handle);
        window.MaxWidth = Math.Max(1, monitor.Work.Width / (monitor.Dpi / 96) - 24);
        window.MaxHeight = Math.Max(1, monitor.Work.Height / (monitor.Dpi / 96) - 24);
        window.Width = Math.Min(desired.Width * current.ScalePercent / 100, window.MaxWidth);
        window.Height = Math.Min(desired.Height * current.ScalePercent / 100, window.MaxHeight);
        NativeWindows.ClampDialog(window);
    }
}

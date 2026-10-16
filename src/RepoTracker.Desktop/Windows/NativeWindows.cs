using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using RepoTracker.Core.Windows;

namespace RepoTracker.Desktop.Windows;

public static class NativeWindows
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; public readonly PixelRect Pixels => new(Left, Top, Right - Left, Bottom - Top); }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    public static void SetAppId(string appId) => Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(appId));

    public static bool ForegroundBelongsToThisProcess()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return pid == Environment.ProcessId;
    }

    public static (PixelRect Monitor, PixelRect Work, double Dpi) GetMonitor(nint window)
    {
        var monitor = MonitorFromWindow(window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new InvalidOperationException("Cannot read monitor work area.");
        var dpi = GetDpiForMonitor(monitor, 0, out var x, out _) == 0 ? x : 96;
        return (info.Monitor.Pixels, info.Work.Pixels, dpi);
    }

    public static PixelRect Bounds(Window window)
    {
        GetWindowRect(new WindowInteropHelper(window).Handle, out var rect);
        return rect.Pixels;
    }

    public static PixelRect Place(Window window, nint monitorWindow, PixelRect? previous = null, double uiScale = 1)
    {
        var monitor = GetMonitor(monitorWindow);
        var placement = PanelPlacement.Place(monitor.Work, monitor.Dpi,
            PanelPlacement.FindTaskbarEdge(monitor.Monitor, monitor.Work), previous, uiScale);
        SetWindowPos(new WindowInteropHelper(window).Handle, 0,
            (int)Math.Round(placement.X), (int)Math.Round(placement.Y),
            (int)Math.Round(placement.Width), (int)Math.Round(placement.Height), 0x14); // NOZORDER | NOACTIVATE
        return placement;
    }

    public static void Activate(Window window)
    {
        window.Activate();
        SetForegroundWindow(new WindowInteropHelper(window).Handle);
    }

    public static void ClampDialog(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var work = GetMonitor(handle).Work;
        var bounds = Bounds(window);
        var width = Math.Min(bounds.Width, work.Width); var height = Math.Min(bounds.Height, work.Height);
        SetWindowPos(handle, 0, (int)Math.Clamp(bounds.X, work.X, work.Right - width),
            (int)Math.Clamp(bounds.Y, work.Y, work.Bottom - height), (int)width, (int)height, 0x14);
    }
}

using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using RepoTracker.Core.Localization;
using RepoTracker.Infrastructure.Runtime;

namespace RepoTracker.Desktop.Windows;

/// <summary>A shell-owned notification icon. Windows and the user control overflow visibility.</summary>
internal sealed class TrayIcon : IDisposable
{
    internal const int CallbackMessage = 0x8002;
    internal const uint IconId = 1;
    private readonly MainWindow panel;
    private readonly SessionLog log;
    private readonly HwndSource source;
    private readonly DispatcherTimer retryTimer;
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private NotifyIconData data;
    private bool disposed, version4, menuOpen;
    private int attempts;
    private uint menuCommand;
    internal nint Handle => source.Handle;

    public TrayIcon(MainWindow panel, SessionLog log)
    {
        this.panel = panel;
        this.log = log;
        // A hidden top-level window receives TaskbarCreated after Explorer restarts.
        // A message-only window would not receive this broadcast.
        source = new HwndSource(new HwndSourceParameters("RepoTracker notification icon")
        {
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80,
            Width = 0, Height = 0
        });
        source.AddHook(WindowMessage);
        data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = source.Handle, Id = IconId,
            Flags = 0x87, Callback = CallbackMessage, Tip = "RepoTracker",
            Info = "", InfoTitle = "", Version = 4
        };
        retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        retryTimer.Tick += Retry;
        Register();
    }

    private void Retry(object? sender, EventArgs e) => Register();

    private void Register()
    {
        if (disposed) return;
        attempts++;
        if (data.Icon == 0)
        {
            var window = new WindowInteropHelper(panel).Handle;
            var icon = SendMessage(window, 0x007F, 2, 0); // WM_GETICON / ICON_SMALL2
            if (icon == 0) icon = SendMessage(window, 0x007F, 0, 0); // ICON_SMALL
            if (icon == 0) icon = SendMessage(window, 0x007F, 1, 0); // ICON_BIG
            data.Icon = CopyIcon(icon);
        }
        // MODIFY also handles a duplicate TaskbarCreated message without adding another icon.
        if (data.Icon != 0 && (Shell_NotifyIcon(0, ref data) || Shell_NotifyIcon(1, ref data)))
        {
            version4 = Shell_NotifyIcon(4, ref data);
            retryTimer.Stop();
            log.Write("tray.registered");
        }
        else if (attempts < 15)
        {
            if (attempts == 1) log.Write(data.Icon == 0 ? "tray.waiting-for-icon" : "tray.waiting-for-shell:" + Marshal.GetLastWin32Error());
            retryTimer.Start();
        }
        else { retryTimer.Stop(); log.Write("tray.registration.failed"); }
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (disposed) return 0;
        // The native menu sends WM_COMMAND; defer actions until its focus guard is released.
        if (message == 0x0111 && lParam == 0 && (wParam.ToInt64() >> 16) == 0 && (uint)wParam is >= 1 and <= 3)
        {
            handled = true;
            if (menuOpen) { menuCommand = (uint)wParam; EndMenu(); }
            else panel.Dispatcher.BeginInvoke(() => ExecuteCommand((uint)wParam));
            return 0;
        }
        if (taskbarCreated != 0 && message == taskbarCreated)
        {
            attempts = 0;
            Register();
        }
        if (message != CallbackMessage) return 0;
        var notification = version4 ? (int)(lParam.ToInt64() & 0xFFFF) : (int)lParam;
        var id = version4 ? (uint)((lParam.ToInt64() >> 16) & 0xFFFF) : (uint)wParam;
        if (id != IconId) return 0;
        handled = true;
        if ((version4 && notification is 0x400 or 0x401) || (!version4 && notification == 0x202))
            panel.Dispatcher.BeginInvoke(() => { if (!disposed) panel.RestoreFromTray(); });
        else if ((version4 && notification == 0x007B) || (!version4 && notification == 0x205))
        {
            var point = new NativePoint { X = (short)(wParam.ToInt64() & 0xFFFF), Y = (short)((wParam.ToInt64() >> 16) & 0xFFFF) };
            if (!version4 || (point.X == -1 && point.Y == -1)) GetCursorPos(out point);
            panel.Dispatcher.BeginInvoke(() => ShowMenu(point));
        }
        return 0;
    }

    private void ShowMenu(NativePoint point)
    {
        if (disposed || menuOpen) return;
        var menu = CreatePopupMenu();
        if (menu == 0) { log.Write("tray.menu.failed"); return; }
        uint command;
        menuCommand = 0;
        menuOpen = true;
        try
        {
            var unavailable = panel.CanUseTrayCommands ? 0u : 1u; // MF_GRAYED
            AppendMenu(menu, 0, 1, Text.Get("Tray.Open"));
            AppendMenu(menu, unavailable, 2, Text.Get("Settings.Window"));
            AppendMenu(menu, 0x800, 0, null); // MF_SEPARATOR
            AppendMenu(menu, unavailable, 3, Text.Get("App.Exit"));
            using (panel.SuspendForTrayMenu())
            {
                SetForegroundWindow(source.Handle);
                TrackPopupMenuEx(menu, 0x2, point.X, point.Y, source.Handle, 0); // RIGHTBUTTON; selection arrives as WM_COMMAND.
                command = menuCommand;
            }
        }
        finally
        {
            DestroyMenu(menu);
            menuOpen = false;
            if (!disposed) PostMessage(source.Handle, 0, 0, 0); // Let the shell dismiss the menu on the next invocation too.
        }
        ExecuteCommand(command);
    }

    private void ExecuteCommand(uint command)
    {
        if (disposed) return;
        log.Write("tray.menu.command:" + command);
        switch (command)
        {
            case 1: panel.RestoreFromTray(); break;
            case 2: panel.ShowSettingsFromTray(); break;
            case 3: if (panel.CanUseTrayCommands) panel.Close(); break;
            default: Shell_NotifyIcon(3, ref data); break; // NIM_SETFOCUS after Escape / dismissal
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        retryTimer.Stop();
        retryTimer.Tick -= Retry;
        if (menuOpen) EndMenu();
        Shell_NotifyIcon(2, ref data);
        source.RemoveHook(WindowMessage);
        source.Dispose();
        if (data.Icon != 0) DestroyIcon(data.Icon);
        log.Write("tray.removed");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint CopyIcon(nint icon);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint parameters);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool EndMenu();
}

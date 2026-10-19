using System.IO;
using System.Runtime.InteropServices;
using RepoTracker.Infrastructure.Runtime;

namespace RepoTracker.Desktop.Windows;

/// <summary>Lets Windows Restart Manager resume an installed instance after an upgrade.</summary>
internal sealed class InstallationSession : IDisposable
{
    // The installer uses this only to prevent uninstalling a running installation.
    // Upgrade shutdown itself is handled gracefully by Windows Restart Manager.
    private readonly Mutex running = new(false, @"Local\Septim.RepoTracker.Installed");

    private InstallationSession(string profile, SessionLog log)
    {
        var quotedProfile = profile.EndsWith('\\') ? profile + "\\" : profile;
        var result = RegisterApplicationRestart($"--profile \"{quotedProfile}\" --minimized", 1 | 2 | 8);
        log.Write(result == 0 ? "installer.restart.registered" : "installer.restart.failed:" + result);
    }

    internal static InstallationSession? Create(string profile, SessionLog log) =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "installed.ini")) ? new(profile, log) : null;

    public void Dispose() => running.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string commandLine, uint flags);
}

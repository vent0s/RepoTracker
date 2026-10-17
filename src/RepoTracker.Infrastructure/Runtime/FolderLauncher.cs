using RepoTracker.Core.Localization;
using System.Diagnostics;
using RepoTracker.Core.Tracking;

namespace RepoTracker.Infrastructure.Runtime;

public sealed class FolderLauncher : IFolderLauncher
{
    public void Open(string path)
    {
        if (!Directory.Exists(path)) throw new TrackerException(Text.Get("Folder.Unavailable"));
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "open" });
    }
}

using System.Diagnostics;
using RepoTracker.Core.Tracking;

namespace RepoTracker.Infrastructure.Runtime;

public sealed class FolderLauncher : IFolderLauncher
{
    public void Open(string path)
    {
        if (!Directory.Exists(path)) throw new TrackerException("目录不存在或无法访问；跟踪记录仍保留，可以移组或解除跟踪。");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "open" });
    }
}

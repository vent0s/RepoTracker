using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RepoTracker.Core.Tracking;

namespace RepoTracker.Infrastructure.Git;

public sealed class GitRepositoryLocator(IGitProcess git) : IRepositoryLocator
{
    public async Task<RepositoryLocation> LocateAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new TrackerException("请选择文件夹，或输入完整的绝对目录路径。");
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath)) throw new TrackerException("目录不存在或无法访问，请检查路径和权限。");
        var result = await git.RunAsync(["--no-optional-locks", "-c", "core.quotepath=false", "-C", fullPath,
            "rev-parse", "--path-format=absolute", "--show-toplevel", "--absolute-git-dir", "--git-common-dir"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new TrackerException("无法识别 Git 工作树。请确认这是普通仓库或 worktree，并检查目录权限及 Git 的可信目录设置。");
        var paths = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
        if (paths.Length != 3 || paths.Any(item => !Path.IsPathFullyQualified(item)))
            throw new TrackerException("Git 返回了无法识别的目录信息，请确认 Git 版本支持 rev-parse --path-format。");
        return await Task.Run(() => new RepositoryLocation(CanonicalDirectory(paths[0]), CanonicalDirectory(paths[1]),
            CanonicalDirectory(paths[2])), cancellationToken).ConfigureAwait(false);
    }

    public static string CanonicalDirectory(string directory)
    {
        using var handle = CreateFile(Path.GetFullPath(directory), 0, 7, 0, 3, 0x02000000, 0);
        if (handle.IsInvalid) throw new IOException("无法解析目录的实际位置。", new Win32Exception(Marshal.GetLastWin32Error()));
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        }
        if (length == 0) throw new IOException("无法解析目录的实际位置。", new Win32Exception(Marshal.GetLastWin32Error()));
        var path = buffer.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return Path.TrimEndingDirectorySeparator(path);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}

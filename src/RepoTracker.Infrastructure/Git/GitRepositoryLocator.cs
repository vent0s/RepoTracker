using RepoTracker.Core.Localization;
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
            throw new TrackerException(Text.Get("Repository.AbsolutePath"));
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath)) throw new TrackerException(Text.Get("Repository.InvalidPath"));
        var result = await git.RunAsync(["--no-optional-locks", "-c", "core.quotepath=false", "-C", fullPath,
            "rev-parse", "--path-format=absolute", "--show-toplevel", "--absolute-git-dir", "--git-common-dir"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new TrackerException(Text.Get("Repository.InvalidWorktree"));
        var paths = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
        if (paths.Length != 3 || paths.Any(item => !Path.IsPathFullyQualified(item)))
            throw new TrackerException(Text.Get("Repository.InvalidGitPath"));
        return await Task.Run(() => new RepositoryLocation(CanonicalDirectory(paths[0]), CanonicalDirectory(paths[1]),
            CanonicalDirectory(paths[2])), cancellationToken).ConfigureAwait(false);
    }

    public static string CanonicalDirectory(string directory)
    {
        using var handle = CreateFile(Path.GetFullPath(directory), 0, 7, 0, 3, 0x02000000, 0);
        if (handle.IsInvalid) throw new IOException(Text.Get("Repository.ResolveFailed"), new Win32Exception(Marshal.GetLastWin32Error()));
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        }
        if (length == 0) throw new IOException(Text.Get("Repository.ResolveFailed"), new Win32Exception(Marshal.GetLastWin32Error()));
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

using RepoTracker.Core.Status;
using RepoTracker.Core.Tracking;

namespace RepoTracker.Infrastructure.Git;

public sealed class GitStatusChecker(IGitProcess git) : ILocalStatusChecker
{
    public async Task<StatusResult> CheckAsync(TrackedRepository repository, CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(repository.WorktreeRoot))
                return new(null, StatusError.DirectoryUnavailable, "目录不存在或无法访问，跟踪记录已保留。");
            // Pin the recorded git directory. A removed .git must never make Git discover an unrelated parent repo.
            var result = await git.RunAsync(["--no-optional-locks", "--no-pager", "-c", "core.fsmonitor=false",
                "--git-dir=" + repository.GitDir, "--work-tree=" + repository.WorktreeRoot, "-C", repository.WorktreeRoot,
                "status", "--porcelain=v2", "--branch", "-z", "--untracked-files=normal", "--ignore-submodules=none", "--ahead-behind"], cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                var error = result.Error;
                if (error.Contains("dubious ownership", StringComparison.OrdinalIgnoreCase) || error.Contains("permission denied", StringComparison.OrdinalIgnoreCase) || error.Contains("access is denied", StringComparison.OrdinalIgnoreCase))
                    return new(null, StatusError.AccessDenied, "Git 拒绝访问，请检查目录权限或可信目录设置。");
                if (error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) || error.Contains("must be run in a work tree", StringComparison.OrdinalIgnoreCase))
                    return new(null, StatusError.NotRepository, "原 Git 工作树已失效，请核对目录；跟踪记录已保留。");
                return new(null, StatusError.Failed, "Git 检查失败，请确认仓库完整且可以访问。");
            }
            var data = PorcelainV2Parser.Parse(result.Output);
            Core.Sources.RemoteIdentity identity;
            try { identity = (await new GitRemoteReader(git).ReadAsync(repository, cancellationToken).ConfigureAwait(false)).Identity; }
            catch (Exception error) when (error is GitExecutionException or FormatException or IOException or UnauthorizedAccessException)
            { identity = new(false, ReadFailed: true); }
            return new(data, Remote: identity);
        }
        catch (OperationCanceledException) { throw; }
        catch (GitExecutionException error) { return new(null, error.Error, error.Message); }
        catch (FormatException) { return new(null, StatusError.InvalidOutput, "Git 输出不完整或格式不受支持，未显示为干净状态。"); }
        catch (UnauthorizedAccessException) { return new(null, StatusError.AccessDenied, "没有读取仓库的权限。"); }
        catch (IOException) { return new(null, StatusError.Failed, "读取仓库失败，请检查磁盘与目录。"); }
    }
}

using RepoTracker.Core.Status;
using RepoTracker.Core.Tracking;

namespace RepoTracker.Infrastructure.Git;

public sealed class GitRemoteChecker(IGitProcess git, GitRemoteReader reader) : IRemoteChecker
{
    public async Task<RemoteResult> CheckAsync(TrackedRepository repo, CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(repo.WorktreeRoot) || !Directory.Exists(repo.GitDir)) return new(RemoteOutcome.Skipped, "目录不可达，未执行 fetch。");
            var config = await reader.ReadAsync(repo, cancellationToken).ConfigureAwait(false);
            if (config.SkipReason is { } reason) return new(RemoteOutcome.Skipped, reason);
            // A destination symref could otherwise redirect an allowed remote-tracking update into refs/heads.
            var prefix = "refs/remotes/" + config.Identity.RemoteName + "/";
            var destinations = await git.RunAsync(["--git-dir=" + repo.GitDir, "for-each-ref", "--format=%(refname)%09%(symref)", prefix], cancellationToken).ConfigureAwait(false);
            if (destinations.ExitCode != 0 || destinations.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(line =>
            {
                var pair = line.TrimEnd('\r').Split('\t');
                return pair.Length != 2 || pair[1].Length > 0 && pair[0] != prefix + "HEAD";
            })) return new(RemoteOutcome.Skipped, "远程跟踪引用含不受支持的符号引用，已跳过 fetch。");
            cancellationToken.ThrowIfCancellationRequested();
            var arguments = new List<string> { "--no-optional-locks", "--no-pager", "--git-dir=" + repo.GitDir, "--work-tree=" + repo.WorktreeRoot, "-C", repo.WorktreeRoot,
                "-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=false", "-c", "credential.helper=", "-c", "credential.helper=manager",
                "-c", "credential.interactive=false", "-c", "core.askPass=", "-c", "protocol.allow=never", "-c", "protocol.https.allow=always", "-c", "protocol.ssh.allow=always",
                "-c", "http.followRedirects=false", "-c", "fetch.prune=false", "-c", "fetch.pruneTags=false",
                "fetch", "--atomic", "--no-tags", "--no-prune", "--no-prune-tags", "--no-recurse-submodules", "--no-write-fetch-head", "--no-auto-maintenance", "--no-write-commit-graph",
                "--refmap=", "--upload-pack=git-upload-pack", "--", config.Url! };
            arguments.AddRange(config.Refspecs);
            arguments.Add("^refs/heads/HEAD"); // Never write the conventional origin/HEAD symbolic reference.
            var result = await git.RunAsync(arguments, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0) return new(RemoteOutcome.Success, "远程检查成功；本地状态正在重新读取。");
            var error = result.Error;
            if (new[] { "authentication", "permission denied", "terminal prompts disabled", "could not read Username", "host key verification", "interactive" }.Any(value => error.Contains(value, StringComparison.OrdinalIgnoreCase)))
                return new(RemoteOutcome.Authentication, "认证或 SSH 主机校验失败；请先在应用外完成凭据配置。");
            if (new[] { "could not resolve", "unable to access", "connection", "network", "timed out" }.Any(value => error.Contains(value, StringComparison.OrdinalIgnoreCase)))
                return new(RemoteOutcome.Offline, "网络不可用或远端无法连接；保留上次成功信息。");
            return new(RemoteOutcome.Failed, "fetch 失败；请在应用外检查远端与仓库配置。");
        }
        catch (OperationCanceledException) { throw; }
        catch (GitExecutionException error) { return new(error.Error == StatusError.Timeout ? RemoteOutcome.Timeout : RemoteOutcome.Failed,
            error.Error == StatusError.Timeout ? "远程检查超时，已终止 Git。" : "Git 不可用，未完成远程检查。"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
        { return new(RemoteOutcome.Failed, "无法读取远程配置；本地检查继续运行。"); }
    }
}

using RepoTracker.Core.Localization;
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
                return new(null, StatusError.DirectoryUnavailable, Text.Get("Status.MissingFolder"));
            // Pin the recorded git directory. A removed .git must never make Git discover an unrelated parent repo.
            var result = await git.RunAsync(["--no-optional-locks", "--no-pager", "-c", "core.fsmonitor=false",
                "--git-dir=" + repository.GitDir, "--work-tree=" + repository.WorktreeRoot, "-C", repository.WorktreeRoot,
                "status", "--porcelain=v2", "--branch", "-z", "--untracked-files=normal", "--ignore-submodules=none", "--ahead-behind"], cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                var error = result.Error;
                if (error.Contains("dubious ownership", StringComparison.OrdinalIgnoreCase) || error.Contains("permission denied", StringComparison.OrdinalIgnoreCase) || error.Contains("access is denied", StringComparison.OrdinalIgnoreCase))
                    return new(null, StatusError.AccessDenied, Text.Get("Status.GitDenied"));
                if (error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) || error.Contains("must be run in a work tree", StringComparison.OrdinalIgnoreCase))
                    return new(null, StatusError.NotRepository, Text.Get("Status.NotWorktree"));
                return new(null, StatusError.Failed, Text.Get("Status.GitFailed"));
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
        catch (FormatException) { return new(null, StatusError.InvalidOutput, Text.Get("Status.InvalidOutput")); }
        catch (UnauthorizedAccessException) { return new(null, StatusError.AccessDenied, Text.Get("Status.NoPermission")); }
        catch (IOException) { return new(null, StatusError.Failed, Text.Get("Status.ReadFailed")); }
    }
}

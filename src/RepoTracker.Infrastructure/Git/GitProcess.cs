using RepoTracker.Core.Localization;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using RepoTracker.Core.Tracking;
using RepoTracker.Core.Status;

namespace RepoTracker.Infrastructure.Git;

public sealed record GitResult(int ExitCode, string Output, string Error);
public sealed class GitExecutionException(StatusError error, string message) : TrackerException(message)
{
    public StatusError Error { get; } = error;
}
public interface IGitProcess
{
    Task<GitResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

public sealed class GitProcess(string executable = "git", TimeSpan? timeout = null, bool remote = false) : IGitProcess
{
    public async Task<GitResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GCM_INTERACTIVE"] = "Never";
        if (remote)
        {
            info.Environment["GIT_ASKPASS"] = "false";
            info.Environment["SSH_ASKPASS"] = "false";
            info.Environment["SSH_ASKPASS_REQUIRE"] = "never";
            info.Environment["GCM_GUI_PROMPT"] = "false";
            info.Environment["GCM_DEBUG"] = "0";
            info.Environment["GCM_TRACE"] = "0";
            info.Environment["GCM_TRACE_SECRETS"] = "0";
            info.Environment["GIT_SSH_VARIANT"] = "ssh";
            info.Environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes -o StrictHostKeyChecking=yes -o ConnectTimeout=10";
        }
        info.Environment["GIT_NO_LAZY_FETCH"] = "1";
        info.Environment["LC_ALL"] = "C";
        info.Environment["LANG"] = "C";
        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (Win32Exception) { throw new GitExecutionException(StatusError.GitUnavailable, Text.Get("Git.StartFailed")); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return new(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new GitExecutionException(StatusError.Timeout, Text.Get("Git.Timeout"));
        }
    }
}

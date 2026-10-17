using System.Diagnostics;
using System.Text;
using RepoTracker.Core.Tracking;
using RepoTracker.Infrastructure.Git;
using RepoTracker.Tests.Shared;

namespace RepoTracker.IntegrationTests;

public sealed class GitProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutAndCancellationReapAnOwnedProcess(bool cancel)
    {
        var area = new TestWorkspace(); var pidPath = Path.Combine(area.Root, "child.pid");
        var quotedPath = pidPath.Replace("'", "''");
        var script = "[IO.File]::WriteAllText('" + quotedPath + ".pending', [string]$PID); [IO.File]::Move('" + quotedPath + ".pending', '" + quotedPath + "'); Start-Sleep -Seconds 30";
        var arguments = new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) };
        using var cancellation = new CancellationTokenSource();
        var process = new GitProcess("powershell.exe", TimeSpan.FromSeconds(cancel ? 20 : 3));
        var operation = process.RunAsync(arguments, cancellation.Token);
        try
        {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(pidPath) && !operation.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(40);
        Assert.True(File.Exists(pidPath), "Owned fixture process must have started before the cancellation check.");
        using var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidPath)));
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        }
        else
        {
            var error = await Assert.ThrowsAsync<GitExecutionException>(() => operation);
            Assert.Equal(RepoTracker.Core.Localization.Text.Get("Git.Timeout"), error.Message);
        }
        Assert.True(child.HasExited, "Timed out or cancelled child process was not reaped.");
        }
        finally
        {
            cancellation.Cancel();
            try { await operation; }
            catch (OperationCanceledException) { }
            catch (GitExecutionException) { }
        }
    }
}

using System.Diagnostics;
using Xunit.Abstractions;

namespace RepoTracker.Desktop.Tests;

public sealed class WindowLifecycleTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Desktop")]
    public async Task RealWindowsRespectTaskbarFocusAndSingleInstanceContracts()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "RepoTracker.slnx"))) root = root.Parent;
        Assert.NotNull(root);
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var run = Path.Combine(root.FullName, "tests", "artifacts", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(run);
        var start = new ProcessStartInfo(Path.Combine(root.FullName, "tests", "RepoTracker.Desktop.TestHost", "bin", configuration, "net10.0-windows", "RepoTracker.Desktop.TestHost.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = root.FullName
        };
        start.ArgumentList.Add("--profile");
        start.ArgumentList.Add(Path.Combine(run, "profile"));
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(240));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        var report = Path.Combine(run, "report.json");
        output.WriteLine("Evidence: " + run);
        var details = File.Exists(report) ? await File.ReadAllTextAsync(report) : "No report generated.";
        output.WriteLine(details);
        Assert.True(process.ExitCode == 0, $"Exit code: {process.ExitCode}. {details}");
        Assert.True(File.Exists(Path.Combine(run, "main.png")));
        Assert.True(File.Exists(Path.Combine(run, "settings.png")));
        var logs = Directory.GetFiles(Path.Combine(run, "profile", "logs"), "*.log");
        Assert.Contains(logs, file => File.ReadAllText(file).Contains("application.stopped"));
        Assert.Contains(logs, file => File.ReadAllText(file).Contains("secondary.activation.sent"));
        Assert.True(File.Exists(Path.Combine(run, "managed-repositories.png")));
        Assert.True(File.Exists(Path.Combine(run, "local-status.png")));
        Assert.True(File.Exists(Path.Combine(run, "local-error.png")));
        Assert.True(File.Exists(Path.Combine(run, "source-rules.png")));
        Assert.True(File.Exists(Path.Combine(run, "remote-settings.png")));
        Assert.True(File.Exists(Path.Combine(run, "sources-panel.png")));
        Assert.True(File.Exists(Path.Combine(run, "main-light-long-list.png")));
        Assert.True(File.Exists(Path.Combine(run, "main-compact-150.png")));
        Assert.True(File.Exists(Path.Combine(run, "preferences-save-failure.png")));
        Assert.Contains(logs, file => File.ReadAllText(file).Contains("status.stopped"));
        start.Environment["REPOTRACKER_TEST_RELOAD"] = "1";
        using var restart = Process.Start(start)!;
        using var restartTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await restart.WaitForExitAsync(restartTimeout.Token); }
        finally { if (!restart.HasExited) { restart.Kill(entireProcessTree: true); await restart.WaitForExitAsync(); } }
        var reloadReport = Path.Combine(run, "reload-report.json");
        var reloadDetails = File.Exists(reloadReport) ? await File.ReadAllTextAsync(reloadReport) : "No restart report generated.";
        output.WriteLine(reloadDetails);
        Assert.True(restart.ExitCode == 0, reloadDetails);
        Assert.True(File.Exists(Path.Combine(run, "restart.png")));
        start.Environment.Remove("REPOTRACKER_TEST_RELOAD");
        start.Environment["REPOTRACKER_TEST_MINIMIZED"] = "1";
        start.ArgumentList.Add("--minimized");
        using var login = Process.Start(start)!;
        using var loginTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await login.WaitForExitAsync(loginTimeout.Token); }
        finally { if (!login.HasExited) { login.Kill(entireProcessTree: true); await login.WaitForExitAsync(); } }
        var loginReport = Path.Combine(run, "minimized-report.json");
        var loginDetails = File.Exists(loginReport) ? await File.ReadAllTextAsync(loginReport) : "No minimized report generated.";
        output.WriteLine(loginDetails); Assert.True(login.ExitCode == 0, loginDetails);
    }
}

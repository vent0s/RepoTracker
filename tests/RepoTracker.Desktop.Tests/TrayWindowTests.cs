using System.Diagnostics;
using Xunit.Abstractions;

namespace RepoTracker.Desktop.Tests;

[Collection("Desktop")]
public sealed class TrayWindowTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("zh-CN", true)]
    [InlineData("en-US", false)]
    public async Task RealTraySupportsActivationMenusRecoveryAndExit(string language, bool minimized)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "RepoTracker.slnx"))) root = root.Parent;
        Assert.NotNull(root);
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var run = Path.Combine(root.FullName, "tests", "artifacts", "tray-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        var start = new ProcessStartInfo(Path.Combine(root.FullName, "tests", "RepoTracker.Desktop.TestHost", "bin", configuration, "net10.0-windows", "RepoTracker.Desktop.TestHost.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root.FullName };
        start.ArgumentList.Add("--profile"); start.ArgumentList.Add(Path.Combine(run, "profile"));
        start.Environment["REPOTRACKER_TEST_TRAY"] = "1";
        start.Environment["REPOTRACKER_TEST_SYSTEM_LANGUAGE"] = language;
        if (minimized)
        {
            start.ArgumentList.Add("--minimized");
            start.Environment["REPOTRACKER_TEST_MINIMIZED"] = "1";
        }
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        var report = Path.Combine(run, minimized ? "minimized-report.json" : "report.json");
        var details = File.Exists(report) ? await File.ReadAllTextAsync(report) : "No report";
        output.WriteLine(run + "\n" + details);
        Assert.True(process.ExitCode == 0, details);
        Assert.True(File.Exists(report), details);
        Assert.True(File.Exists(Path.Combine(run, "tray-settings.png")));
        var logs = Directory.GetFiles(Path.Combine(run, "profile", "logs"), "*.log");
        Assert.Contains(logs, file => File.ReadAllText(file).Contains("tray.removed"));
        Assert.Contains(logs, file => File.ReadAllText(file).Contains("application.stopped"));
    }
}
